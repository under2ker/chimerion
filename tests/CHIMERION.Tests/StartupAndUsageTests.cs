using System.Net;
using System.Net.Http;
using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class StartupCheckTests
{
    private static List<StartupIssue> Inspect(bool connected = false, int v6 = 0, bool backup = false, bool rules = false, bool running = false, bool enabled = false) =>
        StartupCheck.Inspect(new StartupState(connected, v6, backup, rules, running, enabled));

    [Fact]
    public void Clean_state_has_no_issues() => Assert.Empty(Inspect());

    [Fact]
    public void Connected_state_leaves_network_files_to_the_connection_check() =>
        Assert.Empty(Inspect(connected: true, v6: 2, backup: true, rules: true));

    [Fact]
    public void Leftovers_without_connection_need_a_restore()
    {
        var issues = Inspect(v6: 1, backup: true, rules: true);
        Assert.Equal(new[] { StartupIssue.Ipv6LeftOff, StartupIssue.RulesLeft }, issues);
        Assert.All(issues, i => Assert.True(StartupCheck.NeedsRestore(i)));
    }

    [Fact]
    public void Backup_alone_is_stale_and_needs_no_restore()
    {
        var issues = Inspect(backup: true);
        Assert.Equal(new[] { StartupIssue.StaleBackup }, issues);
        Assert.False(StartupCheck.NeedsRestore(issues[0]));
    }

    [Fact]
    public void Zapret_running_while_off_is_reported()
    {
        Assert.Contains(StartupIssue.ForeignZapret, Inspect(running: true));
        Assert.Empty(Inspect(running: true, enabled: true));
    }
}

