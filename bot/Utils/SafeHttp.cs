using System.Net;
using System.Net.Sockets;

namespace BlogBot.Utils;

/// <summary>
/// The bot fetches URLs that come from public posts. This handler refuses to connect to loopback, private,
/// link-local or cloud metadata addresses (checked after DNS resolution and on every redirect), so a crafted
/// link can't make the bot read internal services.
/// </summary>
public static class SafeHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(IsPublic).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"Refusing to connect to non-public address for {context.DnsEndPoint.Host}.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                     (address.GetAddressBytes()[0] & 0xFE) == 0xFC); // fc00::/7 unique local

        var b = address.GetAddressBytes();
        return !(b[0] == 10 ||
                 b[0] == 0 ||
                 (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || // carrier-grade NAT
                 (b[0] == 169 && b[1] == 254) ||              // link-local, cloud metadata
                 (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                 (b[0] == 192 && b[1] == 168) ||
                 b[0] >= 224);                                 // multicast and reserved
    }
}
