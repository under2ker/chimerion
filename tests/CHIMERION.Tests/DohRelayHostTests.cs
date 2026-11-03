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
    public async Task Control_channel_of_same_user_is_accepted_without_current_user_only()
    {
        // Программа с правами администратора и посредник без них — один пользователь, но разные владельцы токена
        // по умолчанию. CurrentUserOnly на стороне клиента отказывал (Windows 10 у тестера), проверка теперь своя
        var channel = Guid.NewGuid().ToString("N");
        using var server = new System.IO.Pipes.NamedPipeServerStream("DNEX-doh-" + channel, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        Assert.True(DohRelayHost.OwnedByCurrentUser(server));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var cmd = new byte[5];
            await server.ReadExactlyAsync(cmd);
            await server.WriteAsync("ok\n"u8.ToArray());
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await DohRelayHost.CommandAsync(channel, "ping", timeout.Token));
        await serve.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Invalid_control_command_is_rejected()
    {
        Assert.False(await DohRelayHost.CommandAsync("bad", "stop", CancellationToken.None));
        Assert.False(await DohRelayHost.CommandAsync(Guid.NewGuid().ToString("N"), "unknown", CancellationToken.None));
    }
}
