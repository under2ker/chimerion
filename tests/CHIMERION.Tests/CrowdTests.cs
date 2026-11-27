using System.Net;
using System.Net.Http;
using System.Text.Json;
using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Общая карта серверов (Crowd) без сети: очередь, отправка, рейтинг по провайдеру.</summary>
[Collection("scenarios")]
public class CrowdTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<(string Url, string Body)> Calls = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Reply = "{}";
        /// <summary>Что происходит, пока запрос «в пути»: например, проверка добавляет в очередь новые итоги.</summary>
        public Action? During;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls.Add((req.RequestUri!.ToString(), req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct)));
            During?.Invoke();
            return new HttpResponseMessage(Status) { Content = new StringContent(Reply) };
        }
    }

    private static DnsServer Srv(string name, string ip, string summary, string? doh = null) =>
        new() { Name = name, Primary = ip, DohUrl = doh, Summary = summary, Status = ServerStatus.Ok };

    private static (Handler H, IDisposable Restore) Fake()
    {
        var (oldHttp, oldUrl) = (Crowd.Http, Crowd.BaseUrl);
        var h = new Handler();
        Crowd.Http = new HttpClient(h);
        Crowd.BaseUrl = () => "https://crowd.test";
        return (h, new Restorer(() => { Crowd.Http = oldHttp; Crowd.BaseUrl = oldUrl; }));
    }

    private sealed class Restorer(Action a) : IDisposable { public void Dispose() => a(); }

    [Fact]
    public void Key_is_doh_host_or_ip_and_queue_keeps_last_result_without_yellow_chips()
    {
        Assert.Equal("dns.comss.one", Crowd.KeyOf(Srv("Comss", "83.220.169.155", "", "https://Dns.Comss.One/dns-query")));
        Assert.Equal("83.217.211.149", Crowd.KeyOf(Srv("Zapret", "83.217.211.149", "")));
        Assert.Null(Crowd.KeyOf(Srv("Пустой", "", "")));

        var q = new Crowd.Queue();
        Crowd.Enqueue(q, Srv("Zapret", "83.217.211.149", "ChatGPT ✓ · Claude ✗ закрыт · Gemini регион"));
        Assert.Equal(2, q.Items.Count); // Gemini желтый: капчу и регион не различить, не отправляем
        Crowd.Enqueue(q, Srv("Zapret", "83.217.211.149", "ChatGPT ✗ нет ответа · Claude ✓"));
        Assert.Equal(2, q.Items.Count);
        Assert.False(q.Items.Single(i => i.Service == "ChatGPT").Ok);
        Assert.True(q.Items.Single(i => i.Service == "Claude").Ok);

        // Свой сервер, добавленный вручную, в общую карту не уходит
        var mine = Srv("Мой VPS", "203.0.113.77", "ChatGPT ✓");
        mine.Manual = true;
        Crowd.Enqueue(q, mine);
        Assert.DoesNotContain(q.Items, i => i.Server == "203.0.113.77");
    }

    [Fact]
    public async Task Results_added_while_sending_are_kept()
    {
        // Аудит CR-12: после ответа очередь очищалась целиком, и итоги, добавленные во время отправки, пропадали
        var (h, restore) = Fake();
        using var _ = restore;
        var q = new Crowd.Queue();
        Crowd.Enqueue(q, Srv("Старый", "203.0.113.30", "ChatGPT ✓"));
        h.During = () =>
        {
            Crowd.Enqueue(q, Srv("Новый", "203.0.113.31", "Claude ✓"));       // новый сервер
            Crowd.Enqueue(q, Srv("Старый", "203.0.113.30", "ChatGPT ✗"));      // свежий итог того же сервера
        };
        Assert.True(await Crowd.SendAsync(q, now: new DateTime(2026, 11, 27, 12, 0, 0)));
        Assert.Equal(2, q.Items.Count);
        Assert.Contains(q.Items, i => i.Server == "203.0.113.31");
        Assert.Contains(q.Items, i => i.Server == "203.0.113.30" && !i.Ok);
        Assert.Contains("203.0.113.30", h.Calls.Single().Body); // ушел прежний итог
    }

    [Fact]
    public void Partial_check_sends_only_checked_services()
    {
        // Аудит CR-06: «Только ChatGPT» — плашки Claude и Gemini прошлые или выдуманные, в карту их не отправляем
        var q = new Crowd.Queue();
        Crowd.Enqueue(q, Srv("Zapret", "83.217.211.149", "ChatGPT ✓ · Claude ✗ · Gemini ✗"), new[] { "ChatGPT" });
        var item = Assert.Single(q.Items);
        Assert.Equal("ChatGPT", item.Service);
        Assert.True(item.Ok);
        Crowd.Enqueue(q, Srv("Zapret", "83.217.211.149", "ChatGPT ✓ · Claude ✗ · Gemini ✗"));
        Assert.Equal(3, q.Items.Count); // полная проверка — как раньше
    }

    [Fact]
    public void Personal_doh_link_is_not_shared_only_host()
    {
        Assert.Equal("https://dns.comss.one/dns-query", Crowd.ShareableUrl("https://dns.comss.one/dns-query{?dns}"));
        Assert.Equal("https://dns.example.net/", Crowd.ShareableUrl("https://dns.example.net/"));
        Assert.Null(Crowd.ShareableUrl("https://dns.example.net/dns-query/a1b2c3d4e5f6a7b8")); // ключ после пути
        Assert.Null(Crowd.ShareableUrl("https://dns.example.net/a1b2c3d4e5f6a7b8"));           // ключ вместо пути
        Assert.Null(Crowd.ShareableUrl("https://dns.example.net/dns-query?token=secret"));
        Assert.Null(Crowd.ShareableUrl("https://user:pass@dns.example.net/dns-query"));
        Assert.Null(Crowd.ShareableUrl("http://dns.example.net/dns-query"));

        var q = new Crowd.Queue();
        Crowd.Enqueue(q, Srv("Платный", "", "ChatGPT ✓", "https://dns.example.net/dns-query/a1b2c3d4e5f6a7b8"));
        var item = Assert.Single(q.Items);
        Assert.Equal("dns.example.net", item.Server);
        Assert.Null(item.Url);
    }

    [Fact]
    public async Task Old_queue_entry_with_personal_link_is_sent_without_it()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var q = new Crowd.Queue();
        q.Items.Add(new Crowd.Item("dns.example.net", "ChatGPT", true, "Платный", "https://dns.example.net/a1b2c3d4e5f6a7b8"));
        Assert.True(await Crowd.SendAsync(q, now: new DateTime(2026, 11, 26, 12, 0, 0)));
        Assert.DoesNotContain("a1b2c3d4e5f6a7b8", Assert.Single(h.Calls).Body);
    }

    [Fact]
    public async Task Send_posts_once_an_hour_and_clears_queue_also_on_429()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var q = new Crowd.Queue();
        Crowd.Enqueue(q, Srv("Zapret", "83.217.211.149", "ChatGPT ✓"));
        var t0 = new DateTime(2026, 10, 26, 12, 0, 0);

        Assert.True(await Crowd.SendAsync(q, now: t0));
        var (url, body) = Assert.Single(h.Calls);
        Assert.Equal("https://crowd.test/v1/report", url);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("83.217.211.149", item.GetProperty("server").GetString());
        Assert.True(item.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("192.168", body);
        Assert.Empty(q.Items);

        Crowd.Enqueue(q, Srv("Zapret", "83.217.211.149", "ChatGPT ✓"));
        Assert.False(await Crowd.SendAsync(q, now: t0.AddMinutes(30))); // рано
        Assert.Single(h.Calls);

        h.Status = HttpStatusCode.TooManyRequests;
        Assert.True(await Crowd.SendAsync(q, now: t0.AddMinutes(61)));
        Assert.Empty(q.Items);

        h.Status = HttpStatusCode.InternalServerError;
        Crowd.Enqueue(q, Srv("Zapret", "83.217.211.149", "ChatGPT ✓"));
        Assert.False(await Crowd.SendAsync(q, now: t0.AddMinutes(125)));
        Assert.Single(q.Items); // сервер сломан: итоги остаются до следующей попытки
    }

    [Fact]
    public async Task Stats_are_fetched_and_best_for_provider_needs_enough_checks()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        h.Reply = """
            {"v":1,"asn":12389,"org":"Rostelecom","provider":[
              {"server":"83.217.211.149","service":"ChatGPT","ok":40,"fail":2,"ok6":10,"fail6":0,"name":"Zapret"},
              {"server":"83.217.211.149","service":"Claude","ok":38,"fail":4,"ok6":10,"fail6":0,"name":"Zapret"},
              {"server":"dns.comss.one","service":"ChatGPT","ok":5,"fail":0,"ok6":5,"fail6":0,"name":"Comss"},
              {"server":"111.88.96.50","service":"ChatGPT","ok":10,"fail":30,"ok6":0,"fail6":10,"name":"Xbox"}],
             "global":[]}
            """;
        var st = await Crowd.FetchAsync();
        Assert.Equal("Rostelecom (AS12389)", Crowd.NetName(st));
        Assert.NotNull(Crowd.LoadStats());

        var zapret = Srv("Zapret", "83.217.211.149", "ChatGPT ✓");
        var comss = Srv("Comss", "83.220.169.155", "ChatGPT ✓", "https://dns.comss.one/dns-query");
        var xbox = Srv("Xbox", "111.88.96.50", "ChatGPT ✓");
        var best = Crowd.BestForProvider(st, new[] { comss, xbox, zapret })!.Value;
        Assert.Same(zapret, best.Server); // у Comss 5 проверок: мало
        Assert.Equal((92, 84), (best.Rate, best.Total));

        zapret.Status = ServerStatus.Fail; // у самого пользователя не работает: не предлагаем
        Assert.Null(Crowd.BestForProvider(st, new[] { comss, xbox, zapret })); // у Xbox 25%: лучший из плохих не совет
        Assert.Null(Crowd.BestForProvider(new Crowd.Stats(), new[] { zapret }));
        File.Delete(Storage.PathOf(Crowd.StatsFile));
    }
}
