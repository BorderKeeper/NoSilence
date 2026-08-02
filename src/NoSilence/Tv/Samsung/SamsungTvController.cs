using System.Net;
using Microsoft.Extensions.Logging;
using NoSilence.Settings;

namespace NoSilence.Tv.Samsung;

/// <summary>
/// Samsung television control: Wake-on-LAN to turn it on, the WebSocket remote for
/// everything else.
/// </summary>
/// <remarks>
/// <para><b>Power-on is Wake-on-LAN only.</b> In standby the set closes its WebSocket port,
/// so the remote API is unreachable precisely when you would want it. The television must
/// have network standby enabled (Settings → General → Network → Expert Settings), and it is
/// materially more reliable over Ethernet — many sets do not honour Wake-on-LAN over Wi-Fi
/// at all, in which case waking is simply not possible and the app says so.</para>
/// <para><b>Power-off prefers KEY_POWEROFF.</b> KEY_POWER is a <em>toggle</em> on many sets,
/// so issuing it with a stale idea of the current state turns the television on rather than
/// off. It is only used as a fallback, and never when the endpoint disagrees with the intent.</para>
/// </remarks>
internal sealed class SamsungTvController : IDisplayController
{
    private readonly TvSettings _settings;
    private readonly Func<bool> _endpointPresent;
    private readonly Action<string> _persistToken;
    private readonly ILogger _log;
    private readonly SamsungDeviceInfoClient _info = new();

    /// <summary>
    /// The pairing token comes from state.json, not settings.json — it is not something to
    /// hand-edit, and it should not sit in the file people are asked to paste into a bug
    /// report.
    /// </summary>
    private readonly string? _initialToken;

    private SamsungRemoteClient? _remote;
    private DateTimeOffset _lastInfoAt;
    private SamsungDeviceInfo? _cachedInfo;

    public SamsungTvController(
        TvSettings settings,
        string? token,
        Func<bool> endpointPresent,
        Action<string> persistToken,
        ILogger log)
    {
        _settings = settings;
        _initialToken = token;
        _endpointPresent = endpointPresent;
        _persistToken = persistToken;
        _log = log;
    }

    public string Id => "samsung";

    public string DisplayName => $"Samsung television at {_settings.Host}";

    public DisplayCapabilities Capabilities =>
        DisplayCapabilities.Wake | DisplayCapabilities.Sleep | DisplayCapabilities.Volume | DisplayCapabilities.PowerQuery;

    public event EventHandler<DisplayEvent>? Diagnostic;

