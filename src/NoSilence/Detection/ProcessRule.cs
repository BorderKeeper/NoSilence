namespace NoSilence.Detection;

internal enum RuleMatchKind
{
    /// <summary>Matches the executable name, e.g. <c>chrome.exe</c>. Supports <c>*</c>.</summary>
    ExeName,

    /// <summary>Substring of the full executable path.</summary>
    ExePathContains,

    /// <summary>Substring of the session's display name. Best for packaged apps.</summary>
    DisplayNameContains,

    /// <summary>Matches the Windows system-sounds session, which has no real process.</summary>
    SystemSounds,
}

internal enum RuleMode
{
    /// <summary>Use the global threshold and duration.</summary>
    Default,

    /// <summary>Never counts as noise, however loud.</summary>
    Ignore,

    /// <summary>Counts only after a longer than usual sustained period.</summary>
    Tolerant,

    /// <summary>Counts normally.</summary>
    Trigger,

    /// <summary>Counts the instant it is above threshold, with no sustain requirement.</summary>
    AlwaysTrigger,
}

/// <summary>How an application's <em>microphone</em> use is interpreted.</summary>
internal enum CaptureMode
{
    /// <summary>
    /// Measure the level, like any other source. Right for anything that might open a
    /// microphone without a conversation attached.
    /// </summary>
    Level,

    /// <summary>
    /// This application makes calls. Sustained microphone signal from it starts a call, and
    /// silence is then held for as long as it keeps the capture session open.
    /// </summary>
    /// <remarks>
    /// The distinction that matters: a call is a <em>context</em>, not a sound. Measuring the
    /// level answers "is someone speaking right now", which goes false every time you pause
    /// for breath. Two days of logs showed the cost — 352 play/silence flips in one day, very
    /// nearly all of them one Zoom call being sampled as intermittent noise.
    /// </remarks>
    Call,
}

/// <summary>
/// One entry in the per-application rules table.
/// </summary>
/// <remarks>
/// The point of the table is that "is this noise?" is not a property of the sound, it is a
/// property of the application. A Discord notification and a Discord call produce the same
/// level; only their duration distinguishes them.
/// </remarks>
internal sealed record ProcessRule(
    string Match,
    RuleMatchKind MatchKind = RuleMatchKind.ExeName,
    RuleMode Mode = RuleMode.Default,
    double? ThresholdDb = null,
    int? MinDurationMs = null,
    bool Enabled = true)
{
    /// <summary>True for rules NoSilence ships, so the UI can mark them and offer a restore.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>How this application's microphone use is read. Defaults to plain level.</summary>
    public CaptureMode CaptureMode { get; init; }

    /// <summary>
    /// The rules shipped out of the box. Ordered: the first match wins, so specific entries
    /// come before general ones.
    /// </summary>
    /// <remarks>
    /// Chat and mail clients are Tolerant rather than Ignore on purpose — a Discord ping is
    /// about a second and must not pause your music, but a Discord call is continuous and
    /// must. Media players are AlwaysTrigger because if you deliberately started one, you
    /// want to hear it immediately, not 1.2 seconds from now.
    /// </remarks>
    public static IReadOnlyList<ProcessRule> BuiltInRules { get; } =
    [
        new("(system sounds)", RuleMatchKind.SystemSounds, RuleMode.Ignore) { BuiltIn = true },
        new("NoSilence.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },

        // Shell surfaces that only ever chime.
        new("explorer.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("SearchHost.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("ShellExperienceHost.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("StartMenuExperienceHost.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("olk.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("OUTLOOK.EXE", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },

        // Console hosts only ever ring the bell — a failed command, a tab-completion dead
        // end. Observed triggering a duck in testing: the bell is around a second, which was
        // enough to satisfy the sustain window. Never content, so never counted.
        new("powershell.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("pwsh.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("cmd.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("conhost.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("OpenConsole.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },
        new("WindowsTerminal.exe", RuleMatchKind.ExeName, RuleMode.Ignore) { BuiltIn = true },

        // Chat: pings are ~1 s, calls are continuous. All of these also make calls, so their
        // microphone use is read as a call context rather than as a level — see CaptureMode.
        // Zoom was missing entirely until daily use found it, which is why it fell through to
        // the default rule and was ducking on a 2 s sustain.
        new("Zoom.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("CptHost.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("discord.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("Teams.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("ms-teams.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("slack.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("Telegram.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("WhatsApp.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },
        new("msedgewebview2.exe", RuleMatchKind.ExeName, RuleMode.Tolerant, MinDurationMs: 4000) { BuiltIn = true, CaptureMode = CaptureMode.Call },

        // Browsers: notification blips are under 1.5 s, so 2.5 s separates them from content.
        new("chrome.exe", RuleMatchKind.ExeName, RuleMode.Trigger, MinDurationMs: 2500) { BuiltIn = true },
        new("msedge.exe", RuleMatchKind.ExeName, RuleMode.Trigger, MinDurationMs: 2500) { BuiltIn = true },
        new("firefox.exe", RuleMatchKind.ExeName, RuleMode.Trigger, MinDurationMs: 2500) { BuiltIn = true },
        new("brave.exe", RuleMatchKind.ExeName, RuleMode.Trigger, MinDurationMs: 2500) { BuiltIn = true },
        new("opera.exe", RuleMatchKind.ExeName, RuleMode.Trigger, MinDurationMs: 2500) { BuiltIn = true },

        // You started these on purpose.
        new("spotify.exe", RuleMatchKind.ExeName, RuleMode.AlwaysTrigger) { BuiltIn = true },
        new("vlc.exe", RuleMatchKind.ExeName, RuleMode.AlwaysTrigger) { BuiltIn = true },
        new("mpc-hc64.exe", RuleMatchKind.ExeName, RuleMode.AlwaysTrigger) { BuiltIn = true },
        new("mpc-be64.exe", RuleMatchKind.ExeName, RuleMode.AlwaysTrigger) { BuiltIn = true },
        new("PotPlayerMini64.exe", RuleMatchKind.ExeName, RuleMode.AlwaysTrigger) { BuiltIn = true },
        new("foobar2000.exe", RuleMatchKind.ExeName, RuleMode.AlwaysTrigger) { BuiltIn = true },
    ];
}

/// <summary>The rule that applied, with its global defaults already folded in.</summary>
internal readonly record struct ResolvedRule(
    RuleMode Mode,
    double ThresholdDb,
    int MinDurationMs,
    string Source,
    CaptureMode CaptureMode = CaptureMode.Level)
{
    public bool Ignored => Mode == RuleMode.Ignore;
}
