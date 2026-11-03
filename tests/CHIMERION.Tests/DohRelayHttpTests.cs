using System.Net;
using System.Net.Sockets;
using System.Text;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayHttpTests
{
    [Fact]
    public async Task Uses_supplied_ip_without_resolving_the_endpoint_name()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = socket.GetStream();
                var buf = new byte[2048];
                var count = await stream.ReadAsync(buf, timeout.Token);
                var request = Encoding.ASCII.GetString(buf, 0, count);
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                await stream.WriteAsync(response, timeout.Token);
                return request;
            });
            using var client = DohRelayHttp.Create(new[] { IPAddress.Loopback });
            Assert.Equal("OK", await client.GetStringAsync($"http://not-resolvable.invalid:{port}/", timeout.Token));
            Assert.Contains("not-resolvable.invalid", await server);
        }
        finally { listener.Stop(); }
    }
}
