using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Наблюдение за сайтами (SiteWatch) без сети: отбор сайтов, круги, падение и восстановление, разбор причины.</summary>
[Collection("scenarios")]
public class SiteWatchTests
{
    [Theory]
    [InlineData("static.cdn.example.co.uk", "example.co.uk")]
    [InlineData("www.figma.com.", "figma.com")]
    [InlineData("api.openai.com", "openai.com")]
    [InlineData("user.github.io", "user.github.io")]
    [InlineData("habr.com", "habr.com")]
    [InlineData("router.lan", null)]
    [InlineData("1.0.168.192.in-addr.arpa", null)]
    [InlineData("www.msftconnecttest.com", null)]
    [InlineData("192.168.1.1", null)]
    [InlineData("localhost", null)]
    [InlineData("chimerion-crowd.chimerion.workers.dev", null)]
    public void Site_is_taken_from_cache_name(string name, string? site) => Assert.Equal(site, SiteWatch.SiteOf(name));

    [Fact]
    public void Site_is_watched_after_two_rounds_and_alerts_only_after_two_fails_of_a_site_that_worked()
    {
        var w = new SiteWatch();
        var t = new DateTime(2026, 10, 26, 12, 0, 0);
        w.Observe(new[] { "www.figma.com", "static.figma.com", "tracker.dead" }, t);
        Assert.Empty(w.Due(t, TimeSpan.FromMinutes(30)));           // встретился один круг: еще не под наблюдением
        w.Observe(new[] { "figma.com", "tracker.dead" }, t.AddMinutes(5));
        var due = w.Due(t.AddMinutes(5), TimeSpan.FromMinutes(30)).Select(s => s.Host).ToList();
        Assert.Equal(new[] { "figma.com", "tracker.dead" }, due.OrderBy(x => x));

        var figma = w.Sites["figma.com"];
        var dead = w.Sites["tracker.dead"];
        Assert.Null(w.Apply(dead, SiteWatch.Verdict.NoDns, null, t));
        Assert.Null(w.Apply(dead, SiteWatch.Verdict.NoDns, null, t)); // никогда не открывался: не шумим
        Assert.Null(w.Apply(figma, SiteWatch.Verdict.Ok, null, t));
        Assert.Null(w.Apply(figma, SiteWatch.Verdict.Blocked, null, t)); // одна неудача
        var down = w.Apply(figma, SiteWatch.Verdict.Blocked, "снаружи открывается", t);
        Assert.True(down!.Down);
        Assert.Null(w.Apply(figma, SiteWatch.Verdict.Blocked, null, t)); // уже сообщили
        Assert.False(w.Apply(figma, SiteWatch.Verdict.Ok, null, t)!.Down); // снова открывается

        // Упавший проверяется в каждом круге, свежепроверенный рабочий ждет
        w.Apply(figma, SiteWatch.Verdict.Blocked, null, t);
        Assert.Contains(figma, w.Due(t.AddMinutes(1), TimeSpan.FromMinutes(30)));
        w.Ignored.Add("figma.com");
        Assert.DoesNotContain(figma, w.Due(t.AddMinutes(1), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Old_and_extra_sites_are_forgotten()
    {
        var w = new SiteWatch();
        var t = new DateTime(2026, 10, 1);
        w.Observe(new[] { "old.com" }, t);
        w.Observe(Enumerable.Range(0, SiteWatch.MaxSites + 20).Select(i => $"site{i}.com"), t.AddDays(SiteWatch.ForgetDays + 1));
        Assert.False(w.Sites.ContainsKey("old.com"));
        Assert.Equal(SiteWatch.MaxSites, w.Sites.Count);
    }

    [Fact]
    public async Task Cause_is_found_from_local_and_outside_checks()
    {
        var (oldLocal, oldOut) = (SiteWatch.Local, SiteWatch.Outside);
        try
        {
            async Task<SiteWatch.Verdict> Case((bool, int?, string?) local, bool? outside, bool smart = false)
            {
                SiteWatch.Local = (_, _) => Task.FromResult(local);
                SiteWatch.Outside = (_, _) => Task.FromResult(outside);
                return (await SiteWatch.CheckAsync("example.com", smart, CancellationToken.None)).Item1;
            }
            Assert.Equal(SiteWatch.Verdict.Ok, await Case((true, 200, null), null));
            Assert.Equal(SiteWatch.Verdict.Ok, await Case((true, 403, "<html>Forbidden</html>"), null)); // просто 403: сайт жив
            Assert.Equal(SiteWatch.Verdict.Blocked, await Case((true, null, null), true));
            Assert.Equal(SiteWatch.Verdict.DownForAll, await Case((true, null, null), false));
            Assert.Equal(SiteWatch.Verdict.DownForAll, await Case((true, 502, null), false));
            Assert.Equal(SiteWatch.Verdict.Ok, await Case((true, 502, null), true));
            Assert.Equal(SiteWatch.Verdict.NoDns, await Case((false, null, null), true));
            Assert.Equal(SiteWatch.Verdict.DownForAll, await Case((false, null, null), false));
            Assert.Equal(SiteWatch.Verdict.Refused, await Case((true, 403, "{\"error\":{\"code\":\"unsupported_country_region_territory\"}}"), true));
            Assert.Equal(SiteWatch.Verdict.NotProxied, await Case((true, 403, "Request not allowed"), true, smart: true));
        }
        finally { (SiteWatch.Local, SiteWatch.Outside) = (oldLocal, oldOut); }
    }

    [Fact]
    public void Dns_cache_can_be_read_without_admin_rights()
    {
        var names = SiteWatch.CachedNames(); // только чтение кэша Windows: не падает, возвращает имена или пусто
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void State_survives_save_and_load()
    {
        var w = new SiteWatch();
        w.Observe(new[] { "habr.com" }, DateTime.Now);
        w.Ignored.Add("tracker.com");
        w.Save();
        var back = SiteWatch.Load();
        Assert.True(back.Sites.ContainsKey("HABR.COM"));
        Assert.Contains("TRACKER.com", back.Ignored);
        File.Delete(Storage.PathOf(SiteWatch.FileName));
    }
}

/// <summary>Обнаружение серверов и статус сервисов через сервер общей карты.</summary>
[Collection("scenarios")]
public class DiscoveryAndStatusTests
{
    private sealed class Reply(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private static async Task<T> With<T>(string json, Func<Task<T>> act)
    {
        var (oldHttp, oldUrl) = (Crowd.Http, Crowd.BaseUrl);
        Crowd.Http = new HttpClient(new Reply(json));
        Crowd.BaseUrl = () => "https://crowd.test";
        try { return await act(); }
        finally { (Crowd.Http, Crowd.BaseUrl) = (oldHttp, oldUrl); }
    }

    [Fact]
    public async Task Discovered_servers_become_rows_with_doh_or_ip()
    {
        var found = await With("""
            {"v":1,"servers":[
              {"server":"dns.comss.one","name":"Comss","url":"https://dns.comss.one/dns-query","rate":90,"checks":40,"nets":3},
              {"server":"xbox-dns.ru","name":"Xbox","url":null,"rate":80,"checks":30,"nets":2},
              {"server":"83.217.211.149","name":"Zapret\u0007","rate":95,"checks":50,"nets":4},
              {"server":"evil.com","name":"Evil","url":"https://other.com/dns-query","rate":99,"checks":99,"nets":9},
              {"server":"not a host","name":"x","rate":90,"checks":40,"nets":3}]}
            """, () => Crowd.DiscoverAsync());
        Assert.Equal(5, found.Count);
        var rows = found.Select(Crowd.ToServer).ToList();
        Assert.Equal("https://dns.comss.one/dns-query", rows[0]!.DohUrl);
        Assert.Equal("https://xbox-dns.ru/dns-query", rows[1]!.DohUrl);
        Assert.Equal("83.217.211.149", rows[2]!.Primary);
        Assert.Equal("Zapret", rows[2]!.Name); // управляющие символы убраны
        Assert.Equal("https://evil.com/dns-query", rows[3]!.DohUrl); // чужой хост в адресе не принимается
        Assert.Null(rows[4]);
        Assert.All(rows.Where(r => r != null), r => Assert.Equal("Общая карта", r!.Source));
    }

    [Fact]
    public async Task Service_status_is_parsed()
    {
        var st = await With("""{"v":1,"services":{"ChatGPT":{"indicator":"major","text":"Partial outage"},"Claude":{"indicator":"none","text":"All Systems Operational"}}}""",
            () => Crowd.ServiceStatusAsync());
        Assert.Equal("major", st["chatgpt"].Indicator);
        Assert.Equal("All Systems Operational", st["Claude"].Text);
    }

    [Fact]
    public void Report_item_carries_doh_url()
    {
        var q = new Crowd.Queue();
        Crowd.Enqueue(q, new DnsServer { Name = "Comss", DohUrl = "https://dns.comss.one/dns-query{?dns}", Summary = "ChatGPT ✓" });
        Assert.Equal("https://dns.comss.one/dns-query", Assert.Single(q.Items).Url);
    }
}

/// <summary>Телефон: профиль iPhone, адрес для Android, QR-код.</summary>
public class PhoneSetupTests
{
    private static readonly DnsServer Comss = new() { Name = "Comss <DNS> & Co", DohUrl = "https://dns.comss.one/dns-query", Status = ServerStatus.Ok };

    [Fact]
    public void Profile_is_valid_plist_with_doh()
    {
        var xml = PhoneSetup.MobileConfig(Comss);
        var doc = XDocument.Parse(xml); // имя с < и & экранировано: XML не ломается
        Assert.Contains("https://dns.comss.one/dns-query", xml);
        Assert.Contains("com.apple.dnsSettings.managed", xml);
        Assert.Equal("plist", doc.Root!.Name.LocalName);
        Assert.Equal("dns.comss.one", PhoneSetup.DotHost(Comss));
        Assert.StartsWith("https://", PhoneSetup.ProfileUrl(Comss));
        Assert.Contains("doh=https%3A%2F%2Fdns.comss.one", PhoneSetup.ProfileUrl(Comss));
        Assert.Throws<InvalidOperationException>(() => PhoneSetup.MobileConfig(new DnsServer { Primary = "1.2.3.4" }));
    }

    [Fact]
    public void Qr_is_a_png_and_pick_needs_doh()
    {
        var png = PhoneSetup.Qr("dns.comss.one");
        Assert.True(png.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        var plain = new DnsServer { Name = "IP", Primary = "1.2.3.4", Status = ServerStatus.Ok };
        Assert.Same(Comss, PhoneSetup.Pick(new[] { plain, Comss }, null));
        Assert.Null(PhoneSetup.Pick(new[] { plain }, null));
    }
}

public class AiSpeedTests
{
    [Fact]
    public void Ai_speed_is_smoothed_and_shown()
    {
        var s = new DnsServer();
        Assert.Equal("—", s.AiText);
        s.AddAiSample(null);
        Assert.Null(s.AiMs);
        s.AddAiSample(400);
        s.AddAiSample(1200);
        Assert.Equal(800, s.AiMs);
        Assert.Equal("800", s.AiText);
    }

    [Fact]
    public async Task Check_measures_api_time_of_open_services()
    {
        var s = new DnsServer { Name = "Проверяемый", Primary = "203.0.113.50" };
        await new FakeChecker().CheckAsync(s, CancellationToken.None);
        Assert.NotNull(s.AiMs);
        var broken = new DnsServer { Name = "Сломан", Primary = "203.0.113.51" };
        await new FakeChecker { Https = _ => ServerChecker.Probe.Blocked }.CheckAsync(broken, CancellationToken.None);
        Assert.Null(broken.AiMs); // закрытые сервисы скорость не дают
    }
}
