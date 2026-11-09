using System.Net;
using System.Net.Http;
using System.Text.Json;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Обращения разработчику (Support) без сети: проверка контакта, отправка, повтор, опрос статусов и уведомления.</summary>
[Collection("scenarios")]
public class SupportTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<(string Url, string Body)> Calls = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Reply = "{}";
        public bool Offline;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls.Add((req.RequestUri!.ToString(), req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct)));
            if (Offline) throw new HttpRequestException("offline");
            return new HttpResponseMessage(Status) { Content = new StringContent(Reply) };
        }
    }

    private sealed class Restorer(Action a) : IDisposable { public void Dispose() => a(); }

    private static (Handler H, IDisposable Restore) Fake()
    {
        var (oldHttp, oldUrl) = (Crowd.Http, Crowd.BaseUrl);
        var h = new Handler();
        Crowd.Http = new HttpClient(h);
        Crowd.BaseUrl = () => "https://crowd.test";
        return (h, new Restorer(() => { Crowd.Http = oldHttp; Crowd.BaseUrl = oldUrl; }));
    }

    [Theory]
    [InlineData("tg", "under2ker", "tg", "@under2ker")]
    [InlineData("tg", "@under2ker", "tg", "@under2ker")]
    [InlineData("tg", "https://t.me/under2ker", "tg", "@under2ker")]
    [InlineData("tg", "t.me/under_2ker", "tg", "@under_2ker")]
    [InlineData("email", " name@example.com ", "email", "name@example.com")]
    [InlineData("tgid", "123456789", "tgid", "123456789")]
    public void Contact_is_normalized_for_the_server(string kind, string raw, string wantKind, string wantValue)
    {
        Assert.True(Support.TryContact(kind, raw, out var c, out var err));
        Assert.Null(err);
        Assert.Equal(new Support.Contact(wantKind, wantValue), c);
    }

    [Theory]
    [InlineData("tg", "abc")]
    [InlineData("tg", "имя_по_русски")]
    [InlineData("email", "not-an-email")]
    [InlineData("email", "a@b")]
    [InlineData("tgid", "12")]
    [InlineData("tgid", "12ab45")]
    public void Wrong_contact_is_refused_with_a_reason(string kind, string raw)
    {
        Assert.False(Support.TryContact(kind, raw, out var c, out var err));
        Assert.Null(c);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void Empty_contact_means_no_contact_and_no_error()
    {
        Assert.True(Support.TryContact("email", "   ", out var c, out var err));
        Assert.Null(c);
        Assert.Null(err);
    }

    [Fact]
    public async Task Send_posts_text_report_and_contact_then_remembers_the_ticket_and_contact()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        h.Reply = """{"id":12,"code":"099d2823"}""";
        var store = new Support.Store();
        var token = Support.NewToken();

        var t = await Support.SendAsync(store, token, "  DNS не подключается  ", new Support.Contact("tg", "@under2ker"), "отчет");

        Assert.Equal("https://crowd.test/v1/ticket", h.Calls.Single().Url);
        using var doc = JsonDocument.Parse(h.Calls.Single().Body);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(token, root.GetProperty("token").GetString());
        Assert.Equal("DNS не подключается", root.GetProperty("text").GetString());
        Assert.Equal("отчет", root.GetProperty("report").GetString());
        Assert.Equal("@under2ker", root.GetProperty("contact").GetProperty("value").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", root.GetProperty("app").GetString()!);

        Assert.Equal(12, t.Id);
        Assert.Equal(Support.New, t.Status);
        Assert.Same(t, store.Items.Single());
        Assert.Equal("tg", store.ContactKind);
        Assert.Equal("@under2ker", store.ContactValue);
        Assert.Equal("https://t.me/CHIMERIONDNS_BOT?start=r12-099d2823", Support.BotLink(t));
    }

    [Fact]
    public async Task Send_without_contact_or_report_sends_nulls_and_keeps_the_saved_contact()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        h.Reply = """{"id":3,"code":"aabbccdd"}""";
        var store = new Support.Store { ContactKind = "email", ContactValue = "old@example.com" };

        await Support.SendAsync(store, Support.NewToken(), "Не работает", null, null);

        using var doc = JsonDocument.Parse(h.Calls.Single().Body);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("contact").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("report").ValueKind);
        Assert.Equal("old@example.com", store.ContactValue);
    }

    [Fact]
    public async Task Repeated_send_with_the_same_token_does_not_duplicate_the_ticket()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        h.Reply = """{"id":5,"code":"11223344"}""";
        var store = new Support.Store();
        var token = Support.NewToken();

        await Support.SendAsync(store, token, "первый", null, null);
        await Support.SendAsync(store, token, "первый", null, null);

        Assert.Single(store.Items);
    }

    [Fact]
    public async Task Too_short_text_is_refused_before_any_request()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var ex = await Assert.ThrowsAsync<Support.SendException>(() => Support.SendAsync(new Support.Store(), Support.NewToken(), " a ", null, null));
        Assert.Contains("парой слов", ex.Message);
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData(429, "Слишком много")]
    [InlineData(413, "слишком большое")]
    [InlineData(500, "не принял")]
    public async Task Server_errors_become_readable_reasons_and_nothing_is_stored(int status, string fragment)
    {
        var (h, restore) = Fake();
        using var _ = restore;
        h.Status = (HttpStatusCode)status;
        var store = new Support.Store();
        var ex = await Assert.ThrowsAsync<Support.SendException>(() => Support.SendAsync(store, Support.NewToken(), "текст обращения", null, null));
        Assert.Contains(fragment, ex.Message);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task No_network_is_a_readable_reason_and_text_can_be_resent()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        h.Offline = true;
        var store = new Support.Store();
        var ex = await Assert.ThrowsAsync<Support.SendException>(() => Support.SendAsync(store, Support.NewToken(), "текст обращения", null, null));
        Assert.Contains("не отвечает", ex.Message);
        Assert.Empty(store.Items);
    }

    private static Support.Ticket Open(int id, string token = "", DateTime? created = null) =>
        new() { Id = id, Token = token == "" ? Support.NewToken() : token, Created = created ?? DateTime.Now, Preview = "x", Code = "abcdef12" };

    [Fact]
    public async Task Poll_does_nothing_without_open_tickets()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var store = new Support.Store();
        store.Items.Add(new Support.Ticket { Id = 1, Token = Support.NewToken(), Created = DateTime.Now, Status = Support.Closed });
        store.Items.Add(Open(2, created: DateTime.Now - Support.Keep - TimeSpan.FromDays(1)));

        Assert.False(Support.HasOpen(store, DateTime.Now));
        Assert.Empty(await Support.PollAsync(store));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Poll_sends_only_tokens_and_marks_changed_tickets_unread()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var a = Open(1); var b = Open(2);
        var store = new Support.Store { Items = { a, b } };
        h.Reply = """{"tickets":[{"id":1,"status":"fixed","fixVersion":"99.0.0","reply":"Исправили","updated":100,"tg":true},{"id":2,"status":"new","fixVersion":null,"reply":null,"updated":50,"tg":false}]}""";

        var changed = await Support.PollAsync(store);

        using var doc = JsonDocument.Parse(h.Calls.Single().Body);
        Assert.Equal(new[] { a.Token, b.Token }, doc.RootElement.GetProperty("tokens").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { 1 }, changed.Select(t => t.Id).ToArray());
        Assert.Equal(Support.Fixed, a.Status);
        Assert.Equal("Исправили", a.Reply);
        Assert.True(a.Unread);
        Assert.True(a.Tg);
        Assert.False(b.Unread);
        Assert.Equal(1, Support.UnreadCount(store));
        Assert.True(Support.NeedsUpdate(a));
        Assert.Equal("Исправлено в версии 99.0.0", Support.StatusLine(a));
        Assert.Equal("Обращение #1: Исправлено в версии 99.0.0. Обновите программу", Support.NoticeText(a));
    }

    [Fact]
    public async Task Same_answer_twice_does_not_raise_a_new_notification()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var a = Open(1);
        var store = new Support.Store { Items = { a } };
        h.Reply = """{"tickets":[{"id":1,"status":"work","fixVersion":null,"reply":"Смотрю","updated":10,"tg":false}]}""";

        Assert.Single(await Support.PollAsync(store));
        Support.MarkSeen(store);
        Assert.Empty(await Support.PollAsync(store));
        Assert.False(a.Unread);
        Assert.Equal("Обращение #1: В работе. Есть ответ", Support.NoticeText(a));
    }

    [Fact]
    public async Task Fixed_in_an_installed_or_older_version_does_not_ask_to_update()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var a = Open(1);
        var store = new Support.Store { Items = { a } };
        h.Reply = """{"tickets":[{"id":1,"status":"fixed","fixVersion":"0.0.1","reply":null,"updated":10,"tg":false}]}""";

        await Support.PollAsync(store);

        Assert.False(Support.NeedsUpdate(a));
        Assert.Equal("Обращение #1: Исправлено в версии 0.0.1", Support.NoticeText(a));
    }

    [Fact]
    public async Task Poll_with_no_network_or_server_error_keeps_state_quietly()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        var a = Open(1);
        var store = new Support.Store { Items = { a } };

        h.Offline = true;
        Assert.Empty(await Support.PollAsync(store));
        h.Offline = false;
        h.Status = HttpStatusCode.InternalServerError;
        Assert.Empty(await Support.PollAsync(store));
        h.Status = HttpStatusCode.OK;
        h.Reply = "не json";
        Assert.Empty(await Support.PollAsync(store));

        Assert.Equal(Support.New, a.Status);
        Assert.False(a.Unread);
    }

    [Fact]
    public void Store_survives_a_save_and_load_round_trip()
    {
        var store = new Support.Store { ContactKind = "email", ContactValue = "a@b.ru" };
        store.Items.Add(new Support.Ticket { Id = 9, Token = "t", Code = "c", Preview = "p", Status = Support.Work, Reply = "r", Unread = true, Created = new DateTime(2026, 11, 3, 12, 0, 0) });
        Support.Save(store);
        try
        {
            var loaded = Support.Load();
            Assert.Equal("a@b.ru", loaded.ContactValue);
            var t = Assert.Single(loaded.Items);
            Assert.Equal((9, "r", true), (t.Id, t.Reply, t.Unread));
        }
        finally { try { File.Delete(Storage.PathOf(Support.StoreFile)); } catch (Exception) { } }
    }

    [Fact]
    public async Task Cyrillic_is_not_escaped_and_a_huge_report_is_clipped_to_the_server_limit()
    {
        var (h, restore) = Fake();
        using var _ = restore;
        h.Reply = """{"id":1,"code":"aabbccdd"}""";
        var report = new string('ж', 200_000);

        await Support.SendAsync(new Support.Store(), Support.NewToken(), "тест обращения", null, report);

        var body = h.Calls.Single().Body;
        Assert.DoesNotContain("\\u0436", body);
        using var doc = JsonDocument.Parse(body);
        var sent = doc.RootElement.GetProperty("report").GetString()!;
        Assert.True(sent.Length <= Support.MaxReport);
        Assert.Contains("отчет сокращен", sent);
        Assert.Equal(report[..100], Support.ClipReport(report[..100]));
    }
}
