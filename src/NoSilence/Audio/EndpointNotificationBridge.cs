using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace NoSilence.Audio;

internal enum EndpointEventKind
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged,
    PropertyChanged,
}

/// <summary>A device notification, flattened into plain data so it can cross threads.</summary>
internal sealed record EndpointEvent(EndpointEventKind Kind, string DeviceId, DeviceState? NewState = null, DataFlow? Flow = null)
{
    public override string ToString() => NewState is { } state
        ? $"{Kind} {DeviceId} -> {state}"
        : $"{Kind} {DeviceId}";
}

/// <summary>
/// Receives WASAPI device notifications and hands them to the audio engine thread.
/// </summary>
/// <remarks>
/// Every method does nothing but enqueue and return. These callbacks arrive on an
/// audio-service thread, and calling back into <see cref="MMDeviceEnumerator"/> — or doing
/// anything slow — from inside one deadlocks. Getting this wrong is subtle: it works right
/// up until the moment the endpoint list is actually changing, which is exactly when it
/// matters.
/// <para>
/// This is how NoSilence learns the TV came back on. Windows removes the HDMI audio
/// endpoint entirely when a TV powers off or switches input, so without these callbacks the
/// only alternative is polling the endpoint list forever.
/// </para>
/// </remarks>
internal sealed class EndpointNotificationBridge : IMMNotificationClient
{
    private readonly Action<EndpointEvent> _dispatch;
    private readonly ILogger _log;

    public EndpointNotificationBridge(Action<EndpointEvent> dispatch, ILogger log)
    {
        _dispatch = dispatch;
        _log = log;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Enqueue(new EndpointEvent(EndpointEventKind.StateChanged, deviceId, newState));

    public void OnDeviceAdded(string pwstrDeviceId) =>
        Enqueue(new EndpointEvent(EndpointEventKind.Added, pwstrDeviceId));

    public void OnDeviceRemoved(string deviceId) =>
        Enqueue(new EndpointEvent(EndpointEventKind.Removed, deviceId));

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Only the console role matters to us; Windows fires one per role and we would
        // otherwise handle the same change three times.
        if (role == Role.Console)
        {
            Enqueue(new EndpointEvent(EndpointEventKind.DefaultChanged, defaultDeviceId ?? string.Empty, Flow: flow));
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
        Enqueue(new EndpointEvent(EndpointEventKind.PropertyChanged, pwstrDeviceId));

    private void Enqueue(EndpointEvent notification)
    {
        try
        {
            _dispatch(notification);
        }
        catch (Exception ex)
        {
            // An exception escaping into COM here would take the audio service callback
            // down with it, so nothing may propagate out of this class.
            _log.LogError(ex, "Failed to dispatch endpoint notification {Notification}.", notification);
        }
    }
}
