using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NoSilence.Audio;
using NoSilence.Signals;

namespace NoSilence.Detection;

/// <summary>
/// Assembles a <see cref="DetectionSnapshot"/> each tick and runs it through the pure
/// <see cref="DecisionEngine"/>.
/// </summary>
/// <remarks>
/// The split matters: everything that touches COM or Win32 lives here, and everything that
/// makes a judgement lives in the engine. That is what allows a recorded snapshot stream to
/// be replayed through the same logic offline.
/// <para>Runs entirely on the audio engine thread.</para>
/// </remarks>
internal sealed class DetectionService : IDisposable
{
    /// <summary>Transitions in an hour that mean the rules or the threshold need attention.</summary>
    private const int FlapThreshold = 20;

    private readonly AudioSessionProbe _sessions;
    private readonly DeviceCatalog _catalog;
    private readonly SignalProbes _signals;
    private readonly ILogger<DetectionService> _log;
    private readonly DecisionState _state = new();

    private DetectionConfig _config = new();
    private OverrideState _override = OverrideState.Auto;
    private DecisionOutcome? _last;
    private ShellActivity _lastShell = ShellActivity.Unknown;
    private bool _disposed;

    public DetectionService(
        AudioSessionProbe sessions,
        DeviceCatalog catalog,
        SignalProbes signals,
        ILogger<DetectionService> log)
    {
        _sessions = sessions;
        _catalog = catalog;
        _signals = signals;
        _log = log;
    }

    /// <summary>Raised on the engine thread every tick, with the decision and what produced it.</summary>
    public event EventHandler<(DecisionOutcome Outcome, DetectionSnapshot Snapshot)>? Decided;

    /// <summary>Raised on the engine thread when play/silence has been oscillating.</summary>
    public event EventHandler<int>? Flapping;

    /// <summary>The most recent decision, for the tray and the settings window.</summary>
    public DecisionOutcome? LastOutcome => _last;

    public DetectionConfig Config => _config;

    public void Configure(DetectionConfig config)
    {
        _config = config;
        _state.Reset();
    }

    public OverrideState Override
    {
        get => _override;
        set
        {
            _override = value;
            _log.LogInformation("Operating mode is now {Mode}{Snooze}.", value.Mode,
                value.SnoozeUntil is { } until ? $" (snoozed until {until.LocalDateTime:HH:mm})" : string.Empty);
        }
    }

    /// <summary>Forces the session list to be rebuilt on the next tick.</summary>
    public void InvalidateSessions() => _sessions.Invalidate();

    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        DetectionSnapshot snapshot = Capture();
        string? callBefore = _state.CallApp;
        string? exhaustedBefore = _state.ExhaustedCallSessionId;
        DecisionOutcome outcome = DecisionEngine.Evaluate(snapshot, _config, _state);

        // The call safety net is meant almost never to fire: a conferencing client closes its
        // microphone when the meeting ends, and that is what normally ends a call. One that
        // holds the microphone open instead is exactly the thing this bound exists for, and
        // the only way to find out which clients do it is to have said so in the log. Logged
        // on the edge, or it would repeat four times a second for as long as the client sat
        // there with the microphone open.
        if (exhaustedBefore is null && _state.ExhaustedCallSessionId is not null)
        {
            _log.LogInformation(
                "The call on {App} produced nothing for {Minutes:F0} min, so it is being treated as over even though the microphone is still open.",
                callBefore ?? "an unknown application",
                _config.CallIdleTimeoutMs / 60000d);
        }

        // "Play through this call" expires with the call it was aimed at. An override that
        // outlived its call would be indistinguishable from the microphone signal being
        // switched off, and would be discovered the same way — days later, by accident.
        if (_override.PlayThroughCall && _state.CallApp is null)
        {
            _override = _override with { PlayThroughCall = false };
            _log.LogInformation("The call ended, so playing through it has been turned off again.");
        }

        if (_last is null || _last.WantsSilence != outcome.WantsSilence || _last.Phase != outcome.Phase)
        {
            _log.LogInformation("{Summary}", DecisionEngine.Summarise(outcome));

            // Automatic flap detection, so oscillation surfaces without anyone watching for
            // it. Twenty an hour means the threshold or a rule needs attention. The latch
            // that makes it once an hour lives on the state, where it can be tested.
            if (_state.ShouldReportFlapping(FlapThreshold))
            {
                _log.LogWarning(
                    "Play/silence has flipped {Count} times in the last hour, which suggests the detection threshold or a rule needs adjusting. Try --diagnose.",
                    _state.TransitionsThisHour);

                Flapping?.Invoke(this, _state.TransitionsThisHour);
            }
        }

        _last = outcome;
        Decided?.Invoke(this, (outcome, snapshot));
    }

    /// <summary>Builds a snapshot. Public so <c>--diagnose</c> can record without deciding.</summary>
    public DetectionSnapshot Capture()
    {
        IReadOnlyList<SessionObservation> render = _sessions.Sample(DataFlow.Render);
        IReadOnlyList<SessionObservation> capture = _config.MicrophoneSignal
            ? _sessions.Sample(DataFlow.Capture)
            : [];

        (bool muted, float volume) = ReadDefaultEndpointVolume();

        ShellActivity shell = _config.FullscreenSignal || _config.FocusAssistSignal
            ? _signals.ReadShellActivity()
            : ShellActivity.Unknown;

        // Logged on change because SHQueryUserNotificationState is hard to reason about from
        // the outside: it reported Busy through nineteen ducks in one ordinary working day,
        // with nothing obviously full-screen running. Knowing when it flips, and what was in
        // the foreground at the time, is the only way to find out what is asserting it. It
        // changes rarely, so this costs a handful of lines a day.
        if (shell != _lastShell)
        {
            _log.LogInformation("Shell activity is now {Shell} (was {Previous}).", shell, _lastShell);
            _lastShell = shell;
        }

        return new DetectionSnapshot(
            At: DateTimeOffset.Now,
            Render: render,
            Capture: capture,
            OutputEndpointPresent: true,
            DefaultEndpointMuted: muted,
            DefaultEndpointVolume: volume,
            Shell: shell,
            UserIdle: _config.SilenceWhenIdleMinutes > 0 ? _signals.ReadUserIdle() : TimeSpan.Zero,
            WorkstationLocked: _signals.WorkstationLocked,
            Override: _override);
    }

    /// <summary>
    /// The endpoint the user actually listens on. If it is muted or at zero, nothing playing
    /// through it is audible, so nothing on it should silence our music.
    /// </summary>
    private (bool Muted, float Volume) ReadDefaultEndpointVolume()
    {
        try
        {
            using MMDevice? device = _catalog.TryGetDefault();
            if (device is null)
            {
                return (false, 1f);
            }

            AudioEndpointVolume endpoint = device.AudioEndpointVolume;
            return (endpoint.Mute, endpoint.MasterVolumeLevelScalar);
        }
        catch (COMException)
        {
            return (false, 1f);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.Dispose();
    }
}
