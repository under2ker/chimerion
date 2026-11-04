using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>DoH-прокси внутри программы: настоящие сокеты на петле, DoH-сервер подменен. Порт любой свободный, DNS системы не меняется.</summary>
[Collection("scenarios")]
public class DohLocalProxyTests : IAsyncLifetime
{
    private static readonly Uri Endpoint = new("https://dns.example.test/dns-query/secret-token");
    private static readonly Uri Other = new("https://dns2.example.test/dns-query/other");
    private static readonly IPAddress[] Upstream = { IPAddress.Parse("203.0.113.10") };

    private sealed class FakeDoh(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }

    /// <summary>Сервер принял соединение и молчит: ответа нет, пока запрос не отменят.</summary>
    private sealed class SilentDoh : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Answering(List<Uri?>? seen = null) => async request =>
    {
        seen?.Add(request.RequestUri);
        var bytes = await request.Content!.ReadAsByteArrayAsync();
        bytes[2] |= 0x80;
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
    };

    private static void UseDoh(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        DohLocalProxy.CreateHttp = _ => new HttpClient(new FakeDoh(respond));

    private readonly Func<DohRelayAddresses, HttpClient> _prevHttp = DohLocalProxy.CreateHttp;
    private readonly int _prevPort = DohLocalProxy.Port;
    private readonly TimeSpan _prevProbe = DohLocalProxy.ProbeTimeout;

    public Task InitializeAsync()
    {
        DohLocalProxy.Port = 0;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await DohLocalProxy.StopAllAsync(_ => { });
        DohLocalProxy.CreateHttp = _prevHttp;
        DohLocalProxy.Port = _prevPort;
        DohLocalProxy.ProbeTimeout = _prevProbe;
    }

    private static async Task<byte[]> AskUdp(DohProxySession proxy, ushort id)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await udp.SendAsync(DnsWire.BuildQuery("chatgpt.com", id), new IPEndPoint(IPAddress.Parse(proxy.ListenAddress), proxy.Port), timeout.Token);
        return (await udp.ReceiveAsync(timeout.Token)).Buffer;
    }

    [Fact]
    public async Task Started_proxy_answers_dns_through_doh_and_stops()
    {
        var seen = new List<Uri?>();
        UseDoh(Answering(seen));
        var log = new List<string>();

        var proxy = await DohLocalProxy.StartAsync(Endpoint, Upstream, null, log.Add);

        Assert.Equal(DohLocalProxy.CandidateAddresses[0], proxy.ListenAddress);
        Assert.True(DohLocalProxy.IsRunning(proxy.ListenAddress));
        var answer = await AskUdp(proxy, 0x1234);
        Assert.Equal(0x12, answer[0]);
        Assert.Equal(0x34, answer[1]);
        Assert.True((answer[2] & 0x80) != 0);
        Assert.All(seen, uri => Assert.Equal(Endpoint, uri));
        Assert.True(seen.Count >= 2, "пробный запрос и запрос клиента");
        Assert.DoesNotContain(log, line => line.Contains("secret-token") || line.Contains("dns.example.test"));

        await DohLocalProxy.StopAsync(proxy.ListenAddress, log.Add);
        Assert.False(DohLocalProxy.IsRunning(proxy.ListenAddress));
    }

    [Fact]
    public async Task Dead_doh_server_fails_before_dns_changes_and_hides_the_token()
    {
        UseDoh(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<DohRelayFailedException>(() => DohLocalProxy.StartAsync(Endpoint, Upstream, null, _ => { }));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
        Assert.Empty(DohLocalProxy.RunningAddresses());
    }

    [Fact]
    public async Task Silent_doh_server_fails_by_timeout()
    {
        DohLocalProxy.ProbeTimeout = TimeSpan.FromMilliseconds(200);
        DohLocalProxy.CreateHttp = _ => new HttpClient(new SilentDoh());
        var error = await Assert.ThrowsAsync<DohRelayFailedException>(() => DohLocalProxy.StartAsync(Endpoint, Upstream, null, _ => { }));
        Assert.Contains("не ответил", error.Message);
        Assert.Empty(DohLocalProxy.RunningAddresses());
    }

    [Fact]
    public async Task Restart_on_preferred_address_works_without_network()
    {
        // После перезагрузки сети еще нет: прокси все равно занимает адрес из записи о подключении
        UseDoh(_ => throw new HttpRequestException("сети нет"));
        var proxy = await DohLocalProxy.StartAsync(Endpoint, Upstream, "127.0.0.4", _ => { });
        Assert.Equal("127.0.0.4", proxy.ListenAddress);
        Assert.True(DohLocalProxy.IsRunning("127.0.0.4"));
    }

    [Fact]
    public async Task Same_link_reuses_proxy_and_other_link_takes_next_address()
    {
        UseDoh(Answering());
        var first = await DohLocalProxy.StartAsync(Endpoint, Upstream, null, _ => { });
        var same = await DohLocalProxy.StartAsync(Endpoint, Upstream, null, _ => { });
        var other = await DohLocalProxy.StartAsync(Other, Upstream, null, _ => { });
        Assert.Equal(first, same);
        Assert.Equal(DohLocalProxy.CandidateAddresses[1], other.ListenAddress);
        Assert.Equal(2, DohLocalProxy.RunningAddresses().Count);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Address_taken_by_another_program_is_skipped()
    {
        UseDoh(Answering());
        DohLocalProxy.Port = FreePort();
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        occupied.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), DohLocalProxy.Port));
        occupied.Listen();

        var proxy = await DohLocalProxy.StartAsync(Endpoint, Upstream, null, _ => { });

