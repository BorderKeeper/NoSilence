namespace NoSilence.Playback;

internal enum PlaybackPhase
{
    /// <summary>No music folders configured, or nothing playable in them.</summary>
    Idle,

    /// <summary>The configured output device is not available — typically the TV is off.</summary>
    NoDevice,

    /// <summary>Opening the device, or waiting out a retry backoff.</summary>
    Opening,

    /// <summary>Audible.</summary>
    Playing,

    /// <summary>Silent because something else is making noise.</summary>
    Ducked,

    /// <summary>Silent because the user asked for it.</summary>
    Silenced,

    /// <summary>Repeated failures we could not recover from automatically.</summary>
    Faulted,
}

/// <summary>
/// An immutable view of what playback is doing, published to the UI. Plain data on purpose:
/// it crosses from the audio thread to the UI thread, so it must hold no COM and no
/// disposable state.
/// </summary>
internal sealed record PlaybackSnapshot(
    PlaybackPhase Phase,
    TrackInfo? Track,
    TimeSpan Position,
    TimeSpan Duration,
    float Gain,
    string? DeviceName,
    string? Detail)
{
    public static PlaybackSnapshot Empty { get; } =
        new(PlaybackPhase.Idle, null, TimeSpan.Zero, TimeSpan.Zero, 0f, null, null);

    public bool IsAudible => Phase == PlaybackPhase.Playing && Gain > 0.001f;
}
