using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NoSilence.Settings;

namespace NoSilence.Audio;

internal enum DeviceResolution
{
    /// <summary>Found and usable.</summary>
    Resolved,

    /// <summary>Nothing is configured yet.</summary>
    NotConfigured,

    /// <summary>Known to Windows, but not currently active — the TV is off or on another input.</summary>
    PresentButInactive,

    /// <summary>Windows has never heard of it, or it has been removed entirely.</summary>
    Missing,
}

internal sealed record DeviceResolutionResult(DeviceResolution Outcome, MMDevice? Device, string Description)
{
    public bool Success => Outcome == DeviceResolution.Resolved && Device is not null;
}

/// <summary>
/// Turns the configured output device into a live endpoint.
/// </summary>
/// <remarks>
/// Resolution order is deliberate: endpoint ID, then exact friendly name, then a
/// case-insensitive substring of the friendly name (which is all v1 ever had). The
/// substring pass is last because it is genuinely ambiguous — on this machine there are
/// four endpoints called "SAMSUNG (NVIDIA High Definition Audio)", one active and three
/// left over from previous connections, and v1's <c>.First()</c> had an even chance of
/// picking a dead one.
/// <para>
/// Falling back to the default device is off unless the user asks for it: sending our music
/// to the same endpoint we listen to is precisely how v1 triggered itself.
/// </para>
/// </remarks>
internal sealed class OutputDeviceResolver
{
    private readonly DeviceCatalog _catalog;
    private readonly ILogger<OutputDeviceResolver> _log;

    public OutputDeviceResolver(DeviceCatalog catalog, ILogger<OutputDeviceResolver> log)
    {
        _catalog = catalog;
        _log = log;
    }

    public DeviceResolutionResult Resolve(OutputSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DeviceId) && string.IsNullOrWhiteSpace(settings.DeviceFriendlyName))
        {
            return settings.FallbackToDefaultDevice
                ? ResolveDefault("no output device is configured")
                : new DeviceResolutionResult(DeviceResolution.NotConfigured, null, "No output device has been chosen yet.");
        }

        if (!string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            MMDevice? byId = _catalog.TryGet(settings.DeviceId);
            if (byId is not null)
            {
                if (byId.State == NAudio.CoreAudioApi.DeviceState.Active)
                {
                    return new DeviceResolutionResult(DeviceResolution.Resolved, byId, byId.FriendlyName);
                }

                string name = byId.FriendlyName;
                byId.Dispose();
                return new DeviceResolutionResult(
                    DeviceResolution.PresentButInactive,
                    null,
                    $"{name} is not connected right now.");
            }
        }

        // The ID stopped resolving. That normally means a GPU driver reinstall minted a new
        // endpoint for the same physical output, so try the remembered name.
        if (!string.IsNullOrWhiteSpace(settings.DeviceFriendlyName))
        {
            DeviceResolutionResult byName = ResolveByName(settings.DeviceFriendlyName);
            if (byName.Success)
            {
                _log.LogWarning(
                    "Output endpoint ID {Id} no longer exists; matched {Name} by name instead. The ID will be updated.",
                    settings.DeviceId,
                    byName.Description);
            }

            return byName;
        }

        return new DeviceResolutionResult(
            DeviceResolution.Missing,
            null,
            "The configured output device no longer exists.");
    }

    private DeviceResolutionResult ResolveByName(string name)
    {
        IReadOnlyList<AudioEndpointInfo> all = _catalog.List(
            DataFlow.Render,
            NAudio.CoreAudioApi.DeviceState.Active | NAudio.CoreAudioApi.DeviceState.Unplugged | NAudio.CoreAudioApi.DeviceState.NotPresent);

        AudioEndpointInfo? exact = all.FirstOrDefault(d => d.IsActive && string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase));
        AudioEndpointInfo? match = exact
            ?? all.FirstOrDefault(d => d.IsActive && d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.IsActive && d.DeviceFriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            MMDevice? device = _catalog.TryGet(match.Id);
            if (device is not null)
            {
                return new DeviceResolutionResult(DeviceResolution.Resolved, device, match.FriendlyName);
            }
        }

        bool knownButOff = all.Any(d => !d.IsActive && d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        return knownButOff
            ? new DeviceResolutionResult(DeviceResolution.PresentButInactive, null, $"{name} is not connected right now.")
            : new DeviceResolutionResult(DeviceResolution.Missing, null, $"No output device matching \"{name}\" was found.");
    }

    private DeviceResolutionResult ResolveDefault(string why)
    {
        MMDevice? device = _catalog.TryGetDefault();
        if (device is null)
        {
            return new DeviceResolutionResult(DeviceResolution.Missing, null, "There is no default output device.");
        }

        _log.LogWarning("Using the default output device because {Why}. Music will play into the device you listen on.", why);
        return new DeviceResolutionResult(DeviceResolution.Resolved, device, device.FriendlyName);
    }
}