        Assert.Equal("127.0.0.3", proxy.ListenAddress);
    }

    [Fact]
    public async Task Preferred_address_taken_by_another_program_is_reported()
    {
        UseDoh(Answering());
        DohLocalProxy.Port = FreePort();
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        occupied.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), DohLocalProxy.Port));
        occupied.Listen();

        var error = await Assert.ThrowsAsync<DohRelayFailedException>(() => DohLocalProxy.StartAsync(Endpoint, Upstream, "127.0.0.2", _ => { }));

        Assert.Contains("занят", error.Message);
        Assert.Empty(DohLocalProxy.RunningAddresses());
    }

    [Fact]
    public async Task Stopping_unknown_address_does_nothing()
    {
        var log = new List<string>();
        await DohLocalProxy.StopAsync("127.0.0.5", log.Add);
        Assert.Empty(log);
    }
}

/// <summary>Уборка прежнего посредника DoH: задания, процессы и файлы. Планировщик подменен.</summary>
[Collection("scenarios")]
public class LegacyRelayCleanupTests
{
    private const string Channel = "f15d22cb599d4cd1b7c0098fd088b6e1";

    private static void RemoveRelayFiles()
    {
        foreach (var path in Directory.GetFiles(Storage.Dir, "doh-relay*.json")) File.Delete(path);
    }

    [Fact]
    public async Task Tasks_and_files_of_old_relay_are_removed_and_foreign_tasks_stay()
    {
        var old = LegacyRelayCleanup.SchtasksRunner;
        var calls = new List<string[]>();
        Directory.CreateDirectory(Storage.Dir);
        try
        {
            RemoveRelayFiles();
            File.WriteAllText(Storage.PathOf("doh-relay-error-" + Channel + ".json"), "{}");
            File.WriteAllText(Storage.PathOf("doh-relay.json"), "{}");
            File.WriteAllText(Storage.PathOf("active.json.keep"), "{}");
            LegacyRelayCleanup.SchtasksRunner = (_, args) =>
            {
                calls.Add(args);
                return args[0] == "/Query"
                    ? (0, $"\"\\CHIMERION DoH {Channel}\",\"N/A\",\"Ready\"\r\n\"\\Other task\",\"N/A\",\"Ready\"\r\n\"\\CHIMERION DoH bad\",\"N/A\",\"Ready\"\r\n")
                    : (0, "");
            };
            var log = new List<string>();

            await LegacyRelayCleanup.RunAsync(log.Add);

            var deleted = calls.Where(c => c[0] == "/Delete").Select(c => c[2]).ToList();
            Assert.Equal(new[] { "CHIMERION DoH " + Channel }, deleted);
            Assert.Empty(Directory.GetFiles(Storage.Dir, "doh-relay*.json"));
            Assert.True(File.Exists(Storage.PathOf("active.json.keep")));
            Assert.Contains(log, line => line.Contains("прежнего посредника"));
        }
        finally
        {
            LegacyRelayCleanup.SchtasksRunner = old;
            File.Delete(Storage.PathOf("active.json.keep"));
            RemoveRelayFiles();
        }
    }

    [Fact]
    public async Task Nothing_found_stays_silent()
    {
        var old = LegacyRelayCleanup.SchtasksRunner;
        Directory.CreateDirectory(Storage.Dir);
        try
        {
            RemoveRelayFiles();
            LegacyRelayCleanup.SchtasksRunner = (_, _) => (0, "\"\\Other task\",\"N/A\",\"Ready\"\r\n");
            var log = new List<string>();
            await LegacyRelayCleanup.RunAsync(log.Add);
            Assert.Empty(log);
        }
        finally { LegacyRelayCleanup.SchtasksRunner = old; }
    }

    [Fact]
    public async Task Running_old_relay_gets_stop_command_over_its_channel()
    {
        var old = LegacyRelayCleanup.SchtasksRunner;
        var channel = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream("DNEX-doh-" + channel, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Assert.True(LegacyRelayCleanup.OwnedByCurrentUser(server));
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var command = new byte[5];
            await server.ReadExactlyAsync(command);
            await server.WriteAsync("ok\n"u8.ToArray());
            return System.Text.Encoding.ASCII.GetString(command);
        });
        try
        {
            LegacyRelayCleanup.SchtasksRunner = (_, args) => args[0] == "/Query" ? (0, $"\"\\CHIMERION DoH {channel}\",\"N/A\",\"Ready\"\r\n") : (0, "");
            var log = new List<string>();
            await LegacyRelayCleanup.RunAsync(log.Add);
            Assert.Equal("stop\n", await serve.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(log, line => line.Contains("процессов 1"));
        }
        finally { LegacyRelayCleanup.SchtasksRunner = old; }
    }

    [Theory]
    [InlineData(Channel, true)]
    [InlineData("bad", false)]
    [InlineData(null, false)]
    public void Start_by_old_task_removes_only_its_own_task_and_exits(string? channel, bool deletes)
    {
        var old = LegacyRelayCleanup.SchtasksRunner;
        var calls = new List<string[]>();
        try
        {
            LegacyRelayCleanup.SchtasksRunner = (_, args) => { calls.Add(args); return (0, ""); };
            Assert.Equal(0, LegacyRelayCleanup.RunAsLegacyHelper(channel));
            if (deletes) Assert.Equal("CHIMERION DoH " + channel, Assert.Single(calls)[2]);
            else Assert.Empty(calls);
        }
        finally { LegacyRelayCleanup.SchtasksRunner = old; }
    }
}
