using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NoSilence.Tv.Samsung;

/// <summary>What the television says about itself.</summary>
internal sealed record SamsungDeviceInfo(string Ip, string? Name, string? Model, string? Mac, string? PowerState, bool TokenAuthSupported)
{
    public bool IsOn => string.Equals(PowerState, "on", StringComparison.OrdinalIgnoreCase);

    public bool IsStandby => string.Equals(PowerState, "standby", StringComparison.OrdinalIgnoreCase);

    public string Describe() => $"{Name ?? "Samsung TV"} ({Model ?? "unknown model"}) at {Ip}";
}

/// <summary>
/// Reads <c>http://{ip}:8001/api/v2/</c>.
/// </summary>
/// <remarks>
/// Plain HTTP, no authentication, and it answers even in standby when network standby is
/// enabled — which makes it the most reliable way to find a set and to check whether it is
/// merely asleep rather than unplugged.
/// </remarks>
internal sealed class SamsungDeviceInfoClient : IDisposable
{
    private readonly HttpClient _http;

    public SamsungDeviceInfoClient(TimeSpan? timeout = null)
    {
        _http = new HttpClient
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(2),
        };
    }

    public async Task<SamsungDeviceInfo?> TryGetAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            ApiResponse? response = await _http
                .GetFromJsonAsync<ApiResponse>($"http://{address}:8001/api/v2/", ct)
                .ConfigureAwait(false);

            if (response?.Device is not { } device)
            {
                return null;
            }

            return new SamsungDeviceInfo(
                address.ToString(),
                device.Name ?? response.Name,
                device.ModelName,
                device.WifiMac,
                device.PowerState,
                string.Equals(device.TokenAuthSupport, "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or OperationCanceledException)
        {
            // Not a Samsung TV, not reachable, or not answering right now.
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record ApiResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("device")]
        public DeviceBlock? Device { get; init; }
    }

    private sealed record DeviceBlock
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("modelName")]
        public string? ModelName { get; init; }

        /// <summary>
        /// Named "wifiMac" but reported by wired sets too — and on those it is often the
        /// Wi-Fi radio's address, which is useless for waking the wired NIC. Treated as a
        /// hint; ARP is preferred.
        /// </summary>
        [JsonPropertyName("wifiMac")]
        public string? WifiMac { get; init; }

        /// <summary>Only present on newer firmware.</summary>
        [JsonPropertyName("PowerState")]
        public string? PowerState { get; init; }

        [JsonPropertyName("TokenAuthSupport")]
        public string? TokenAuthSupport { get; init; }
    }
}
