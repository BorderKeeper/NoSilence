using System.Globalization;

namespace NoSilence.Detection;

/// <summary>
/// Decides whether the user wants silence right now.
/// </summary>
/// <remarks>
/// A pure function of (snapshot, config, state). No COM, no Win32, no I/O, and — importantly
/// — no clock: time comes from <see cref="DetectionSnapshot.At"/>. That is what lets a
/// recorded session be replayed through it and what makes the timing testable to the tick.
/// <para>
/// The shape of the fix versus v1: v1 blocked its loop for three seconds to "confirm" noise
/// and then resumed on the very first quiet sample. Confirmation was a single instantaneous
/// re-read, so anything bursty — speech, a gap between words — read as silence, and with no
/// release debounce at all the result oscillated. Here the attack is a duty cycle measured
/// over a trailing window and the release is a full 20 seconds of continuous quiet, both
/// evaluated without ever blocking.
/// </para>
/// </remarks>
internal static class DecisionEngine
{
    public static DecisionOutcome Evaluate(DetectionSnapshot snapshot, DetectionConfig config, DecisionState state)
    {
        if (state.PhaseEnteredAt == default)
        {
            state.PhaseEnteredAt = snapshot.At;
            state.TransitionWindowStartedAt = snapshot.At;
        }

        var contributions = new List<TriggerContribution>();

        if (TryEvaluateOverride(snapshot, config, state, contributions) is { } overridden)
        {
            return overridden;
        }

        bool triggered = false;

        // Audio from other applications is the primary signal; everything else supplements it.
        triggered |= EvaluateRenderSessions(snapshot, config, state, contributions);
        triggered |= EvaluateCaptureSessions(snapshot, config, state, contributions);
        triggered |= EvaluateShellSignals(snapshot, config, contributions);
        triggered |= EvaluateIdleAndLock(snapshot, config, contributions);

        state.Tracker.Prune(snapshot.At);

        return triggered
            ? Duck(snapshot, config, state, contributions)
            : MaybeRelease(snapshot, config, state, contributions);
    }

    // ---- overrides -------------------------------------------------------

    private static DecisionOutcome? TryEvaluateOverride(
        DetectionSnapshot snapshot,
        DetectionConfig config,
        DecisionState state,
        List<TriggerContribution> contributions)
    {
        OverrideState state0 = snapshot.Override;

        if (state0.IsSnoozed(snapshot.At))
        {
            double remaining = (state0.SnoozeUntil!.Value - snapshot.At).TotalMinutes;
            contributions.Add(new TriggerContribution("Snooze", $"snoozed for another {remaining:F0} min", Counts: true));
            return Silence(state, snapshot, config, DecisionPhase.Overridden,
                $"Snoozed until {state0.SnoozeUntil.Value.LocalDateTime:HH:mm}", contributions);
        }

        switch (state0.Mode)
        {
            case OperatingMode.AlwaysSilent:
                contributions.Add(new TriggerContribution("Mode", "set to always silent", Counts: true));
                return Silence(state, snapshot, config, DecisionPhase.Overridden, "Set to always silent", contributions);

            case OperatingMode.AlwaysPlay:
                contributions.Add(new TriggerContribution("Mode", "set to always play", Counts: false));
                return Play(state, snapshot, config, "Set to always play", contributions);

            default:
                return null;
        }
    }

    // ---- signals ---------------------------------------------------------