    /// <summary>
    /// Power state, cheapest and most trustworthy source first.
    /// </summary>
    /// <remarks>
    /// The HDMI audio endpoint being present is the primary sensor: it costs nothing, is
    /// instantaneous, and is the only signal that reflects "the television is on <em>and</em>
    /// showing this input". The network API can cheerfully report "on" while the set is
    /// displaying a games console.
    /// </remarks>
    public async Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct)
    {
        if (_endpointPresent())
        {
            return DisplayPowerState.On;
        }

        SamsungDeviceInfo? info = await GetInfoAsync(ct).ConfigureAwait(false);

        if (info is null)
        {
            return DisplayPowerState.Unreachable;
        }

        if (info.IsStandby)
        {
            return DisplayPowerState.Standby;
        }

        // It answered, so it is at least network-awake — but the HDMI endpoint is absent,
        // which means it is not showing us. Standby is the honest answer.
        return info.IsOn ? DisplayPowerState.On : DisplayPowerState.Standby;
    }

    public async Task<bool> WakeAsync(CancellationToken ct)
    {
        byte[]? mac = await ResolveMacAsync(ct).ConfigureAwait(false);
        if (mac is null)
        {
            Report("No MAC address for the television, so it cannot be woken. Set one on the TV tab.", isError: true);
            return false;
        }

        IPAddress? address = IPAddress.TryParse(_settings.Host, out IPAddress? parsed) ? parsed : null;
        int sent = await WakeOnLan.SendAsync(mac, address, _log, ct: ct).ConfigureAwait(false);

        if (sent == 0)
        {
            Report("Could not send any Wake-on-LAN packets.", isError: true);
            return false;
        }

        Report($"Sent {sent} Wake-on-LAN packet(s) to {WakeOnLan.FormatMac(mac)}.");

        // Give the set time to wake and Windows time to re-add the HDMI endpoint.
        DateTimeOffset deadline = DateTimeOffset.Now.AddMilliseconds(_settings.WaitForEndpointMs);
        while (DateTimeOffset.Now < deadline && !ct.IsCancellationRequested)
        {
            if (_endpointPresent())
            {
                Report("The television is awake and the HDMI output is back.");
                return true;
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        Report(
            "The television did not come back within the wait period. If it is connected over Wi-Fi, Wake-on-LAN may not be supported at all.",
            isError: true);
        return false;
    }

    public async Task<bool> SleepAsync(CancellationToken ct)
    {
        if (!_endpointPresent())
        {
            // Refuse to send a power command that disagrees with the endpoint. With
            // KEY_POWER being a toggle, this is exactly how an app turns a TV *on* by
            // mistake at three in the morning.
            Report("Not sending a power-off: the television already appears to be off.");
            return false;
        }

        SamsungRemoteClient remote = GetRemote();

        if (await remote.SendKeyAsync(SamsungKeys.PowerOff, ct).ConfigureAwait(false))
        {
            Report("Sent power-off to the television.");
            return true;
        }

        if (await remote.SendKeyAsync(SamsungKeys.Power, ct).ConfigureAwait(false))
        {
            Report("Sent the power toggle to the television (KEY_POWEROFF was not accepted).");
            return true;
        }

        Report("Could not reach the television to turn it off.", isError: true);
        return false;
    }

    public async Task<bool> SendVolumeAsync(VolumeCommand command, CancellationToken ct)
    {
        string key = command switch
        {
            VolumeCommand.Up => SamsungKeys.VolumeUp,
            VolumeCommand.Down => SamsungKeys.VolumeDown,
            _ => SamsungKeys.Mute,
        };

        return await GetRemote().SendKeyAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>Connects and waits for the on-screen prompt to be accepted.</summary>
    public async Task<bool> PairAsync(CancellationToken ct)
    {
        Report("Connecting… look at your television and choose Allow.");
        bool connected = await GetRemote().ConnectAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        Report(connected
            ? "Paired. The television will not ask again."
            : "Pairing failed. Make sure the television is switched on and on the same network.", isError: !connected);

        return connected;
    }

    private SamsungRemoteClient GetRemote()
    {
        if (_remote is not null)
        {
            return _remote;
        }

        _remote = new SamsungRemoteClient(_settings.Host, _initialToken, _log);
        _remote.TokenReceived += (_, token) => _persistToken(token);
        return _remote;
    }

    private async Task<SamsungDeviceInfo?> GetInfoAsync(CancellationToken ct)
    {
        // Polled sparingly: this is a network round trip and the endpoint check above
        // answers the common case for free.
        if (_cachedInfo is not null && DateTimeOffset.Now - _lastInfoAt < TimeSpan.FromSeconds(30))
        {
            return _cachedInfo;
        }

        if (!IPAddress.TryParse(_settings.Host, out IPAddress? address))
        {
            return null;
        }

        _cachedInfo = await _info.TryGetAsync(address, ct).ConfigureAwait(false);
        _lastInfoAt = DateTimeOffset.Now;
        return _cachedInfo;
    }

    private async Task<byte[]?> ResolveMacAsync(CancellationToken ct)
    {
        if (WakeOnLan.TryParseMac(_settings.MacAddress, out byte[] configured))
        {
            return configured;
        }

        if (IPAddress.TryParse(_settings.Host, out IPAddress? address))
        {
            // ARP first: it reports the adapter actually carrying this IP, whereas the
            // television's own API often hands back its Wi-Fi MAC even when wired.
            byte[]? arp = WakeOnLan.TryResolveMacViaArp(address);
            if (arp is not null)
            {
                _log.LogInformation("Resolved the television's MAC via ARP: {Mac}", WakeOnLan.FormatMac(arp));
                return arp;
            }
        }

        SamsungDeviceInfo? info = await GetInfoAsync(ct).ConfigureAwait(false);
        if (info?.Mac is { } reported && WakeOnLan.TryParseMac(reported, out byte[] fromApi))
        {
            _log.LogWarning(
                "Using the MAC the television reported ({Mac}). On a wired set this is often the Wi-Fi radio's address and will not wake it; set the correct one manually if waking fails.",
                WakeOnLan.FormatMac(fromApi));
            return fromApi;
        }

        return null;
    }

    private void Report(string message, bool isError = false)
    {
        if (isError)
        {
            _log.LogWarning("{Message}", message);
        }
        else
        {
            _log.LogInformation("{Message}", message);
        }

        Diagnostic?.Invoke(this, new DisplayEvent(message, isError));
    }

    public async ValueTask DisposeAsync()
    {
        if (_remote is not null)
        {
            await _remote.DisposeAsync().ConfigureAwait(false);
        }

        _info.Dispose();
    }
}
