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
    /// <summary>
    /// Works out whether the television is actually on.
    /// </summary>
    /// <remarks>
    /// The television's own report wins over the HDMI audio endpoint, which sounds backwards
    /// and is not. Endpoint presence was the original primary sensor on the reasoning that
    /// Windows deletes the endpoint when a TV powers off — and it does, when the set is
    /// switched off with its remote. But a <c>KEY_POWEROFF</c> standby on this set leaves the
    /// HDMI link asserted, so Windows keeps the endpoint Active while the screen is dark.
    /// Trusting the endpoint there makes the app conclude "already on" and refuse to wake it.
    /// <para>
    /// So: an explicit standby report is believed outright; the endpoint is only consulted
    /// when the set reports nothing useful, or cannot be reached at all.
    /// </para>
    /// </remarks>
    public async Task<DisplayPowerState> GetPowerStateAsync(CancellationToken ct)
    {
        SamsungDeviceInfo? info = await GetInfoAsync(ct).ConfigureAwait(false);

        if (info?.IsStandby == true)
        {
            return DisplayPowerState.Standby;
        }

        if (info?.IsOn == true)
        {
            // On, though not necessarily showing this input — the endpoint tells us that.
            return DisplayPowerState.On;
        }

        if (_endpointPresent())
        {
            return DisplayPowerState.On;
        }

        return info is null ? DisplayPowerState.Unreachable : DisplayPowerState.Standby;
    }

    /// <summary>
    /// Turns the television on, trying both mechanisms.
    /// </summary>
    /// <remarks>
    /// Wake-on-LAN is sent first because it is the only option for a set that shuts its
    /// network ports down in standby. But not every set does: this one keeps 8001, 8002 and
    /// 8080 open and answers its device-info API while asleep, and simply ignores magic
    /// packets — verified by sending 54 of them from three interfaces with no effect. For
    /// those, KEY_POWER over the remote channel works and is deterministic.
    /// <para>
    /// KEY_POWER is a <b>toggle</b>, so it is only ever sent once we are satisfied the set is
    /// actually asleep: the HDMI endpoint must be absent <em>and</em> the television must
    /// either report standby or not report a power state at all. Getting that wrong turns a
    /// television off instead of on.
    /// </para>
    /// </remarks>
    public async Task<bool> WakeAsync(CancellationToken ct)
    {
        _cachedInfo = null;   // never decide power state from a stale cache
        DisplayPowerState state = await GetPowerStateAsync(ct).ConfigureAwait(false);

        if (state == DisplayPowerState.On)
        {
            Report("The television is already on.");
            return true;
        }

        Report($"The television reports {state.ToString().ToLowerInvariant()}; waking it.");
        bool attempted = false;

        // 1. Wake-on-LAN. Harmless when unsupported, and essential when the set powers its
        //    network interface down.
        byte[]? mac = await ResolveMacAsync(ct).ConfigureAwait(false);
        if (mac is not null)
        {
            IPAddress? address = IPAddress.TryParse(_settings.Host, out IPAddress? parsed) ? parsed : null;
            int sent = await WakeOnLan.SendAsync(mac, address, _log, ct: ct).ConfigureAwait(false);
            attempted |= sent > 0;
            Report($"Sent {sent} Wake-on-LAN packet(s) to {WakeOnLan.FormatMac(mac)}.");
        }
        else
        {
            Report("No MAC address for the television, so Wake-on-LAN was skipped.");
        }

        // 2. Give Wake-on-LAN a few seconds, then fall back to the remote channel. On sets
        //    that keep their ports open in standby — this one does — that is the mechanism
        //    that actually works.
        if (await WaitForOnAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
        {
            Report("The television is awake.");
            return true;
        }

        if (await GetPowerStateAsync(ct).ConfigureAwait(false) == DisplayPowerState.Unreachable)
        {
            Report("The television is not answering on the network, so the remote cannot be used to turn it on.", isError: true);
            return false;
        }

        Report("Wake-on-LAN had no effect; sending the power key over the network instead.");
        attempted |= await GetRemote().SendKeyAsync(SamsungKeys.Power, ct).ConfigureAwait(false);

        if (!attempted)
        {
            Report("Could not reach the television by any means.", isError: true);
            return false;
        }

        if (await WaitForOnAsync(TimeSpan.FromMilliseconds(_settings.WaitForEndpointMs), ct).ConfigureAwait(false))
        {
            Report(_endpointPresent()
                ? "The television is awake and the HDMI output is available."
                : "The television is awake, but it is not showing this PC's input.");
            return true;
        }

        Report(
            "The television did not wake within the wait period. Check that Network Standby is enabled on the TV.",
            isError: true);
        return false;
    }

    /// <summary>
    /// Polls until the television reports itself on.
    /// </summary>
    /// <remarks>
    /// Deliberately not a wait on the audio endpoint: this set keeps the endpoint Active
    /// while in standby, so waiting on it would return true instantly and report success for
    /// a television that never woke.
    /// </remarks>
    private async Task<bool> WaitForOnAsync(TimeSpan timeout, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.Now + timeout;

        while (DateTimeOffset.Now < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);

            _cachedInfo = null;
            if (await GetPowerStateAsync(ct).ConfigureAwait(false) == DisplayPowerState.On)
            {
                return true;
            }
        }

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
