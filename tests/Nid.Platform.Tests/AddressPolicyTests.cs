using System.Net;
using Nid.Platform.Outbound;

namespace Nid.Platform.Tests;

public class AddressPolicyTests
{
    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2001:db8::1")]
    public void Refuses_private_loopback_link_local_and_reserved(string address)
        => Assert.False(AddressPolicy.IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("185.199.108.133")]   // raw.githubusercontent.com, as resolved on 2026-09-23
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]        // just outside 172.16/12
    [InlineData("2606:50c0:8000::154")]
    [InlineData("::ffff:8.8.8.8")]
    public void Allows_public_addresses(string address)
        => Assert.True(AddressPolicy.IsAllowed(IPAddress.Parse(address)));
}