[Collection("scenarios")]
public class UsageCounterTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = new();
        public HttpStatusCode Code { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Code);
        }
    }

    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0);
    private const string Guid1 = "11111111-2222-3333-4444-555555555555";

    private static async Task With(Func<Handler, Task> test)
    {
        var (http, url, guid) = (Crowd.Http, Crowd.BaseUrl, UsageCounter.Source);
        var h = new Handler();
        Crowd.Http = new HttpClient(h);
        Crowd.BaseUrl = () => "https://crowd.test";
        UsageCounter.Source = () => Guid1;
        var mark = Storage.PathOf(UsageCounter.LockFile);
        File.Delete(mark);
        try { await test(h); }
        finally { Crowd.Http = http; Crowd.BaseUrl = url; UsageCounter.Source = guid; File.Delete(mark); }
    }

    [Fact]
    public void Id_is_stable_for_a_device_differs_between_devices_and_hides_the_source()
    {
        var id = UsageCounter.IdOf(Guid1)!;
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, UsageCounter.IdOf(" " + Guid1.ToUpperInvariant() + " "));
        Assert.NotEqual(id, UsageCounter.IdOf("11111111-2222-3333-4444-555555555556"));
        Assert.DoesNotContain("11111111", id);
        Assert.Null(UsageCounter.IdOf(""));
    }

    [Fact]
    public async Task Ping_goes_at_start_and_every_six_hours() => await With(async h =>
    {
        var s = new AppSettings();
        Assert.True(await UsageCounter.PingAsync(s, Now));
        Assert.False(await UsageCounter.PingAsync(s, Now.AddHours(3)));
        Assert.True(await UsageCounter.PingAsync(s, Now.AddHours(3), atStart: true));
        Assert.False(await UsageCounter.PingAsync(s, Now.AddHours(8)));
        Assert.True(await UsageCounter.PingAsync(s, Now.AddHours(9)));
        Assert.Equal(3, h.Calls.Count);
        Assert.EndsWith("/v1/ping", h.Calls[0].Url);
        using var doc = System.Text.Json.JsonDocument.Parse(h.Calls[0].Body);
        Assert.Equal(new[] { "v", "id", "ver", "src", "tok", "prev", "moved", "sec", "runs" }, doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(System.Text.Json.JsonValueKind.String, doc.RootElement.GetProperty("ver").ValueKind);
        Assert.Equal(UsageCounter.IdOf(Guid1), doc.RootElement.GetProperty("id").GetString());
        Assert.DoesNotContain(Guid1, h.Calls[0].Body);
    });

    private static byte[] Smbios(byte[] uuid)
    {
        var bios = new byte[] { 0, 4, 0, 0, 0, 0 }; // запись типа 0 без строк
        var system = new byte[25];
        system[0] = 1; system[1] = 25;
        uuid.CopyTo(system, 8);
        return new byte[8].Concat(bios).Concat(system).Concat(new byte[] { 0, 0 }).ToArray();
    }

    [Fact]
    public void Smbios_uuid_is_read_and_factory_values_are_rejected()
    {
        var uuid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(Convert.ToHexString(uuid).ToLowerInvariant(), DeviceIdentity.ParseSmbiosUuid(Smbios(uuid)));
        Assert.Null(DeviceIdentity.ParseSmbiosUuid(Smbios(new byte[16])));
        Assert.Null(DeviceIdentity.ParseSmbiosUuid(Smbios(Enumerable.Repeat((byte)0xFF, 16).ToArray())));
        Assert.Null(DeviceIdentity.ParseSmbiosUuid(Smbios(Convert.FromHexString("03000200040005000006000700080009"))));
        Assert.Null(DeviceIdentity.ParseSmbiosUuid(new byte[8]));
    }

    [Fact]
    public void Real_device_id_is_stable_between_reads() =>
        Assert.Equal(DeviceIdentity.HashOf(DeviceIdentity.ReadSource()), DeviceIdentity.HashOf(DeviceIdentity.ReadSource()));

    private static System.Text.Json.JsonElement Last(Handler h) => System.Text.Json.JsonDocument.Parse(h.Calls[^1].Body).RootElement;

    [Fact]
    public async Task Replaced_uuid_reports_previous_id_and_keeps_token() => await With(async h =>
    {
        await UsageCounter.PingAsync(new AppSettings(), Now);
        var token = Last(h).GetProperty("tok").GetString();
        UsageCounter.Source = () => "22222222-2222-2222-2222-222222222222";
        await UsageCounter.PingAsync(new AppSettings(), Now);
        Assert.Equal(UsageCounter.IdOf(Guid1), Last(h).GetProperty("prev").GetString());
        Assert.Equal(token, Last(h).GetProperty("tok").GetString());
        await UsageCounter.PingAsync(new AppSettings(), Now);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Last(h).GetProperty("prev").ValueKind);
    });

    [Fact]
    public async Task Lock_from_another_machine_is_reported_as_moved() => await With(async h =>
    {
        File.WriteAllBytes(Storage.PathOf(UsageCounter.LockFile), new byte[] { 1, 2, 3 });
        await UsageCounter.PingAsync(new AppSettings(), Now);
        Assert.True(Last(h).GetProperty("moved").GetBoolean());
        await UsageCounter.PingAsync(new AppSettings(), Now);
        Assert.False(Last(h).GetProperty("moved").GetBoolean());
    });

    [Fact]
    public async Task Hidden_uuid_is_visible_by_source_kind() => await With(async h =>
    {
        var kind = UsageCounter.Kind;
        UsageCounter.Kind = () => "windows";
        try { await UsageCounter.PingAsync(new AppSettings(), Now); }
        finally { UsageCounter.Kind = kind; }
        Assert.Equal("windows", Last(h).GetProperty("src").GetString());
        UsageCounter.Source = () => null;
        await UsageCounter.PingAsync(new AppSettings(), Now);
        Assert.Equal("random", Last(h).GetProperty("src").GetString());
    });

    [Fact]
    public async Task Session_time_is_sent_once_and_sleep_is_not_counted() => await With(async h =>
    {
        var s = new AppSettings { UsageRuns = 2 };
        UsageCounter.AddSession(s, TimeSpan.FromMinutes(10));
        UsageCounter.AddSession(s, TimeSpan.FromHours(8)); // компьютер спал
        Assert.Equal(1500, s.UsageSeconds);
        await UsageCounter.PingAsync(s, Now);
        Assert.Equal(1500, Last(h).GetProperty("sec").GetInt64());
        Assert.Equal(2, Last(h).GetProperty("runs").GetInt32());
        Assert.Equal(0, s.UsageSeconds);
        Assert.Equal(0, s.UsageRuns);
    });

    [Fact]
    public async Task Failed_ping_is_retried() => await With(async h =>
    {
        h.Code = HttpStatusCode.NotFound; // сервер еще не обновлен
        var s = new AppSettings();
        Assert.False(await UsageCounter.PingAsync(s, Now));
        Assert.Null(s.UsagePingAt);
        h.Code = HttpStatusCode.OK;
        Assert.True(await UsageCounter.PingAsync(s, Now));
    });

    [Fact]
    public async Task Without_windows_id_a_random_one_is_kept_in_settings() => await With(async _ =>
    {
        UsageCounter.Source = () => null;
        var s = new AppSettings();
        var id = UsageCounter.DeviceId(s);
        Assert.Equal(id, UsageCounter.DeviceId(s));
        Assert.Equal(id, s.UsageId);
        await Task.CompletedTask;
    });
}
