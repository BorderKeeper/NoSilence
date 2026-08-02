using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NoSilence.Detection;
using AudioSessionState = NAudio.CoreAudioApi.Interfaces.AudioSessionState;

namespace NoSilence.Audio;

/// <summary>
/// Reads what every application is playing, per process, per endpoint.
/// </summary>
/// <remarks>
/// This is the replacement for v1's single <c>AudioMeterInformation.PeakValue</c> on the
/// default endpoint. Three cadences, because the operations cost wildly different amounts:
/// <list type="bullet">
/// <item>The session manager is acquired once per device and held.</item>
/// <item>The session list is rebuilt every couple of seconds. <c>IAudioSessionEnumerator</c>
/// returns a snapshot — new sessions never appear in an old one — and rebuilding costs a
/// COM round trip plus a QueryInterface per session.</item>
/// <item>Peaks are read every tick from the cached controls. That is one
/// <c>GetPeakValue</c> per session, which is cheap enough to do at 4 Hz across a dozen
/// sessions without registering on a CPU graph.</item>
/// </list>
/// <para>
/// Every property read is wrapped: a control belonging to an exited application throws
/// <c>AUDCLNT_E_DEVICE_INVALIDATED</c> on any access, and a TV powering off invalidates a
/// whole endpoint's worth at once.
/// </para>
/// </remarks>
internal sealed class AudioSessionProbe : IDisposable
{
    private readonly DeviceCatalog _catalog;
    private readonly ProcessInfoCache _processes;
    private readonly ILogger<AudioSessionProbe> _log;
    private readonly Dictionary<DataFlow, FlowState> _flows = [];

    private bool _disposed;

    public AudioSessionProbe(DeviceCatalog catalog, ProcessInfoCache processes, ILogger<AudioSessionProbe> log)
    {
        _catalog = catalog;
        _processes = processes;
        _log = log;
    }

    /// <summary>How often the session list is rebuilt, in milliseconds.</summary>
    public int RefreshIntervalMs { get; set; } = 2000;

    /// <summary>Forces a rebuild on the next sample, e.g. after an endpoint notification.</summary>
    public void Invalidate()
    {
        foreach (FlowState flow in _flows.Values)
        {
            flow.NextRefreshAt = 0;
        }
    }

    public IReadOnlyList<SessionObservation> Sample(DataFlow flow)
    {
        if (_disposed)
        {
            return [];
        }

        if (!_flows.TryGetValue(flow, out FlowState? state))
        {
            state = new FlowState();
            _flows[flow] = state;
        }

        if (Environment.TickCount64 >= state.NextRefreshAt)
        {
            RefreshEndpoints(flow, state);
            state.NextRefreshAt = Environment.TickCount64 + RefreshIntervalMs;
        }

        var observations = new List<SessionObservation>(state.EstimatedSessionCount);

        foreach (EndpointSessions endpoint in state.Endpoints)
        {
            for (int i = endpoint.Controls.Count - 1; i >= 0; i--)
            {
                SessionObservation? observation = Observe(endpoint, endpoint.Controls[i]);
                if (observation is null)
                {
                    // Dead control: drop it now and force a rebuild so we do not keep
                    // paying for exceptions every tick.
                    endpoint.Controls.RemoveAt(i);
                    state.NextRefreshAt = 0;
                    continue;
                }

                observations.Add(observation);
            }
        }

        state.EstimatedSessionCount = Math.Max(observations.Count, 4);
        return observations;
    }

    private SessionObservation? Observe(EndpointSessions endpoint, AudioSessionControl control)
    {
        try
        {
            uint processId = control.GetProcessID;
            bool isSystemSounds = control.IsSystemSoundsSession;
            string instanceId = control.GetSessionInstanceIdentifier ?? string.Empty;
            string identifier = control.GetSessionIdentifier ?? string.Empty;

            string exeName = isSystemSounds
                ? "(system sounds)"
                : _processes.Resolve(instanceId, identifier, processId);

            SimpleAudioVolume volume = control.SimpleAudioVolume;

            return new SessionObservation(
                SessionInstanceId: instanceId,
                EndpointId: endpoint.Id,
                EndpointName: endpoint.Name,
                ProcessId: processId,
                ExeName: exeName,
                DisplayName: Clean(control.DisplayName),
                IsSystemSounds: isSystemSounds,
                IsOurProcess: processId == Environment.ProcessId,
                State: Map(control.State),
                Peak: control.AudioMeterInformation.MasterPeakValue,
                SessionVolume: volume.Volume,
                SessionMuted: volume.Mute);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            // Expected whenever an application exits or an endpoint is invalidated.
            return null;
        }
    }

    private void RefreshEndpoints(DataFlow flow, FlowState state)
    {
        foreach (EndpointSessions endpoint in state.Endpoints)
        {
            endpoint.Dispose();
        }

        state.Endpoints.Clear();

        IReadOnlyList<AudioEndpointInfo> endpoints = _catalog.List(flow, DeviceState.Active);

        foreach (AudioEndpointInfo info in endpoints)
        {
            MMDevice? device = _catalog.TryGet(info.Id);
            if (device is null)
            {
                continue;
            }

            try
            {
                AudioSessionManager manager = device.AudioSessionManager;
                manager.RefreshSessions();

                var controls = new List<AudioSessionControl>();
                SessionCollection sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        AudioSessionControl control = sessions[i];
                        if (control.State != AudioSessionState.AudioSessionStateExpired)
                        {
                            controls.Add(control);
                        }
                    }
                    catch (COMException)
                    {
                        // Session vanished mid-enumeration.
                    }
                }

                state.Endpoints.Add(new EndpointSessions(info.Id, info.FriendlyName, device, controls));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _log.LogDebug(ex, "Could not read sessions on {Endpoint}.", info.FriendlyName);
                device.Dispose();
            }
        }
    }

    private static SessionActivity Map(AudioSessionState state) => state switch
    {
        AudioSessionState.AudioSessionStateActive => SessionActivity.Active,
        AudioSessionState.AudioSessionStateInactive => SessionActivity.Inactive,
        _ => SessionActivity.Expired,
    };

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Packaged apps report a resource string such as "@{Microsoft.WindowsCalculator…}",
        // which is worse than useless in a list the user has to read.
        return value.StartsWith("@{", StringComparison.Ordinal) ? null : value.Trim();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (FlowState state in _flows.Values)
        {
            foreach (EndpointSessions endpoint in state.Endpoints)
            {
                endpoint.Dispose();
            }
        }

        _flows.Clear();
    }

    private sealed class FlowState
    {
        public List<EndpointSessions> Endpoints { get; } = [];

        public long NextRefreshAt { get; set; }

        public int EstimatedSessionCount { get; set; } = 8;
    }

    private sealed record EndpointSessions(string Id, string Name, MMDevice Device, List<AudioSessionControl> Controls)
    {
        public void Dispose()
        {
            Controls.Clear();
            try
            {
                Device.Dispose();
            }
            catch (COMException)
            {
                // Already invalidated.
            }
        }
    }
}
