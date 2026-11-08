using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Окно «Аккаунты» (AccountRisk) и новые блокировки у провайдера (Incidents, sites в общей карте).</summary>
[Collection("scenarios")]
public class AccountRiskAndIncidentsTests
{
    private static AccountRisk.ServiceView View(string name, string? now, string? home, params string[] recent) =>
        new(name, now, home, recent, null);

    [Fact]
    public void Same_country_everywhere_is_calm_and_unknown_without_egress()
    {
        var calm = AccountRisk.Assess(new[] { View("ChatGPT", "NL", "NL", "NL"), View("Claude", "NL", "NL", "NL") }, guardOn: true);
        Assert.Equal(AccountRisk.Level.Calm, calm.Level);
        Assert.True(calm.Known);
        Assert.Empty(calm.Factors);

        var empty = AccountRisk.Assess(new[] { View("ChatGPT", null, null) }, guardOn: true);
        Assert.False(empty.Known);
        Assert.Contains("Пока не с чем сравнивать", AccountRisk.Summary(empty));
    }

    [Fact]
    public void Other_country_bad_address_and_many_countries_are_risky_two_countries_attention()
    {
        var other = AccountRisk.Assess(new[] { View("ChatGPT", "DE", "NL", "NL") }, guardOn: true);
        Assert.Equal(AccountRisk.Level.Risky, other.Level);
        Assert.Contains(other.Factors, f => f.Text.Contains("ChatGPT видит вас из страны Германия"));

        var two = AccountRisk.Assess(new[] { View("Claude", "NL", "NL", "DE", "NL") }, guardOn: true);
        Assert.Equal(AccountRisk.Level.Attention, two.Level);
        var three = AccountRisk.Assess(new[] { View("Claude", "NL", "NL", "DE", "US", "NL") }, guardOn: true);
        Assert.Equal(AccountRisk.Level.Risky, three.Level);

        var rep = new IpRep("203.0.113.9", 90, "VPN", null, new[] { "DroneBL" }, DateTime.Now);
        var bad = AccountRisk.Assess(new[] { new AccountRisk.ServiceView("Gemini", "NL", "NL", new[] { "NL" }, rep) }, guardOn: true);
        Assert.Equal(AccountRisk.Level.Risky, bad.Level);
        Assert.Contains(bad.Factors, f => f.Text.Contains("DroneBL"));

        // Выключенная защита сама по себе повод насторожиться, но не тревога
        var off = AccountRisk.Assess(new[] { View("ChatGPT", "NL", "NL", "NL") }, guardOn: false);
        Assert.Equal(AccountRisk.Level.Attention, off.Level);
    }

    [Fact]
    public void Common_home_only_when_all_services_agree()
    {
        Assert.Equal("NL", AccountRisk.CommonHome(new[] { View("ChatGPT", "NL", "nl"), View("Claude", null, "NL"), View("Gemini", null, null) }));
        Assert.Null(AccountRisk.CommonHome(new[] { View("ChatGPT", "NL", "NL"), View("Claude", "DE", "DE") }));
        Assert.Null(AccountRisk.CommonHome(new[] { View("ChatGPT", null, null) }));
    }

    private static SiteWatch.Site Site(string host, SiteWatch.Verdict v) => new() { Host = host, State = v };

    [Fact]
    public void Only_listed_services_with_clear_verdict_are_reported_one_per_service()
    {
        var reports = Incidents.Collect(new[]
        {
            Site("www.youtube.com", SiteWatch.Verdict.Blocked),
            Site("i.ytimg.youtube.com", SiteWatch.Verdict.Ok),      // тот же сервис открылся по другому адресу: открывается
            Site("discord.com", SiteWatch.Verdict.Refused),
            Site("x.com", SiteWatch.Verdict.DownForAll),             // лежит у всех: не про провайдера
            Site("my-private-site.example", SiteWatch.Verdict.Blocked), // свой сайт не уходит никогда
            Site("notyoutube.com", SiteWatch.Verdict.Blocked),
        }, bypass: true);
        Assert.Equal(new[] { "discord.com", "youtube.com" }, reports.Select(r => r.Site));
        Assert.False(reports.Single(r => r.Site == "discord.com").Ok);
        Assert.True(reports.Single(r => r.Site == "youtube.com").Ok);
        Assert.All(reports, r => Assert.True(r.Bypass));
    }

