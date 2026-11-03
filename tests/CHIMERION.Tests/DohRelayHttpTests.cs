using System.Net.Http;
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

    [Fact]
    public async Task Unresponsive_first_address_does_not_use_up_the_time_for_the_second()
    {
        var addresses = new[] { IPAddress.Parse("203.0.113.1"), IPAddress.Parse("203.0.113.2") };
        var tried = new List<IPAddress>();
        using var connected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Первый адрес молчит (как потерянные пакеты), второй отвечает.
        Task<Socket> Connect(IPEndPoint endpoint, CancellationToken token)
        {
            lock (tried) tried.Add(endpoint.Address);
            return endpoint.Address.Equals(addresses[0])
                ? Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith<Socket>(_ => throw new OperationCanceledException(token), TaskScheduler.Default)
                : Task.FromResult(connected);
        }
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (socket, index) = await DohRelayHttp.ConnectAnyAsync(addresses, 443, 0, Connect, TimeSpan.FromMilliseconds(300), overall.Token);
        Assert.Same(connected, socket);
        Assert.Equal(1, index);
        Assert.Equal(addresses, tried);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"подключение заняло {watch.Elapsed.TotalSeconds:0.0} с");
    }

    [Fact]
    public async Task Next_connection_starts_with_the_address_that_answered()
    {
        var addresses = new[] { IPAddress.Parse("203.0.113.1"), IPAddress.Parse("203.0.113.2") };
        var tried = new List<IPAddress>();
        using var connected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task<Socket> Connect(IPEndPoint endpoint, CancellationToken token) { tried.Add(endpoint.Address); return Task.FromResult(connected); }
        var (_, index) = await DohRelayHttp.ConnectAnyAsync(addresses, 443, 1, Connect, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(1, index);
        Assert.Equal(new[] { addresses[1] }, tried);
    }

    [Fact]
    public async Task All_addresses_failing_reports_a_connection_error()
    {
        var addresses = new[] { IPAddress.Parse("203.0.113.1"), IPAddress.Parse("203.0.113.2") };
        Task<Socket> Connect(IPEndPoint endpoint, CancellationToken token) => Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => DohRelayHttp.ConnectAnyAsync(addresses, 443, 0, Connect, TimeSpan.FromSeconds(1), CancellationToken.None));
    }
}
