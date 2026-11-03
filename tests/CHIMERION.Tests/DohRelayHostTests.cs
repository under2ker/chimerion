using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayHostTests
{
    private sealed class GoodUpstream : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token); bytes[2] |= 0x80;
            var body = new ByteArrayContent(bytes); body.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        }
    }

    [Fact]
    public async Task Helper_reports_ready_and_stops_on_command_without_network_changes()
    {
        using var client = new HttpClient(new GoodUpstream());
        var config = DohRelayConfig.Create(new Uri("https://dns.example.test/dns-query/example"), IPAddress.Parse("127.0.0.2"), new[] { IPAddress.Parse("203.0.113.10") });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var host = DohRelayHost.RunAsync(config, client, 0, timeout.Token);
        Assert.True(await DohRelayHost.CommandAsync(config.Channel, "ping", timeout.Token));
        Assert.True(await DohRelayHost.CommandAsync(config.Channel, "stop", timeout.Token));
        await host.WaitAsync(timeout.Token);
    }

    private sealed class OfflineUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Helper_starts_without_waiting_for_internet_after_reboot()
    {
        using var client = new HttpClient(new OfflineUpstream());
        var config = DohRelayConfig.Create(new Uri("https://dns.example.test/dns-query/example"), IPAddress.Parse("127.0.0.2"), new[] { IPAddress.Parse("203.0.113.10") });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var host = DohRelayHost.RunAsync(config, client, 0, timeout.Token);
        Assert.True(await DohRelayHost.CommandAsync(config.Channel, "ping", timeout.Token));
        Assert.True(await DohRelayHost.CommandAsync(config.Channel, "stop", timeout.Token));
        await host.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Invalid_control_command_is_rejected()
    {
        Assert.False(await DohRelayHost.CommandAsync("bad", "stop", CancellationToken.None));
        Assert.False(await DohRelayHost.CommandAsync(Guid.NewGuid().ToString("N"), "unknown", CancellationToken.None));
    }
}
