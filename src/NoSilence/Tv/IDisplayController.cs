namespace NoSilence.Tv;

internal enum DisplayPowerState
{
    Unknown,
    On,
    Standby,
    Off,
    Unreachable,
}

[Flags]
internal enum DisplayCapabilities
{
    None = 0,
    Wake = 1,
    Sleep = 2,
    Volume = 4,
    InputSelect = 8,
    PowerQuery = 16,
}

internal enum VolumeCommand
{
    Up,
    Down,
    ToggleMute,
}

internal sealed record DisplayEvent(string Message, bool IsError = false);

/// <summary>
/// Turning the television on and off.
/// </summary>
/// <remarks>
/// An interface rather than a Samsung class because the hardware situation is genuinely
/// varied: a PC graphics card cannot send HDMI-CEC (consumer NVIDIA cards do not expose it
/// at all), so "wake the TV" ends up being Wake-on-LAN for some people, a Home Assistant
/// webhook or a smart plug for others, and a USB CEC adapter for a few.
/// </remarks>
internal interface IDisplayController : IAsyncDisposable
{
    /// <summary>Stable identifier used in settings: <c>none</c>, <c>wol</c>, <c>shell</c>, <c>samsung</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    DisplayCapabilities Capabilities { get; }

    Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct);

    Task<bool> WakeAsync(CancellationToken ct);

    Task<bool> SleepAsync(CancellationToken ct);

    Task<bool> SendVolumeAsync(VolumeCommand command, CancellationToken ct);

    /// <summary>Progress and failures, for the TV tab's log.</summary>
    event EventHandler<DisplayEvent>? Diagnostic;
}

/// <summary>
/// The default. Declares no capabilities and does nothing, so an unconfigured install never
/// touches hardware.
/// </summary>
internal sealed class NullDisplayController : IDisplayController
{
    public string Id => "none";

    public string DisplayName => "No television control";

    public DisplayCapabilities Capabilities => DisplayCapabilities.None;

    public event EventHandler<DisplayEvent>? Diagnostic
    {
        add { }
        remove { }
    }

    public Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct) => Task.FromResult(DisplayPowerState.Unknown);

    public Task<bool> WakeAsync(CancellationToken ct) => Task.FromResult(false);

    public Task<bool> SleepAsync(CancellationToken ct) => Task.FromResult(false);

    public Task<bool> SendVolumeAsync(VolumeCommand command, CancellationToken ct) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
