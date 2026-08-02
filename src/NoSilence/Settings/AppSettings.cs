namespace NoSilence.Settings;

/// <summary>
/// Everything the user can configure, serialised to <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Every property carries its default as an initialiser, so a missing or partial file
/// deserialises into something sane rather than nulls. <see cref="SchemaVersion"/> exists
/// so the shape can change later without silently discarding a user's configuration.
/// </remarks>
internal sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public LibrarySettings Library { get; set; } = new();

    public OutputSettings Output { get; set; } = new();

    /// <summary>
    /// Serialised directly rather than mirrored into a separate settings type. The config is
    /// already a plain POCO, and a mapping layer would be one more place for a default to
    /// drift out of step with the value the engine actually uses.
    /// </summary>
    public Detection.DetectionConfig Detection { get; set; } = new();

    public TvSettings Tv { get; set; } = new();

    public GeneralSettings General { get; set; } = new();
}

internal sealed class TvSettings
{
    /// <summary><c>none</c>, <c>wol</c>, <c>shell</c> or <c>samsung</c>.</summary>
    public string Provider { get; set; } = "none";

    /// <summary>The television's IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Overrides the automatically discovered MAC. Worth having: the address a Samsung set
    /// reports about itself is often its Wi-Fi radio's, which cannot wake a wired one.
    /// </summary>
    public string? MacAddress { get; set; }

    /// <summary>How long to wait for the HDMI endpoint to reappear after a wake.</summary>
    public int WaitForEndpointMs { get; set; } = 45000;

    /// <summary>Command or URL run to wake the display, for the shell provider.</summary>
    public string? WakeCommand { get; set; }

    public string? SleepCommand { get; set; }

    /// <summary>Command whose output is matched against on/off/standby.</summary>
    public string? StateCommand { get; set; }

    public Tv.TvPolicyConfig Policy { get; set; } = new();
}

internal sealed class LibrarySettings
{
    /// <summary>Folders scanned for music.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>
    /// Whether to descend into subfolders. On by default, but worth being able to turn off:
    /// a folder at the root of a large drive would otherwise walk the entire drive.
    /// </summary>
    public bool Recursive { get; set; } = true;

    /// <summary>
    /// Only formats Windows can actually decode without extra codecs. Ogg and Opus are
    /// deliberately absent: Media Foundation cannot open them, so including them by
    /// default would fill the skip list with failures on a first run.
    /// </summary>
    public List<string> Extensions { get; set; } = [".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma", ".aiff", ".aif"];

    public bool Shuffle { get; set; } = true;

    /// <summary>How many recently played tracks to push towards the back when reshuffling.</summary>
    public int NoRepeatWindow { get; set; } = 25;
}

internal sealed class OutputSettings
{
    /// <summary>
    /// The WASAPI endpoint ID. This is the durable identifier — it survives the TV being
    /// switched off, which removes the endpoint from Windows entirely.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Remembered alongside the ID purely as a fallback: a GPU driver reinstall can mint a
    /// new endpoint ID for the same physical output.
    /// </summary>
    public string? DeviceFriendlyName { get; set; }

    public int VolumePercent { get; set; } = 20;

    /// <summary>Shared-mode WASAPI buffer, in milliseconds. Higher is safer, lower is snappier.</summary>
    public int LatencyMs { get; set; } = 200;

    /// <summary>
    /// Off by default and deliberately so: falling back to the default device is how v1
    /// ended up playing into the device it was also listening to, which oscillates.
    /// </summary>
    public bool FallbackToDefaultDevice { get; set; }

    /// <summary>
    /// Keep feeding silence to the device while ducked instead of releasing it. An open
    /// WASAPI stream is what stops the TV going to sleep, so leaving this on trades a
    /// little power for an instant resume.
    /// </summary>
    public bool KeepStreamOpenWhileDucked { get; set; } = true;
}

internal sealed class GeneralSettings
{
    public bool RunAtStartup { get; set; }

    public NotificationLevel Notifications { get; set; } = NotificationLevel.ErrorsOnly;
}

internal enum NotificationLevel
{
    Off,
    ErrorsOnly,
    All,
}