    [Fact]
    public void Notify_only_used_services_matching_bypass_and_once_a_day()
    {
        var day = new DateTime(2026, 11, 8);
        var list = new[]
        {
            new Incidents.Incident("discord.com", false, 80, 9, 100),
            new Incidents.Incident("youtube.com", true, 70, 6, 100),   // обход у человека выключен: не про него
            new Incidents.Incident("instagram.com", false, 90, 9, 100), // не пользуется
            new Incidents.Incident("t.me", false, 90, 9, 100),
            new Incidents.Incident("telegram.org", false, 90, 9, 100), // тот же Telegram: одно уведомление
        };
        var used = new[] { "discord.com", "www.youtube.com", "t.me", "web.telegram.org" };
        var notified = new List<string>();
        var first = Incidents.ToNotify(list, 12389, used, bypassOn: false, notified, day);
        Assert.Equal(new[] { "discord.com", "t.me" }, first.Select(i => i.Site));

        notified.AddRange(first.Select(i => Incidents.Key(12389, i, day)));
        // О Telegram уже сказали по t.me: telegram.org в тот же день не повторяет
        Assert.Empty(Incidents.ToNotify(list, 12389, used, bypassOn: false, notified, day));
        Assert.Equal(2, Incidents.ToNotify(list, 12389, used, bypassOn: false, notified, day.AddDays(1)).Count);
        // Обход включен: теперь важна только блокировка с обходом
        Assert.Equal(new[] { "youtube.com" }, Incidents.ToNotify(list, 12389, used, bypassOn: true, notified, day).Select(i => i.Site));
    }

    [Fact]
    public void Describe_names_service_network_and_what_to_do()
    {
        var since = DateTimeOffset.Now.AddHours(-2).ToUnixTimeSeconds() / 3600;
        var off = Incidents.Describe(new Incidents.Incident("discord.com", false, 80, 9, since), "Rostelecom (AS12389)");
        Assert.Contains("У пользователей Rostelecom (AS12389)", off);
        Assert.Contains("не открывается Discord", off);
        Assert.Contains("«Обход и Telegram»", off);
        var on = Incidents.Describe(new Incidents.Incident("youtube.com", true, 80, 9, since), "AS1");
        Assert.Contains("обход перестал открывать YouTube", on);
        Assert.DoesNotContain("ё", off + on);
        Assert.DoesNotContain("—", off + on);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        public string Reply = "{}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Bodies.Add(req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply) };
        }
    }

    [Fact]
    public async Task Sites_go_in_report_without_server_items_and_incidents_come_back_in_stats()
    {
        var (oldHttp, oldUrl) = (Crowd.Http, Crowd.BaseUrl);
        var h = new Handler();
        Crowd.Http = new HttpClient(h);
        Crowd.BaseUrl = () => "https://crowd.test";
        try
        {
            var q = new Crowd.Queue();
            Crowd.EnqueueSites(q, new[] { new Incidents.Report("discord.com", true, false) });
            Crowd.EnqueueSites(q, new[] { new Incidents.Report("discord.com", false, false) });
            Assert.Single(q.Sites);
            Assert.True(await Crowd.SendAsync(q));
            using var doc = JsonDocument.Parse(h.Bodies.Single());
            var site = doc.RootElement.GetProperty("sites").EnumerateArray().Single();
            Assert.Equal("discord.com", site.GetProperty("site").GetString());
            Assert.False(site.GetProperty("ok").GetBoolean());
            Assert.Empty(q.Sites);

            h.Reply = """{"v":1,"asn":12389,"org":"Rostelecom","provider":[],"global":[],"incidents":[{"site":"discord.com","bypass":false,"fail":83,"checks":6,"since":493000}]}""";
            var st = await Crowd.FetchAsync();
            var inc = Assert.Single(st.Incidents);
            Assert.Equal(("discord.com", false, 83, 6, 493000L), (inc.Site, inc.Bypass, inc.Fail, inc.Checks, inc.Since));

            // Старый сервер без поля incidents: пустой список, а не ошибка
            h.Reply = """{"v":1,"asn":1,"provider":[],"global":[]}""";
            Assert.Empty((await Crowd.FetchAsync()).Incidents);
        }
        finally { Crowd.Http = oldHttp; Crowd.BaseUrl = oldUrl; }
    }

    [Fact]
    public void Worker_accepts_the_same_service_list_as_the_program()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CHIMERION.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "backend", "crowd", "worker.js"));
        var m = Regex.Match(js, @"export const SITES = new Set\(\[(.*?)\]\)", RegexOptions.Singleline);
        Assert.True(m.Success);
        var worker = Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).OrderBy(x => x).ToArray();
        Assert.Equal(Incidents.Sites.OrderBy(x => x).ToArray(), worker);
    }
}
