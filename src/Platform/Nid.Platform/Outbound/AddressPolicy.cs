using System.Net;
using System.Net.Sockets;

namespace Nid.Platform.Outbound;

/// <summary>
/// <c>security/application.md</c> §3: addresses an outbound request may never reach, checked after resolution with
/// the platform's own <see cref="IPNetwork"/>, never by parsing text by hand (octal, hexadecimal and mapped forms
/// are what hand-written parsers get wrong).
/// </summary>
public static class AddressPolicy
{
    /// <summary>The well-known NAT64 prefix: judged on its embedded IPv4 address below, never refused wholesale.</summary>
    private static readonly IPNetwork Nat64WellKnown = IPNetwork.Parse("64:ff9b::/96");

    private static readonly IPNetwork[] Refused =
    [
        // IPv4: this network, private, carrier-grade NAT, loopback, link-local (cloud metadata), protocol
        // assignments, documentation, 6to4 relay, benchmarking, multicast, reserved and broadcast.
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),

        // IPv6: IPv4-compatible (embeds IPv4), unspecified, loopback, SIIT IPv4-translated, local-use NAT64,
        // discard, IANA special-purpose (includes 2001:db8::/32 below, kept separate for clarity), documentation
        // (2001:db8::/32 and 3fff::/20), Teredo, reserved, 6to4, unique local, link-local, site-local, multicast.
        // IPv4-mapped addresses are judged on their IPv4 form below; the well-known NAT64 prefix
        // (64:ff9b::/96) is judged on its embedded IPv4 form, so it is deliberately absent from this list.
        IPNetwork.Parse("::/96"), IPNetwork.Parse("::/128"), IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("::ffff:0:0:0/96"), IPNetwork.Parse("64:ff9b:1::/48"), IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("3fff::/20"),
        IPNetwork.Parse("2002::/16"), IPNetwork.Parse("5f00::/16"), IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("fec0::/10"), IPNetwork.Parse("ff00::/8"),
    ];

    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && Nat64WellKnown.Contains(address))
            return IsAllowed(EmbeddedIPv4Of(address));

        foreach (var network in Refused)
            if (network.Contains(address))
                return false;

        return true;
    }

    private static IPAddress EmbeddedIPv4Of(IPAddress nat64Address)
    {
        var bytes = nat64Address.GetAddressBytes();
        return new IPAddress(bytes[12..]);
    }
}
