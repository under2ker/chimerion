using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohLocalListenerTests
{
    private sealed class AnswerHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            body[2] |= 0x80;
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    [Fact]
    public async Task Listens_on_loopback_and_forwards_udp_and_tcp()
    {
        using var http = new HttpClient(new AnswerHandler());
        var transport = new DohRelayTransport(http, new Uri("https://dns.example.test/dns-query"));
        await using var listener = new DohLocalListener(IPAddress.Loopback, 0, transport);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var query = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        using var udp = new UdpClient();
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, listener.Port), timeout.Token);
        var response = await udp.ReceiveAsync(timeout.Token);
        Assert.Equal(0x81, response.Buffer[2]);
        Assert.Equal(query[0], response.Buffer[0]);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, listener.Port, timeout.Token);
        var stream = tcp.GetStream();
        await stream.WriteAsync(new byte[] { 0, (byte)query.Length }, timeout.Token);
        await stream.WriteAsync(query, timeout.Token);
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, timeout.Token);
        var body = new byte[(header[0] << 8) | header[1]];
        await stream.ReadExactlyAsync(body, timeout.Token);
        Assert.Equal(0x81, body[2]);
        Assert.Equal(query[0], body[0]);
    }

    [Fact]
    public void Occupied_udp_port_does_not_leave_tcp_port_open()
    {
        using var occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        using var http = new HttpClient(new AnswerHandler());
        var transport = new DohRelayTransport(http, new Uri("https://dns.example.test/dns-query"));
        Assert.Throws<SocketException>(() => new DohLocalListener(IPAddress.Loopback, port, transport));
        var tcp = new TcpListener(IPAddress.Loopback, port);
        try { tcp.Start(); }
        finally { tcp.Stop(); }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task Dispose_cancels_in_flight_queries_before_returning()
    {
        var handler = new WaitingHandler();
        using var http = new HttpClient(handler);
        var transport = new DohRelayTransport(http, new Uri("https://dns.example.test/dns-query"));
        var listener = new DohLocalListener(IPAddress.Loopback, 0, transport);
        listener.Start();
        using var udp = new UdpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var query = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, listener.Port), timeout.Token);
        await handler.Started.Task.WaitAsync(timeout.Token);
        await listener.DisposeAsync();
        Assert.True(handler.Cancelled);
    }

    [Fact]
    public void Rejects_public_bind_address()
    {
        using var http = new HttpClient(new AnswerHandler());
        var transport = new DohRelayTransport(http, new Uri("https://dns.example.test/dns-query"));
        Assert.Throws<ArgumentException>(() => new DohLocalListener(IPAddress.Any, 0, transport));
    }
}
