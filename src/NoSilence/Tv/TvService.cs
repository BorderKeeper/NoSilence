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

    /// <summary>
    /// How long the television's own account of itself is trusted. Two minutes: it is asked
    /// once at launch and acted on within seconds, and a report older than this could predate
    /// somebody picking up the remote.
    /// </summary>
    private const int PowerReportValidForMs = 120000;

    /// <summary>
    /// How many times to ask before giving up and letting the audio endpoint decide. More than
    /// one because a logon races the network coming up, and an unreachable television five
    /// seconds after launch is not an answer.
    /// </summary>
    private const int PowerQueryAttempts = 4;

    /// <summary>
    /// How long the output endpoint must stay gone before we believe the user switched the
    /// television off by hand.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds, because the endpoint flaps. On the morning of 10 August it went
    /// Unplugged and came back inside five seconds — twice — and each flap was recorded as
    /// "you turned it off", which suppressed waking for the following hour. The one thing this
    /// rule must never do is fire when nobody touched anything, so the loss is confirmed before
    /// it counts.
    /// </remarks>
    private const int VetoConfirmMs = 15000;

    private readonly WakeWatch _wake = new();
    private readonly SettingsService _settings;
    private readonly StateService _state;
    private readonly Signals.SignalProbes _probes;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TvService> _log;

    private IDisplayController _controller = new NullDisplayController();
    private Func<bool> _endpointPresent = () => false;
    private Func<bool> _wantsToPlay = () => false;
    private Func<bool> _libraryHasTracks = () => false;
    private Func<OverrideState> _override = () => OverrideState.Auto;

    private long _nextEvaluationAt;
    private int _busy;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private DateTimeOffset _sleepCommandSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _endpointLostAt;
    private string _windowTrigger = "at startup";
    private int _powerQueryStarted;
    private int _powerQueryAttempts;
    private PowerReport? _power;
    private bool _disposed;

    /// <summary>What the television last said about itself, and when.</summary>
    /// <remarks>
    /// Assigned as a whole object so the engine thread never sees half of it: the query runs
    /// on the thread pool and the policy reads it from the tick.
    /// </remarks>
    private sealed record PowerReport(DisplayPowerState State, DateTimeOffset At);

    public TvService(
        SettingsService settings,
        StateService state,
        Signals.SignalProbes probes,
        ILoggerFactory loggerFactory,
        ILogger<TvService> log)
    {
        _settings = settings;
        _state = state;
        _probes = probes;
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

        // Called once, after the persisted state has been loaded, so this is where "now" is
        // the moment the app started rather than the moment the object was constructed.
        _startedAt = DateTimeOffset.Now;
        _windowTrigger = "at startup";
        TvPolicy.BeginSession(_state.Current.TvPolicy);

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
        DateTimeOffset now = DateTimeOffset.Now;
        bool endpointPresent = _endpointPresent();

        // Before anything else: did the machine just come back? A wake is treated exactly like
        // a launch, because from the television's point of view it is one. Observed even when the
        // setting is off, so that turning it on mid-session does not inherit a stale baseline.
        WakeReason wake = _wake.Observe(now, endpointPresent, _probes.ReadUserIdle());

        if (wake != WakeReason.None)
        {
            // Outside the WakeAtStartup guard on purpose: whether the startup wake is switched
            // on has nothing to do with whether an endpoint that vanished while the machine was
            // away was the user reaching for the remote. See NoteAwake.
            _endpointLostAt = null;

            if (settings.Policy.WakeAtStartup)
            {
                NoteAwake(now, wake switch
                {
                    WakeReason.ClockJumped => "the clock jumped, so the machine was suspended",
                    WakeReason.OutputReturned => "the output device came back after a long absence",
                    _ => "input arrived after a long silence",
                });
            }
        }

        ConfirmOrForgetAManualPowerOff(now, endpointPresent);

        bool atStartup = (now - _startedAt).TotalMilliseconds < settings.Policy.StartupWindowMs;

        if (atStartup && settings.Policy.WakeAtStartup)
        {
            AskTheTelevisionAtStartup();
        }

        var input = new TvPolicyInput(
            now,
            _wantsToPlay(),
            endpointPresent,
            _libraryHasTracks(),
            state.Mode,
            state.IsSnoozed(now),
            _controller.Capabilities,
            _startedAt,
            atStartup ? FreshPowerReport(now) : null);

        TvAction action = TvPolicy.Decide(input, settings.Policy, _state.Current.TvPolicy);
        if (action == TvAction.None)
        {
            return;
        }

        // Which of the two rules fired is the difference between "it did that at logon" and
        // "it did that in the middle of my afternoon", so the log has to distinguish them.
        Execute(action, atStartup ? _windowTrigger : "automatically");
    }

    /// <summary>
    /// Windows says it resumed from sleep. Keep it — it is free when it arrives — but do not
    /// depend on it: on this machine it has never arrived once. See <see cref="WakeWatch"/>.
    /// </summary>
    public void NoteResumedFromSleep() => NoteAwake(DateTimeOffset.Now, "Windows reported a resume");

    /// <summary>
    /// Treats the machine coming back as a fresh launch, so the startup wake applies to it.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the exercise. A wake is the moment somebody sits down at the
    /// machine, which is precisely when they want the television on, and it is a moment the
    /// original startup window could not see: <c>_startedAt</c> was set once, at launch, and the
    /// app runs for weeks — three, at the time of writing, with the same process id.
    /// <para>
    /// The stale power report is dropped as well. It described a television as it was before the
    /// machine went away, and acting on it would be worse than having no report at all.
    /// </para>
    /// </remarks>
    private void NoteAwake(DateTimeOffset now, string reason)
    {
        _startedAt = now;
        _windowTrigger = "after a wake";

        // So is an unconfirmed endpoint loss, and this is the bug it fixes. The confirmation
        // rule is "gone for fifteen seconds", and a machine that suspends satisfies it for
        // free: no ticks run while it is away, so the first tick after a resume finds a loss
        // that is hours old and calls it a manual power-off. The log said so twice in two
        // days, on the same tick as the wake it was about to veto:
        //
        //   10:39:41.676  The machine is back (the clock jumped, so the machine was suspended)
        //   10:39:41.677  Television wake paused until 11:39 (you turned it off)
        //
        // Nobody had touched the remote. The endpoint went away because the machine did, and
        // an hour of not waking is the one thing that must not follow from that.
        //
        // Only the *pending* loss is dropped. A veto already confirmed — the set switched off
        // by hand while somebody was sitting there — is left exactly where it is, because that
        // one really was an instruction.
        _endpointLostAt = null;

        Volatile.Write(ref _power, null);
        _powerQueryAttempts = 0;
        Volatile.Write(ref _powerQueryStarted, 0);

        TvPolicy.BeginSession(_state.Current.TvPolicy);

        _log.LogInformation("The machine is back ({Reason}); the television gets another look.", reason);
    }

    /// <summary>
    /// Decides whether a vanished output endpoint really means somebody switched the set off.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="NoteOutputDeviceLost"/> so the decision can wait for evidence. The
    /// endpoint disappearing is not, on its own, a manual power-off: it also happens for a
    /// second or two whenever the television changes state, and it happens every evening when
    /// the display goes away. Only a loss that persists is the real thing.
    /// </remarks>
    private void ConfirmOrForgetAManualPowerOff(DateTimeOffset now, bool endpointPresent)
    {
        if (_endpointLostAt is not { } lostAt)
        {
            return;
        }

        if (endpointPresent)
        {
            _log.LogDebug("The output endpoint came back within {Ms} ms; not treating it as a power-off.", VetoConfirmMs);
            _endpointLostAt = null;
            return;
        }

        if ((now - lostAt).TotalMilliseconds < VetoConfirmMs)
        {
            return;
        }

        _endpointLostAt = null;

        TvPolicyConfig config = _settings.Current.Tv.Policy;
        TvPolicy.NoteUnexpectedDisappearance(now, config, _state.Current.TvPolicy);
        _state.Save();

        Status = $"Television wake paused until {_state.Current.TvPolicy.UserVetoUntil?.LocalDateTime:HH:mm} (you turned it off).";
        _log.LogInformation("{Status}", Status);
        Diagnostic?.Invoke(this, new DisplayEvent(Status));
    }

    /// <summary>
    /// Asks the television what state it is in while starting up, until it answers.
    /// </summary>
    /// <remarks>
    /// Without this the startup wake is unreachable on the hardware it was written for. The
    /// audio endpoint is the policy's normal power sensor, and a Samsung set in a
    /// <c>KEY_POWEROFF</c> standby keeps the HDMI link asserted, so Windows reports the
    /// endpoint Active while the screen is dark: the set looks on, and nothing is sent. Asking
    /// it directly costs one HTTP request at launch and gets the true answer.
    /// <para>
    /// Deliberately not done on the ordinary five-second cadence. There the endpoint is good
    /// enough and conservative, and polling a television all day to make waking it easier is
    /// the wrong trade.
    /// </para>
    /// </remarks>
    private void AskTheTelevisionAtStartup()
    {
        if (!_controller.Capabilities.HasFlag(DisplayCapabilities.PowerQuery)
            || _powerQueryAttempts >= PowerQueryAttempts
            || Interlocked.CompareExchange(ref _powerQueryStarted, 1, 0) != 0)
        {
            return;
        }

        _powerQueryAttempts++;   // engine thread only, like the rest of the tick

        // Its own task rather than RunInBackground: this is a read, and it must not make a
        // wake arriving a moment later look like a second overlapping command.
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            DisplayPowerState state = DisplayPowerState.Unreachable;

            try
            {
                state = await _controller.GetPowerStateAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not read the television's power state at startup.");
            }

            if (state is DisplayPowerState.On or DisplayPowerState.Standby or DisplayPowerState.Off)
            {
                Volatile.Write(ref _power, new PowerReport(state, DateTimeOffset.Now));
                _log.LogInformation("The television reports {State} at startup.", state);
                return;
            }

            // No usable answer. Free the guard so the next tick tries again rather than
            // spending the whole startup window on a sensor known to be wrong here.
            _log.LogDebug("The television gave no usable power state ({State}); attempt {Attempt}.", state, _powerQueryAttempts);
            Volatile.Write(ref _powerQueryStarted, 0);
        });
    }

    private DisplayPowerState? FreshPowerReport(DateTimeOffset now) =>
        Volatile.Read(ref _power) is { } report && (now - report.At).TotalMilliseconds < PowerReportValidForMs
            ? report.State
            : null;

    /// <summary>Wakes the television now, clearing any veto. The tray's "Wake TV".</summary>
    public void WakeNow()
    {
        TvPolicy.ClearVeto(_state.Current.TvPolicy);
        _state.Save();
        Execute(TvAction.Wake, "on request");
    }

    public void SleepNow() => Execute(TvAction.Sleep, "on request");

    public void SendVolume(VolumeCommand command) => RunInBackground(async ct =>
    {
        await _controller.SendVolumeAsync(command, ct).ConfigureAwait(false);
    });

    public Task<bool> PairAsync(CancellationToken ct) =>
        _controller is SamsungTvController samsung ? samsung.PairAsync(ct) : Task.FromResult(false);

    public Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct) => _controller.GetPowerStateAsync(ct);

    private void Execute(TvAction action, string trigger)
    {
        RunInBackground(async ct =>
        {
            TvPolicy.RecordPowerCommand(DateTimeOffset.Now, _state.Current.TvPolicy);

            if (action == TvAction.Wake)
            {
                _log.LogInformation("Waking the television ({Trigger}).", trigger);
                bool ok = await _controller.WakeAsync(ct).ConfigureAwait(false);
                _state.Current.TvPolicy.WeWokeIt = ok;
                Status = ok ? "The television is awake." : "The television did not respond to a wake.";
            }
            else
            {
                _log.LogInformation("Turning the television off ({Trigger}).", trigger);

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

        // Noted, not acted on. The tick confirms it once the endpoint has stayed away long
        // enough to mean something — see ConfirmOrForgetAManualPowerOff.
        _endpointLostAt ??= DateTimeOffset.Now;
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
