using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NoSilence.Tv.Samsung;

/// <summary>
/// Finds Samsung televisions by sweeping the local subnet.
/// </summary>
/// <remarks>
/// A brute-force HTTP sweep rather than SSDP. It is less elegant but far more reliable
/// across firmware generations, and with 32 requests in flight and a short timeout a /24
/// completes in a few seconds.
/// </remarks>
internal sealed class SamsungDiscovery
{
    private readonly ILogger<SamsungDiscovery> _log;

    public SamsungDiscovery(ILogger<SamsungDiscovery> log) => _log = log;

    /// <summary>Guesses the subnet to sweep from the local interfaces, e.g. <c>192.168.1</c>.</summary>
    public static IReadOnlyList<string> LocalSubnets()
    {
        var subnets = new List<string>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                byte[] ip = address.Address.GetAddressBytes();

                // Private ranges only: sweeping a public range would be rude and pointless.
                bool isPrivate = ip[0] == 10
                    || (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31)
                    || (ip[0] == 192 && ip[1] == 168);

                if (!isPrivate)
                {
                    continue;
                }

                string subnet = $"{ip[0]}.{ip[1]}.{ip[2]}";
                if (!subnets.Contains(subnet, StringComparer.Ordinal))
                {
                    subnets.Add(subnet);
                }
            }
        }

        return subnets;
    }

    public async Task<IReadOnlyList<SamsungDeviceInfo>> SweepAsync(string? subnet, CancellationToken ct)
    {
        IReadOnlyList<string> subnets = subnet is { Length: > 0 } ? [subnet.TrimEnd('.')] : LocalSubnets();

        if (subnets.Count == 0)
        {
            _log.LogWarning("No private IPv4 subnet found to sweep.");
            return [];
        }

        var found = new List<SamsungDeviceInfo>();

        foreach (string prefix in subnets)
        {
            _log.LogInformation("Sweeping {Prefix}.1-254 for Samsung televisions.", prefix);

            using var client = new SamsungDeviceInfoClient(TimeSpan.FromMilliseconds(700));
            using var gate = new SemaphoreSlim(32);

            IEnumerable<Task<SamsungDeviceInfo?>> probes = Enumerable.Range(1, 254).Select(async host =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return IPAddress.TryParse($"{prefix}.{host}", out IPAddress? address)
                        ? await client.TryGetAsync(address, ct).ConfigureAwait(false)
                        : null;
                }
                finally
                {
                    gate.Release();
                }
            });

            SamsungDeviceInfo?[] results = await Task.WhenAll(probes).ConfigureAwait(false);
            found.AddRange(results.OfType<SamsungDeviceInfo>());
        }

        _log.LogInformation("Discovery found {Count} television(s).", found.Count);
        return found;
    }
}
