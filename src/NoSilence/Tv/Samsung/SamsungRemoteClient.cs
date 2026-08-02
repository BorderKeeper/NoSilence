using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace NoSilence.Tv.Samsung;

/// <summary>Samsung remote key codes we use.</summary>
internal static class SamsungKeys
{
    /// <summary>A toggle on many sets — see the warning on <see cref="SamsungTvController"/>.</summary>
    public const string Power = "KEY_POWER";

    /// <summary>Unambiguous, but only implemented on some firmware.</summary>
    public const string PowerOff = "KEY_POWEROFF";

    public const string VolumeUp = "KEY_VOLUP";
    public const string VolumeDown = "KEY_VOLDOWN";
    public const string Mute = "KEY_MUTE";
}

/// <summary>
/// The WebSocket remote control channel.
/// </summary>
/// <remarks>
/// Only usable while the television is already awake: in standby it closes the port
/// entirely, which is why power-on is Wake-on-LAN and this is only good for power-off and
/// volume.
/// <para>
/// The certificate is self-signed, so validation is bypassed — <b>scoped to this one socket
/// instance</b>, never through <c>ServicePointManager</c>, which would silently disable
/// validation for the whole process. Worth being loud about, since "we turn off TLS
/// verification" deserves to be visible.
/// </para>
/// </remarks>
internal sealed class SamsungRemoteClient : IAsyncDisposable
{
    private const string ClientName = "NoSilence";

    private readonly string _host;
    private readonly ILogger _log;

    private ClientWebSocket? _socket;
    private string? _token;
    private bool _preferInsecurePort;

    public SamsungRemoteClient(string host, string? token, ILogger log)
    {
        _host = host;
        _token = token;
        _log = log;
    }

    /// <summary>The pairing token, once the television has issued one. Persist it.</summary>
    public string? Token => _token;

    /// <summary>Raised when a token is issued or renewed, so it can be saved.</summary>
    public event EventHandler<string>? TokenReceived;

    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_socket is { State: WebSocketState.Open })
        {
            return true;
        }

        await CloseAsync().ConfigureAwait(false);

        // Newer sets are wss on 8002; 2016-era Tizen is ws on 8001. Remember which worked.
        bool[] attempts = _preferInsecurePort ? [false, true] : [true, false];

        foreach (bool secure in attempts)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            if (secure)
            {
                // Scoped to this socket only. Never touch the global callback.
                socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            string name = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientName));
            string scheme = secure ? "wss" : "ws";
            int port = secure ? 8002 : 8001;
            string url = $"{scheme}://{_host}:{port}/api/v2/channels/samsung.remote.control?name={name}";

            if (!string.IsNullOrEmpty(_token))
            {
                url += $"&token={_token}";
            }

            try
            {
                await socket.ConnectAsync(new Uri(url), linked.Token).ConfigureAwait(false);
                _socket = socket;
                _preferInsecurePort = !secure;

                await ReadConnectFrameAsync(linked.Token).ConfigureAwait(false);
                _log.LogInformation("Connected to the television over {Scheme}.", scheme);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                socket.Dispose();
                _log.LogDebug(ex, "Could not connect over {Scheme}://{Host}:{Port}.", scheme, _host, port);
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the channel-connect frame, which carries the pairing token.
    /// </summary>
    /// <remarks>
    /// On a first pairing the television shows an on-screen "allow this device?" prompt and
    /// this frame does not arrive until the user accepts — hence the generous timeout at the
    /// call site and the instruction to go and look at the TV.
    /// </remarks>
    private async Task ReadConnectFrameAsync(CancellationToken ct)
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[8192];
        WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
        string json = Encoding.UTF8.GetString(buffer, 0, result.Count);

        try
        {
            ConnectFrame? frame = JsonSerializer.Deserialize<ConnectFrame>(json);

            if (string.Equals(frame?.Event, "ms.channel.unauthorized", StringComparison.Ordinal))
            {
                throw new WebSocketException("The television refused the connection. Accept the prompt on screen and try again.");
            }

            if (frame?.Data?.Token is { Length: > 0 } token && !string.Equals(token, _token, StringComparison.Ordinal))
            {
                _token = token;
                TokenReceived?.Invoke(this, token);
                _log.LogInformation("Received a pairing token from the television.");
            }
        }
        catch (JsonException)
        {
            _log.LogDebug("Unrecognised connect frame: {Json}", json);
        }
    }

    public async Task<bool> SendKeyAsync(string key, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!await ConnectAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            {
                return false;
            }

            string payload = JsonSerializer.Serialize(new
            {
                method = "ms.remote.control",
                @params = new
                {
                    Cmd = "Click",
                    DataOfCmd = key,
                    Option = "false",
                    TypeOfRemote = "SendRemoteKey",
                },
            });

            try
            {
                await _socket!.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, endOfMessage: true, ct)
                    .ConfigureAwait(false);

                _log.LogInformation("Sent {Key} to the television.", key);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The socket went stale between connect and send; retry once with a fresh one.
                _log.LogDebug(ex, "Send failed; reconnecting.");
                await CloseAsync().ConfigureAwait(false);
            }
        }

        return false;
    }

    public async Task CloseAsync()
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing a broken socket is not worth reporting.
        }
        finally
        {
            _socket.Dispose();
            _socket = null;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private sealed record ConnectFrame
    {
        [JsonPropertyName("event")]
        public string? Event { get; init; }

        [JsonPropertyName("data")]
        public ConnectData? Data { get; init; }
    }

    private sealed record ConnectData
    {
        [JsonPropertyName("token")]
        public string? Token { get; init; }
    }
}
