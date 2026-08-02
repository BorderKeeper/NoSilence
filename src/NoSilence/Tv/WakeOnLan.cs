using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NoSilence.Interop;

namespace NoSilence.Tv;

/// <summary>
/// Sends Wake-on-LAN magic packets.
/// </summary>
/// <remarks>
/// Power-on for a Samsung television is Wake-on-LAN and nothing else: in standby the set
/// closes its WebSocket port entirely, so the remote API cannot be used to turn it on.
/// <para>
/// The packet is sent to the broadcast address, the directed subnet broadcast <em>and</em>
/// the unicast address, on ports 9 and 7, from <b>every</b> local IPv4 interface in turn.
/// That looks excessive and is not: with a VPN, Hyper-V vSwitch or WSL adapter present — and
/// this machine has several — an unbound broadcast routinely leaves through the wrong
/// adapter and never reaches the television. It is the single most common reason
/// Wake-on-LAN "does not work".
/// </para>
/// </remarks>
internal static class WakeOnLan
{
    private static readonly int[] Ports = [9, 7];

    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string cleaned = text.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (cleaned.Length != 12)
        {
            return false;
        }

        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        mac = bytes;
        return true;
    }

    public static string FormatMac(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Builds the 102-byte magic packet: six 0xFF bytes then the MAC sixteen times.</summary>
    public static byte[] BuildPacket(byte[] mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        if (mac.Length != 6)
        {
            throw new ArgumentException("A MAC address is six bytes.", nameof(mac));
        }

        var packet = new byte[102];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (int i = 1; i <= 16; i++)
        {
            mac.CopyTo(packet.AsSpan(i * 6, 6));
        }

        return packet;
    }

    /// <summary>
    /// Sends the packet everywhere it might plausibly need to go.
    /// </summary>
    /// <param name="unicastAddress">The TV's IP, when known. Some sets answer only this.</param>
    /// <param name="repeats">Packets are cheap and UDP is lossy; three is a reasonable belt.</param>
    /// <returns>How many packets were actually put on the wire.</returns>
    public static async Task<int> SendAsync(
        byte[] mac,
        IPAddress? unicastAddress,
        ILogger log,
        int repeats = 3,
        int intervalMs = 150,
        CancellationToken ct = default)
    {
        byte[] packet = BuildPacket(mac);
        List<IPAddress> sources = LocalIPv4Addresses().ToList();
        List<IPEndPoint> targets = BuildTargets(unicastAddress, sources);
        int sent = 0;

        log.LogInformation(
            "Sending Wake-on-LAN to {Mac} from {Sources} interface(s) to {Targets} address(es).",
            FormatMac(mac),
            sources.Count,
            targets.Count);

        for (int round = 0; round < repeats && !ct.IsCancellationRequested; round++)
        {
            foreach (IPAddress source in sources)
            {
                using var udp = new UdpClient(new IPEndPoint(source, 0)) { EnableBroadcast = true };

                foreach (IPEndPoint target in targets)
                {
                    try
                    {
                        await udp.SendAsync(packet, target, ct).ConfigureAwait(false);
                        sent++;
                    }
                    catch (SocketException ex)
                    {
                        // Routine: a VPN adapter often cannot reach a LAN broadcast address.
                        log.LogTrace(ex, "Wake-on-LAN packet from {Source} to {Target} failed.", source, target);
                    }
                }
            }

            if (round < repeats - 1)
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }

        log.LogInformation("Wake-on-LAN: {Sent} packet(s) sent.", sent);
        return sent;
    }

    private static List<IPEndPoint> BuildTargets(IPAddress? unicast, List<IPAddress> sources)
    {
        var addresses = new List<IPAddress> { IPAddress.Broadcast };

        // Directed subnet broadcasts, derived from each interface's own mask.
        foreach (IPAddress broadcast in DirectedBroadcasts())
        {
            if (!addresses.Any(a => a.Equals(broadcast)))
            {
                addresses.Add(broadcast);
            }
        }

        if (unicast is not null)
        {
            addresses.Add(unicast);
        }

        return [.. addresses.SelectMany(a => Ports.Select(p => new IPEndPoint(a, p)))];
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return address.Address;
                }
            }
        }
    }

    private static IEnumerable<IPAddress> DirectedBroadcasts()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null)
                {
                    continue;
                }

                byte[] ip = address.Address.GetAddressBytes();
                byte[] mask = address.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    broadcast[i] = (byte)(ip[i] | ~mask[i]);
                }

                yield return new IPAddress(broadcast);
            }
        }
    }

    /// <summary>
    /// Asks the ARP table for the MAC actually associated with an IP on this LAN.
    /// </summary>
    /// <remarks>
    /// Preferred over the MAC the television reports about itself. Samsung's device-info API
    /// returns <c>wifiMac</c>, which on an Ethernet-connected set is frequently the Wi-Fi
    /// radio's address — and a magic packet sent to that will never wake the wired NIC.
    /// </remarks>
    public static byte[]? TryResolveMacViaArp(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var mac = new byte[6];
        uint length = (uint)mac.Length;

#pragma warning disable CS0618 // GetAddressBytes ordering is what SendARP expects
        uint destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
#pragma warning restore CS0618

        return NativeMethods.SendARP(destination, 0, mac, ref length) == 0 && length == 6 ? mac : null;
    }
}