    private static bool EvaluateRenderSessions(
        DetectionSnapshot snapshot,
        DetectionConfig config,
        DecisionState state,
        List<TriggerContribution> contributions)
    {
        // Nothing is audible through a muted endpoint, so nothing on it should silence us.
        bool endpointInaudible = config.IgnoreWhenEndpointMuted
            && (snapshot.DefaultEndpointMuted || snapshot.DefaultEndpointVolume < 0.02f);

        bool triggered = false;

        foreach (SessionObservation session in snapshot.Render)
        {
            // Our own audio, excluded by process ID. This single check is what frees v2 from
            // v1's rule that the music device had to differ from the device being watched.
            if (session.IsOurProcess)
            {
                contributions.Add(new TriggerContribution(session.Describe(), "our own playback", Counts: false, session.Dbfs, Rule: "self", Endpoint: session.EndpointName));
                continue;
            }

            ResolvedRule rule = RuleMatcher.Resolve(session, config);
            SessionStats stats = state.Tracker.Observe(session, rule, config, snapshot.At);

            if (rule.Ignored)
            {
                contributions.Add(new TriggerContribution(session.Describe(), "ignored by rule", Counts: false, stats.LastDb, Rule: rule.Source, Endpoint: session.EndpointName));
                continue;
            }

            if (endpointInaudible)
            {
                contributions.Add(new TriggerContribution(session.Describe(), "output is muted", Counts: false, stats.LastDb, Rule: rule.Source, Endpoint: session.EndpointName));
                continue;
            }

            if (config.IgnoreMutedSessions && (session.SessionMuted || session.SessionVolume <= 0.001f))
            {
                contributions.Add(new TriggerContribution(session.Describe(), "muted in the volume mixer", Counts: false, stats.LastDb, Rule: rule.Source, Endpoint: session.EndpointName));
                continue;
            }

            if (session.State == SessionActivity.Expired)
            {
                continue;
            }

            // The trailing window *is* the sustain requirement — SessionTracker sizes it from
            // MinDurationMs — so testing SustainedMs against MinDurationMs again here would
            // double-count it and take twice as long to duck as configured. SustainedMs is
            // reported, not re-tested.
            // "Play through this call" has to cover the application's own audio as well as the
            // microphone. Suppressing only the microphone would leave the other end talking
            // still ducking the music, which is not what anyone means by playing through it.
            if (snapshot.Override.PlayThroughCall && rule.CaptureMode == CaptureMode.Call)
            {
                contributions.Add(new TriggerContribution(
                    session.Describe(), "playing through this call", Counts: false,
                    stats.LastDb, Rule: rule.Source, Endpoint: session.EndpointName));
                continue;
            }

            bool counts = stats.NoisySince is not null;
            triggered |= counts;

            // Audio from the application that is in the call is the other end talking, and it
            // is what keeps the call alive for someone who sits muted through a whole meeting.
            // Matched to that application specifically, so a Teams notification cannot quietly
            // extend a Zoom call that has already finished.
            if (counts && rule.CaptureMode == CaptureMode.Call && state.IsCallExe(session.ExeName))
            {
                state.NoteCallSignal(snapshot.At);
            }

            contributions.Add(new TriggerContribution(
                session.Describe(),
                counts
                    ? $"peaked at {stats.WindowPeakDb:F1} dBFS over {stats.SustainedMs / 1000d:F1} s"
                    : $"{stats.LastDb:F1} dBFS",
                counts,
                stats.LastDb,
                stats.SustainedMs,
                rule.Source,
                session.EndpointName,
                stats.WindowPeakDb));
        }

        return triggered;
    }

    private static bool EvaluateCaptureSessions(
        DetectionSnapshot snapshot,
        DetectionConfig config,
        DecisionState state,
        List<TriggerContribution> contributions)
    {
        if (!config.MicrophoneSignal || snapshot.Capture.Count == 0)
        {
            state.EndCall();
            return false;
        }

        var micRule = new ResolvedRule(RuleMode.Trigger, config.MicThresholdDb, config.MicMinDurationMs, "microphone");
        bool triggered = false;
        bool callStillOpen = false;

        foreach (SessionObservation session in snapshot.Capture)
        {
            if (session.IsOurProcess || IsExcludedCapture(session, config))
            {
                continue;
            }

            // An application we never allow to silence us through its output must not be
            // able to do so through the microphone either. Windows Settings holds a live
            // capture session whenever its sound page is open, purely to draw a level
            // meter — enough, without this, to silence the music for as long as that page
            // stays open.
            ResolvedRule appRule = RuleMatcher.Resolve(session, config);

            if (appRule.Ignored)
            {
                contributions.Add(new TriggerContribution(
                    $"{session.Describe()} (microphone)", "ignored by rule", Counts: false,
                    session.Dbfs, Rule: "ignored", Endpoint: session.EndpointName));
                continue;
            }

            SessionStats stats = state.Tracker.Observe(session, micRule, config, snapshot.At);

            bool speaking = config.TreatActiveCaptureAsNoise
                ? session.State == SessionActivity.Active
                : stats.NoisySince is not null;

            // A call is a context, not a sound. Once an application that makes calls has
            // carried real microphone signal, it holds silence for as long as it keeps the
            // capture session open — through every pause for breath, which the level test
            // alone could never do.
            //
            // Note what arms it: `speaking` is the exact condition that used to trigger
            // directly. Nothing new starts ducking here, only the stopping changes. That
            // asymmetry is deliberate, because the dangerous version of this feature is the
            // one where a client sitting idle in the tray with an open microphone — OBS,
            // Voicemeeter, Zoom between meetings — silences the music indefinitely. An idle
            // microphone carries no sustained signal, so it never arms a call.
            bool open = appRule.CaptureMode == CaptureMode.Call && session.State == SessionActivity.Active;

            if (open && speaking)
            {
                state.BeginCall(session.SessionInstanceId, session.Describe(), session.ExeName);
                state.NoteCallSignal(snapshot.At);
            }

            // The hold is bounded. A client that keeps its capture session open after the
            // meeting ends would otherwise silence the music until it exited, so a call that
            // has produced nothing from either direction for CallIdleTimeoutMs is over.
            bool alive = state.CallSignalAt is { } signal
                && (snapshot.At - signal).TotalMilliseconds <= config.CallIdleTimeoutMs;

            bool holding = open && alive && state.CallSessionId == session.SessionInstanceId;
            callStillOpen |= holding;

            // Note that the call is still tracked while it is being played through — arming
            // and holding both continue. That is what lets the override expire on its own when
            // the call ends, rather than being left on for the next one.
            bool suppressed = snapshot.Override.PlayThroughCall && appRule.CaptureMode == CaptureMode.Call;

            bool counts = !suppressed && (speaking || holding);
            triggered |= counts;

            contributions.Add(new TriggerContribution(
                $"{session.Describe()} (microphone)",
                suppressed
                    ? "playing through this call"
                    : counts
                        ? holding && !speaking ? "in a call" : $"in use, peaked at {stats.WindowPeakDb:F1} dBFS"
                        : $"{stats.LastDb:F1} dBFS",
                counts,
                stats.LastDb,
                stats.SustainedMs,
                holding ? "call" : "microphone",
                session.EndpointName,
                stats.WindowPeakDb));
        }

        // The call ends when its capture session stops being active or disappears from the
        // list entirely — the client closed the microphone, which is the one unambiguous
        // signal that the meeting is over.
        if (!callStillOpen)
        {
            state.EndCall();
        }

        return triggered;
    }

