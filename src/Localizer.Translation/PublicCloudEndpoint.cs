using System.Net;
using System.Net.Sockets;

namespace Localizer.Translation;

/// <summary>Validates public HTTPS API bases and connects only to checked public addresses.</summary>
public static class PublicCloudEndpoint
{
    public static string Canonicalize(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048 || endpoint.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || endpoint.Contains('\\') || endpoint.Contains('?') || endpoint.Contains('#')
            || !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || endpoint[8..].Split('/', 2)[0].Contains('@')
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port < 1 || uri.Port > 65535)
            throw new ArgumentException("A public HTTPS API base URL is required.", nameof(endpoint));

        var host = uri.IdnHost.EndsWith('.') ? uri.IdnHost[..^1] : uri.IdnHost;
        if (!IsAllowedHost(host))
            throw new ArgumentException("The API host must be a public IP address or public DNS name.", nameof(endpoint));

        var canonical = new UriBuilder(uri) { Host = host }.Uri.AbsoluteUri;
        return canonical.TrimEnd('/');
    }

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectPublicAsync
    };

    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return false;
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Private, loopback, shared, link-local, documentation, benchmark,
            // protocol-assignment, multicast and future-reserved IPv4 ranges.
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127
                && !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                && !(bytes[0] == 192 && bytes[1] == 0 && (bytes[2] == 0 || bytes[2] == 2))
                && !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] < 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // Restrict IPv6 to ordinary global unicast. Transition/translation ranges
        // can encode private destinations, so they are deliberately not accepted.
        return (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xfe) == 0)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x3f && bytes[1] == 0xfe)
            && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    private static bool IsAllowedHost(string host)
    {
        if (IPAddress.TryParse(host, out var address)) return IsPublicAddress(address);
        if (host.Length > 253 || !host.Contains('.') || host.Contains('%')) return false;
        var lower = host.ToLowerInvariant();
        if (new[] { "localhost", "local", "internal", "lan", "home", "home.arpa", "onion", "invalid", "test" }
            .Any(suffix => lower == suffix || lower.EndsWith("." + suffix, StringComparison.Ordinal))) return false;
        return host.Split('.').All(label => label.Length is >= 1 and <= 63
            && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host.TrimEnd('.');
        if (!IsAllowedHost(host)) throw new HttpRequestException("The API host is not public.");
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        // Reject the entire answer set before opening any connection, rather than
        // skipping private answers. The socket uses the checked IP, never a second DNS lookup.
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("The API host resolved to a non-public address.");

        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                // SocketsHttpHandler performs TLS above this stream using the original
                // request hostname, retaining normal certificate and SNI validation.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) { socket.Dispose(); lastError = error; }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Unable to connect to the public API host.", lastError);
    }
}
