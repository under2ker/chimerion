using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Поиск страниц через локальный системный прокси (аудит CR-08). Сайт в сеть не запрашивается: прокси подменен.</summary>
public class SearchProxyTests
{
    /// <summary>Прокси на петле: принимает одно соединение, запоминает первую строку и отвечает 502.</summary>
    private static (WebProxy Proxy, Task<string> FirstLine, TcpListener Listener) FakeProxy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var line = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buf = new byte[1024];
            var n = await stream.ReadAsync(buf);
            var text = Encoding.ASCII.GetString(buf, 0, n);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"));
            return text.Split("\r\n")[0];
        });
        return (new WebProxy($"http://127.0.0.1:{port}") { BypassProxyOnLocal = false }, line, listener);
    }

    [Fact]
    public async Task Local_proxy_is_used_for_public_site()
    {
        var (proxy, firstLine, listener) = FakeProxy();
        try
        {
            using var http = SourceParser.CreateHttp(proxy);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("https://1.1.1.1/"));
            Assert.Equal("CONNECT 1.1.1.1:443 HTTP/1.1", await firstLine.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Private_site_is_refused_even_through_proxy()
    {
        var (proxy, firstLine, listener) = FakeProxy();
        try
        {
            using var http = SourceParser.CreateHttp(proxy);
            var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("https://192.168.1.1/"));
            Assert.Contains("не публичный", error.ToString());
            Assert.False(firstLine.IsCompleted); // до прокси дело не дошло
        }
        finally { listener.Stop(); }
    }
}
