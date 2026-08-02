using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace NoSilence.Audio;

/// <summary>
/// The one place that owns an <see cref="MMDeviceEnumerator"/>.
/// </summary>
/// <remarks>
/// v1 created an enumerator, disposed it with <c>using</c>, and returned devices derived
/// from it — those devices are only valid while the enumerator lives. Here the enumerator
/// is held for the lifetime of the catalog, and callers get either an immutable
/// <see cref="AudioEndpointInfo"/> or an <see cref="MMDevice"/> they are told to dispose.
/// <para>
/// Every call is wrapped: enumerating endpoints touches the audio service, which can be
/// mid-restart, and a TV powering off can invalidate a device between two lines of code.
/// </para>
/// </remarks>
internal sealed class DeviceCatalog : IDisposable
{
    private readonly ILogger<DeviceCatalog> _log;
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    public DeviceCatalog(ILogger<DeviceCatalog> log) => _log = log;

    internal MMDeviceEnumerator Enumerator => _enumerator;

    /// <summary>
    /// Lists endpoints as immutable snapshots.
    /// </summary>
    /// <param name="flow">Render for outputs, Capture for microphones.</param>
    /// <param name="states">
    /// Defaults to <see cref="DeviceState.Active"/>. Pass <c>Active | Unplugged | NotPresent</c>
    /// to also see the TV while it is switched off — the settings UI wants that so it can
    /// show a configured-but-absent device rather than silently dropping it.
    /// </param>
    public IReadOnlyList<AudioEndpointInfo> List(DataFlow flow = DataFlow.Render, DeviceState states = DeviceState.Active)
    {
        string? defaultId = TryGetDefaultId(flow);
        var result = new List<AudioEndpointInfo>();

        try
        {
            MMDeviceCollection collection = _enumerator.EnumerateAudioEndPoints(flow, states);
            foreach (MMDevice device in collection)
            {
                try
                {
                    result.Add(new AudioEndpointInfo(
                        device.ID,
                        device.FriendlyName,
                        device.DeviceFriendlyName,
                        device.State,
                        string.Equals(device.ID, defaultId, StringComparison.Ordinal),
                        flow));
                }
                catch (COMException ex)
                {
                    // The endpoint went away between enumeration and reading its properties.
                    _log.LogDebug(ex, "Skipping an endpoint that became invalid while listing.");
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (COMException ex)
        {
            _log.LogWarning(ex, "Could not enumerate {Flow} endpoints; the audio service may be restarting.", flow);
        }

        return result
            .OrderByDescending(d => d.IsActive)
            .ThenBy(d => d.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves an endpoint ID to a live device. Caller disposes. Null if it is gone.</summary>
    public MMDevice? TryGet(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        try
        {
            return _enumerator.GetDevice(endpointId);
        }
        catch (COMException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The default console render endpoint. Caller disposes. Null if there is none.</summary>
    public MMDevice? TryGetDefault(DataFlow flow = DataFlow.Render, Role role = Role.Console)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(flow, role)
                ? _enumerator.GetDefaultAudioEndpoint(flow, role)
                : null;
        }
        catch (COMException)
        {
            // Happens with no audio hardware at all, or during an audio-service restart.
            return null;
        }
    }

    public string? TryGetDefaultId(DataFlow flow = DataFlow.Render, Role role = Role.Console)
    {
        using MMDevice? device = TryGetDefault(flow, role);
        try
        {
            return device?.ID;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enumerator.Dispose();
    }
}