    private static bool IsExcludedCapture(SessionObservation session, DetectionConfig config)
    {
        foreach (string pattern in config.MicExclusions)
        {
            if (RuleMatcher.GlobMatch(pattern, session.ExeName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvaluateShellSignals(DetectionSnapshot snapshot, DetectionConfig config, List<TriggerContribution> contributions)
    {
        bool triggered = false;

        if (config.FullscreenSignal && snapshot.Shell is ShellActivity.FullScreenD3D or ShellActivity.PresentationMode or ShellActivity.Busy)
        {
            triggered = true;
            contributions.Add(new TriggerContribution("Full screen", $"{snapshot.Shell} application in the foreground", Counts: true));
        }

        if (config.FocusAssistSignal && snapshot.Shell == ShellActivity.QuietTime)
        {
            triggered = true;
            contributions.Add(new TriggerContribution("Focus Assist", "do not disturb is on", Counts: true));
        }

        return triggered;
    }

    private static bool EvaluateIdleAndLock(DetectionSnapshot snapshot, DetectionConfig config, List<TriggerContribution> contributions)
    {
        bool triggered = false;

        if (config.SilenceWhenLocked && snapshot.WorkstationLocked)
        {
            triggered = true;
            contributions.Add(new TriggerContribution("Workstation", "locked", Counts: true));
        }

        if (config.SilenceWhenIdleMinutes > 0 && snapshot.UserIdle >= TimeSpan.FromMinutes(config.SilenceWhenIdleMinutes))
        {
            triggered = true;
            contributions.Add(new TriggerContribution("Idle", $"no input for {snapshot.UserIdle.TotalMinutes:F0} min", Counts: true));
        }

        return triggered;
    }

    // ---- hysteresis ------------------------------------------------------

    private static DecisionOutcome Duck(
        DetectionSnapshot snapshot,
        DetectionConfig config,
        DecisionState state,
        List<TriggerContribution> contributions)
    {
        state.LastTriggerAt = snapshot.At;
        state.SilenceSince ??= snapshot.At;
        // A duck that happened *while* a call was being played through was caused by something
        // else entirely, so it must not inherit the call's longer release.
        state.LastTriggerWasCall = state.CallApp is not null && !snapshot.Override.PlayThroughCall;
        EnterPhase(state, DecisionPhase.Ducked, snapshot.At);

        // Ranked by the level that caused the trigger, not by whatever the newest sample
        // happens to read — otherwise the source named as the culprit can be one that has
        // already gone quiet.
        TriggerContribution? loudest = contributions
            .Where(c => c.Counts)
            .OrderByDescending(c => c.PeakDbfs ?? c.Dbfs ?? double.MinValue)
            .FirstOrDefault();

        // A call names itself. "Zoom.exe (microphone): in use, peaked at -18.2 dBFS" is a true
        // statement about a sample and a useless one about why the music stopped.
        string reason = state.CallApp is { } caller
            ? $"In a call — {caller}"
            : loudest is null
                ? "Something else is playing"
                : $"{loudest.Source}: {loudest.Detail}";

        return new DecisionOutcome(true, config.DuckedGain, config.DuckFadeOutMs, DecisionPhase.Ducked, reason, contributions)
        {
            IsCall = state.CallApp is not null,
        };
    }

    private static DecisionOutcome MaybeRelease(
        DetectionSnapshot snapshot,
        DetectionConfig config,
        DecisionState state,
        List<TriggerContribution> contributions)
    {
        if (state.Phase is DecisionPhase.Playing)
        {
            return Play(state, snapshot, config, "Nothing else is playing", contributions);
        }

        // An explicit "play through this call" is a request for music now, not in fifteen
        // seconds. The release window exists to absorb gaps in someone else's audio and has
        // nothing to say about a decision the user just made by hand. Reaching here at all
        // means nothing else is triggering, so there is nothing to hold out for.
        if (snapshot.Override.PlayThroughCall)
        {
            return Play(state, snapshot, config, "Playing through the call", contributions);
        }

        double quietMs = state.LastTriggerAt is { } last ? (snapshot.At - last).TotalMilliseconds : double.MaxValue;
        double silentMs = state.SilenceSince is { } since ? (snapshot.At - since).TotalMilliseconds : double.MaxValue;

        // A call gets its own, longer release. Reaching here after one means the microphone
        // has actually closed, and the tail covers a client that reopens it briefly between
        // meetings. Math.Max so that raising the ordinary release above it still applies.
        double releaseMs = state.LastTriggerWasCall
            ? Math.Max(config.CallReleaseMs, config.ReleaseMs)
            : config.ReleaseMs;

        // The grace period stops a single quiet tick immediately after ducking from bouncing
        // the music straight back up. Measured from when silence began, not from the current
        // phase — Ducked and Releasing are one continuous stretch of silence.
        if (quietMs < releaseMs || silentMs < config.HardDuckGraceMs)
        {
            double remaining = Math.Max(0d, (releaseMs - quietMs) / 1000d);
            EnterPhase(state, DecisionPhase.Releasing, snapshot.At, countTransition: false);

            return new DecisionOutcome(
                true,
                config.DuckedGain,
                config.DuckFadeOutMs,
                DecisionPhase.Releasing,
                $"Quiet — resuming in {remaining:F0} s",
                contributions)
            {
                ResumesInSeconds = remaining,
            };
        }

        return Play(state, snapshot, config, "Nothing else is playing", contributions);
    }

    private static DecisionOutcome Play(
        DecisionState state,
        DetectionSnapshot snapshot,
        DetectionConfig config,
        string reason,
        List<TriggerContribution> contributions)
    {
        state.SilenceSince = null;
        EnterPhase(state, DecisionPhase.Playing, snapshot.At);
        return new DecisionOutcome(false, 1f, config.DuckFadeInMs, DecisionPhase.Playing, reason, contributions);
    }

    private static DecisionOutcome Silence(
        DecisionState state,
        DetectionSnapshot snapshot,
        DetectionConfig config,
        DecisionPhase phase,
        string reason,
        List<TriggerContribution> contributions)
    {
        state.SilenceSince ??= snapshot.At;
        EnterPhase(state, phase, snapshot.At);
        return new DecisionOutcome(true, config.DuckedGain, config.DuckFadeOutMs, phase, reason, contributions);
    }

    private static void EnterPhase(DecisionState state, DecisionPhase phase, DateTimeOffset at, bool countTransition = true)
    {
        if (state.Phase == phase)
        {
            return;
        }

        // Flap detection: a healthy setup transitions a handful of times an hour. Twenty is
        // a sign the threshold or the rules need attention, and counting it here means the
        // problem can be found without watching for it.
        if (countTransition)
        {
            if (at - state.TransitionWindowStartedAt > TimeSpan.FromHours(1))
            {
                state.TransitionWindowStartedAt = at;
                state.TransitionsThisHour = 0;
            }

            state.TransitionsThisHour++;
        }

        state.Phase = phase;
        state.PhaseEnteredAt = at;
    }

    /// <summary>One-line summary for the tray tooltip and the log.</summary>
    public static string Summarise(DecisionOutcome outcome) => string.Create(
        CultureInfo.CurrentCulture,
        $"{(outcome.WantsSilence ? "SILENT" : "PLAYING")} — {outcome.Reason}");
}
