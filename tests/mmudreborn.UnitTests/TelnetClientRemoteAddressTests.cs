using System.Net;
using System.Net.Sockets;
using CWGamingServ;
using Xunit;

namespace mmudreborn.UnitTests;

// The login ghost sweep logs RemoteAddress for connections the host has already closed. A disposed
// TcpClient hands back a null Client, so reading the address off the live socket threw, inside the
// NEW login's session, and killed it. Every later login for that account was dropped the same way.
public sealed class TelnetClientRemoteAddressTests
{
    [Fact]
    public async Task Remote_address_is_still_readable_after_the_connection_is_closed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var client = new TelnetClient(accepted);
            Assert.Equal("127.0.0.1", client.RemoteAddress);

            client.Disconnect();

            Assert.Equal("127.0.0.1", client.RemoteAddress);
        }
        finally
        {
            listener.Stop();
        }
    }
}
