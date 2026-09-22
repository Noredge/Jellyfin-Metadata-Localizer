using System.Net;
using Localizer.Translation;

internal static class PublicCloudEndpointChecks
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("public_endpoint_preserves_custom_paths_and_ports", () =>
        {
            Expect(PublicCloudEndpoint.Canonicalize("HTTPS://API.EXAMPLE.COM/v1/") == "https://api.example.com/v1");
            Expect(PublicCloudEndpoint.Canonicalize("https://api.example.com/api/v3") == "https://api.example.com/api/v3");
            Expect(PublicCloudEndpoint.Canonicalize("https://api.example.com/team@api/v3") == "https://api.example.com/team@api/v3");
            Expect(PublicCloudEndpoint.Canonicalize("https://api.example.com:8443/") == "https://api.example.com:8443");
            Expect(PublicCloudEndpoint.Canonicalize("https://api.example.com./") == "https://api.example.com");
            Expect(PublicCloudEndpoint.Canonicalize("https://1.1.1.1/v1") == "https://1.1.1.1/v1");
            Expect(PublicCloudEndpoint.Canonicalize("https://[2606:4700:4700::1111]/") == "https://[2606:4700:4700::1111]");
            return Task.CompletedTask;
        });
        await check("public_endpoint_rejects_ambiguous_or_non_https_bases", () =>
        {
            foreach (var endpoint in new[]
            {
                "", " https://api.example.com/v1", "https://api.example.com/v 1", "https://api.example.com/\n",
                "http://api.example.com/v1", "ftp://api.example.com", "https://key@api.example.com",
                "https://@api.example.com/v1", "https://api.example.com../v1",
                "https://api.example.com/v1?key=secret", "https://api.example.com/v1#section",
                "https://api.example.com/v1?", "https://api.example.com/v1#", "https://api.example.com\\v1",
                "https://api.example.com:0/v1", "https://api.example.com:65536/v1", "https://-bad.example.com", "https://api..example.com"
            }) Reject(endpoint);
            return Task.CompletedTask;
        });
        await check("public_endpoint_rejects_local_and_reserved_hostnames", () =>
        {
            foreach (var endpoint in new[]
            {
                "https://localhost", "https://server", "https://api.localhost", "https://api.localhost.",
                "https://api.local", "https://api.internal", "https://api.lan", "https://api.home.arpa",
                "https://api.onion", "https://api.invalid", "https://api.test", "https://127.1", "https://2130706433",
                "https://0x7f000001", "https://[::1]", "https://[::ffff:127.0.0.1]", "https://10.0.0.1/v1"
            }) Reject(endpoint);
            return Task.CompletedTask;
        });
        await check("public_endpoint_blocks_special_ipv4_ranges", () =>
        {
            foreach (var ip in new[]
            {
                "0.0.0.0", "0.2.3.4", "10.0.0.1", "100.64.0.1", "100.127.255.254", "127.0.0.1",
                "169.254.169.254", "172.16.0.1", "172.31.255.254", "192.0.0.1", "192.0.2.1",
                "192.88.99.1", "192.168.0.1", "198.18.0.1", "198.19.255.254", "198.51.100.1",
                "203.0.113.1", "224.0.0.1", "239.255.255.255", "240.0.0.1", "255.255.255.255"
            }) Expect(!PublicCloudEndpoint.IsPublicAddress(IPAddress.Parse(ip)), ip);
            foreach (var ip in new[] { "1.1.1.1", "8.8.8.8", "100.63.255.254", "100.128.0.1", "172.15.255.254", "172.32.0.1", "198.20.0.1", "223.255.255.254" })
                Expect(PublicCloudEndpoint.IsPublicAddress(IPAddress.Parse(ip)), ip);
            return Task.CompletedTask;
        });
        await check("public_endpoint_blocks_special_ipv6_and_mapped_private_ranges", () =>
        {
            foreach (var ip in new[]
            {
                "::", "::1", "::127.0.0.1", "::ffff:127.0.0.1", "::ffff:10.0.0.1", "::ffff:192.0.2.1",
                "fc00::1", "fdff::1", "fe80::1", "fec0::1", "ff02::1", "64:ff9b::a00:1", "64:ff9b:1::1",
                "100::1", "2001::1", "2001:2::1", "2001:20::1", "2001:db8::1", "2002:7f00:1::1", "3ffe::1", "3fff::1", "3fff:fff::1"
            }) Expect(!PublicCloudEndpoint.IsPublicAddress(IPAddress.Parse(ip)), ip);
            foreach (var ip in new[] { "2606:4700:4700::1111", "2001:4860:4860::8888", "::ffff:8.8.8.8" })
                Expect(PublicCloudEndpoint.IsPublicAddress(IPAddress.Parse(ip)), ip);
            Expect(!PublicCloudEndpoint.IsPublicAddress(new IPAddress(IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes(), 2)));
            return Task.CompletedTask;
        });
        await check("public_endpoint_handler_disables_redirects_and_proxy_dns", () =>
        {
            using var handler = (SocketsHttpHandler)PublicCloudEndpoint.CreateHandler();
            Expect(!handler.AllowAutoRedirect && !handler.UseProxy && handler.ConnectCallback is not null);
            return Task.CompletedTask;
        });
    }

    private static void Reject(string endpoint)
    {
        try { PublicCloudEndpoint.Canonicalize(endpoint); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Accepted an invalid endpoint: " + endpoint);
    }

    private static void Expect(bool result, string? detail = null)
    {
        if (!result) throw new InvalidOperationException(detail ?? "Assertion failed.");
    }
}
