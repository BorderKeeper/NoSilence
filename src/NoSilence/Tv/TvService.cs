using Microsoft.Extensions.Logging;
using NoSilence.Detection;
using NoSilence.Settings;
using NoSilence.Tv.Samsung;

namespace NoSilence.Tv;

/// <summary>
/// Connects the pure <see cref="TvPolicy"/> to real events and real hardware.
/// </summary>
/// <remarks>
/// Evaluated on a slow cadence, never on the audio tick: power commands are measured in
/// minutes and there is nothing to gain from asking four times a second.
/// <para>
/// All hardware work happens on the thread pool. The engine thread must never block on a
/// network round trip — a Wake-on-LAN wait is up to 45 seconds, which would stall detection
/// and the device state machine completely.
/// </para>
/// </remarks>
internal sealed class TvService : IDisposable
{
    private const int EvaluateEveryMs = 5000;

    private readonly SettingsService _settings;
    private readonly StateService _state;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TvService> _log;

    private IDisplayController _controller = new NullDisplayController();
    private Func<bool> _endpointPresent = () => false;
    private Func<bool> _wantsToPlay = () => false;
    private Func<bool> _libraryHasTracks = () => false;
    private Func<OverrideState> _override = () => OverrideState.Auto;

    private long _nextEvaluationAt;
    private int _busy;
    private DateTimeOffset _sleepCommandSentAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public TvService(SettingsService settings, StateService state, ILoggerFactory loggerFactory, ILogger<TvService> log)
    {
        _settings = settings;
        _state = state;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    public IDisplayController Controller => _controller;

    public TvPolicyState PolicyState => _state.Current.TvPolicy;

    /// <summary>Human-readable status for the tray and the settings window.</summary>
    public string Status { get; private set; } = "Television control is off.";

    public event EventHandler<DisplayEvent>? Diagnostic;

    public void Configure(
        Func<bool> endpointPresent,
        Func<bool> wantsToPlay,
        Func<bool> libraryHasTracks,
        Func<OverrideState> currentOverride)
    {
        _endpointPresent = endpointPresent;
        _wantsToPlay = wantsToPlay;
        _libraryHasTracks = libraryHasTracks;
        _override = currentOverride;

        RebuildController();
    }

    public void RebuildController()
    {
        IDisplayController previous = _controller;
        _controller = Create(_settings.Current.Tv);
        _controller.Diagnostic += OnDiagnostic;

        if (!ReferenceEquals(previous, _controller))
        {
            previous.Diagnostic -= OnDiagnostic;
            _ = previous.DisposeAsync().AsTask();
        }

        Status = _controller.Id == "none" ? "Television control is off." : $"Using {_controller.DisplayName}.";
        _log.LogInformation("Television control: {Controller}.", _controller.DisplayName);
    }

    private IDisplayController Create(TvSettings settings)
    {
        bool Present() => _endpointPresent();

        return settings.Provider?.ToLowerInvariant() switch
        {
            "samsung" when !string.IsNullOrWhiteSpace(settings.Host) => new SamsungTvController(
                settings,
                _state.Current.SamsungToken,
                Present,
                token =>
                {
                    _state.Current.SamsungToken = token;
                    _state.Save();
                },
                _loggerFactory.CreateLogger<SamsungTvController>()),

            "wol" => new WakeOnLanDisplayController(settings, Present, _loggerFactory.CreateLogger<WakeOnLanDisplayController>()),
            "shell" => new ShellCommandDisplayController(settings, Present, _loggerFactory.CreateLogger<ShellCommandDisplayController>()),
            _ => new NullDisplayController(),
        };
    }

    /// <summary>Called from the engine tick; throttles itself and never blocks.</summary>
    public void Tick()
    {
        if (_disposed || _controller.Capabilities == DisplayCapabilities.None)
        {
            return;
        }

        if (Environment.TickCount64 < _nextEvaluationAt)
        {
            return;
        }

        _nextEvaluationAt = Environment.TickCount64 + EvaluateEveryMs;

        TvSettings settings = _settings.Current.Tv;
        OverrideState state = _override();

        var input = new TvPolicyInput(
            DateTimeOffset.Now,
            _wantsToPlay(),
            _endpointPresent(),
            _libraryHasTracks(),
            state.Mode,
            state.IsSnoozed(DateTimeOffset.Now),
            _controller.Capabilities);

        TvAction action = TvPolicy.Decide(input, settings.Policy, _state.Current.TvPolicy);
        if (action == TvAction.None)
        {
            return;
        }

        Execute(action, automatic: true);
    }

    /// <summary>Wakes the television now, clearing any veto. The tray's "Wake TV".</summary>
    public void WakeNow()
    {
        TvPolicy.ClearVeto(_state.Current.TvPolicy);
        _state.Save();
        Execute(TvAction.Wake, automatic: false);
    }

    public void SleepNow() => Execute(TvAction.Sleep, automatic: false);

    public void SendVolume(VolumeCommand command) => RunInBackground(async ct =>
    {
        await _controller.SendVolumeAsync(command, ct).ConfigureAwait(false);
    });

    public Task<bool> PairAsync(CancellationToken ct) =>
        _controller is SamsungTvController samsung ? samsung.PairAsync(ct) : Task.FromResult(false);

    public Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct) => _controller.GetPowerStateAsync(ct);

