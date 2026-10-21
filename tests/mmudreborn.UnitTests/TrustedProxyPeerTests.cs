using System.Net;
using CWGamingServ;
using Xunit;

namespace mmudreborn.UnitTests;

// A PROXY-protocol header may set the client's recorded IP only when the raw socket peer is a proxy on
// this host. Anyone else sending "PROXY TCP4 <ip> ..." would otherwise put an arbitrary IP on their
// account (LastIpAddress, ;who, logs) and could get that innocent address banned.
public sealed class TrustedProxyPeerTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_peer_is_trusted(string address)
    {
        Assert.True(TelnetClient.IsTrustedProxyPeer(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("192.168.1.50")]
    [InlineData("10.0.0.5")]
    [InlineData("172.17.32.1")]
    [InlineData("::ffff:203.0.113.7")]
    [InlineData("2001:db8::1")]
    public void Non_loopback_peer_is_not_trusted(string address)
    {
        Assert.False(TelnetClient.IsTrustedProxyPeer(IPAddress.Parse(address)));
    }

    [Fact]
    public void Unknown_peer_is_not_trusted()
    {
        Assert.False(TelnetClient.IsTrustedProxyPeer(null));
    }
}
