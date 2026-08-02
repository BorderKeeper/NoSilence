using NAudio.CoreAudioApi;

namespace NoSilence.Audio;

/// <summary>
/// A plain snapshot of a WASAPI endpoint. Deliberately a value type rather than a live
/// <see cref="MMDevice"/>: v1 handed out COM objects whose enumerator had already been
/// disposed, and anything that crosses a thread or lives in the UI must not be COM.
/// </summary>
/// <param name="Id">
/// The WASAPI endpoint ID (<c>{0.0.0.00000000}.{guid}</c>). This is what we persist:
/// it survives the TV powering off, reboots, and most driver updates — unlike an index
/// or a position in a list.
/// </param>
/// <param name="FriendlyName">Full name, e.g. "SAMSUNG (NVIDIA High Definition Audio)".</param>
/// <param name="DeviceFriendlyName">Short name, e.g. "SAMSUNG".</param>
/// <param name="State">Active / Disabled / NotPresent / Unplugged.</param>
/// <param name="IsDefault">True if this is the default console render endpoint.</param>
/// <param name="Flow">Render or capture.</param>
internal sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    string DeviceFriendlyName,
    DeviceState State,
    bool IsDefault,
    DataFlow Flow)
{
    public bool IsActive => State == DeviceState.Active;

    /// <summary>Short human description used in menus and logs.</summary>
    public string Describe() => State == DeviceState.Active
        ? FriendlyName
        : $"{FriendlyName} ({DescribeState()})";

    public string DescribeState() => State switch
    {
        DeviceState.Active => "available",
        DeviceState.Disabled => "disabled",
        DeviceState.NotPresent => "not connected",
        DeviceState.Unplugged => "unplugged",
        _ => State.ToString().ToLowerInvariant(),
    };
}