    private void Execute(TvAction action, bool automatic)
    {
        RunInBackground(async ct =>
        {
            TvPolicy.RecordPowerCommand(DateTimeOffset.Now, _state.Current.TvPolicy);

            if (action == TvAction.Wake)
            {
                _log.LogInformation("Waking the television ({Trigger}).", automatic ? "automatically" : "on request");
                bool ok = await _controller.WakeAsync(ct).ConfigureAwait(false);
                _state.Current.TvPolicy.WeWokeIt = ok;
                Status = ok ? "The television is awake." : "The television did not respond to a wake.";
            }
            else
            {
                _log.LogInformation("Turning the television off ({Trigger}).", automatic ? "automatically" : "on request");

                // Suppress the user-veto rule for the endpoint loss we are about to cause.
                // Recorded as a timestamp rather than held as a flag across a delay: doing
                // the latter kept the single-operation guard locked for ten seconds, during
                // which a "turn it on" click was silently dropped.
                _sleepCommandSentAt = DateTimeOffset.Now;

                bool ok = await _controller.SleepAsync(ct).ConfigureAwait(false);
                _state.Current.TvPolicy.WeWokeIt = false;
                Status = ok ? "The television has been turned off." : "The television could not be turned off.";
            }

            _state.Save();
        });
    }

    /// <summary>
    /// The output device vanished. If we did not cause it, the user switched the television
    /// off by hand — so stop trying to wake it, or the two of you will fight over it.
    /// </summary>
    public void NoteOutputDeviceLost()
    {
        // Endpoint removal follows our own power-off by a second or two, and that must not
        // be mistaken for the user switching the set off by hand.
        bool weCausedIt = DateTimeOffset.Now - _sleepCommandSentAt < TimeSpan.FromSeconds(20);

        if (weCausedIt || _controller.Capabilities == DisplayCapabilities.None)
        {
            return;
        }

        TvPolicyConfig config = _settings.Current.Tv.Policy;
        TvPolicy.NoteUnexpectedDisappearance(DateTimeOffset.Now, config, _state.Current.TvPolicy);
        _state.Save();

        Status = $"Television wake paused until {_state.Current.TvPolicy.UserVetoUntil?.LocalDateTime:HH:mm} (you turned it off).";
        _log.LogInformation("{Status}", Status);
        Diagnostic?.Invoke(this, new DisplayEvent(Status));
    }

    /// <summary>
    /// One operation at a time, on the thread pool. A wake can take 45 seconds; overlapping
    /// them would let the hourly circuit breaker be bypassed.
    /// </summary>
    private void RunInBackground(Func<CancellationToken, Task> work)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            // Visible rather than silent: a dropped button press with no feedback looks
            // exactly like a broken app.
            const string Message = "Another television command is still running; ignoring this one.";
            _log.LogWarning(Message);
            Status = Message;
            Diagnostic?.Invoke(this, new DisplayEvent(Message, IsError: true));
            return;
        }

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await work(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Television operation failed.");
                Diagnostic?.Invoke(this, new DisplayEvent($"Failed: {ex.Message}", IsError: true));
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private void OnDiagnostic(object? sender, DisplayEvent e)
    {
        Status = e.Message;
        Diagnostic?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.Diagnostic -= OnDiagnostic;
        _ = _controller.DisposeAsync().AsTask();
        _state.Save();
    }
}
