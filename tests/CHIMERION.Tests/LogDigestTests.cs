using System.Net;
using System.Net.Http;
using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Сводка журнала, очистка личного в журнале и проверка Google «sorry».</summary>
public class LogDigestTests
{
    private static readonly DateTime T0 = new(2026, 1, 10, 12, 0, 0);

    private static (DateTime, string) L(int sec, string session, string text) => (T0.AddSeconds(sec), $"[Подробно; сеанс={session}; запись={sec}] {text}");

    private static List<(DateTime, string)> Sample() => new()
    {
        L(0, "aaaa1111", "Начало сеанса; версия=0.1.160; Windows=10.0; разрядность=64"),
        L(1, "aaaa1111", "Сервис Gemini; сайт=True; облегченная=False: Доступ=✓ доступен; через Smart DNS=True"),
        L(2, "aaaa1111", "Начало: DNS через сервер; номер=0123456789abcdef0123456789abcdef; DoH=False; родитель=нет"),
        L(3, "aaaa1111", "DNS через сервер; номер=0123456789abcdef0123456789abcdef; DoH=False: TimeoutException; HResult=0x80131505; стек=DnsWire.ResolveUdpAsync"),
        L(4, "aaaa1111", "Итог: DNS через сервер; номер=0123456789abcdef0123456789abcdef; DoH=False; истекло время ожидания; длительность=3000 мс"),
        L(10, "bbbb2222", "Начало сеанса; версия=0.1.176; Windows=10.0; разрядность=64"),
        L(11, "bbbb2222", "Проверка API сервиса: TaskCanceledException; HResult=0x8013153B; тайм-аут; стек=ConnectionProbe.ProbeAsync"),
        L(12, "bbbb2222", "Итог: Проверка API сервиса; истекло время ожидания; длительность=10000 мс"),
        L(13, "bbbb2222", "Сервис Gemini; сайт=True; облегченная=False: Доступ=✗ доступ закрыт; через Smart DNS=True"),
        L(14, "bbbb2222", "Событие: Помех в системе не найдено"),
        L(15, "bbbb2222", "Событие: Не удалось восстановить DNS"),
        L(16, "bbbb2222", "Событие: Не удалось восстановить DNS"),
        L(17, "bbbb2222", "Конец сеанса; код выхода=0"),
        L(20, "cccc3333", "Начало сеанса; версия=0.1.176; Windows=10.0; разрядность=64"),
    };

    [Fact]
    public void Digest_lists_sessions_and_marks_one_that_ended_without_closing()
    {
        var text = string.Join("\n", LogDigest.Build(Sample()));
        Assert.Contains("сеанс aaaa1111, версия 0.1.160", text);
        Assert.Contains("оборвалась без записи о закрытии", text);
        Assert.Contains("сеанс bbbb2222, версия 0.1.176, строк 8: закрыта, код 0", text);
        Assert.Contains("сеанс cccc3333, версия 0.1.176, строк 1: работает", text);
    }

    [Fact]
    public void Digest_groups_failures_with_cause_and_keeps_server_list_apart()
    {
        var lines = LogDigest.Build(Sample());
        var failure = Assert.Single(lines, l => l.Contains("Проверка API сервиса"));
        Assert.Contains("1× Проверка API сервиса: истекло время ожидания", failure);
        Assert.Contains("TaskCanceledException; тайм-аут ×1", failure);
        Assert.DoesNotContain(lines, l => l.Contains("× DNS через сервер"));
        Assert.Contains(lines, l => l.StartsWith("Проверка списка серверов: неудачных проверок 1, разных серверов 1"));
        Assert.Contains(lines, l => l.Contains("DNS: TimeoutException ×1"));
    }

    [Fact]
    public void Digest_shows_access_changes_and_problem_events_only()
    {
        var text = string.Join("\n", LogDigest.Build(Sample()));
        Assert.Contains("Gemini (сайт): ✓ доступен → ✗ доступ закрыт", text);
        Assert.Contains("2× Не удалось восстановить DNS", text);
        Assert.DoesNotContain("Помех в системе не найдено", text);
    }

    [Fact]
    public void Report_puts_digest_first_and_leaves_server_list_details_out_of_the_log_part()
    {
        var entries = Sample().Select(x => new LogEntry(x.Item1, x.Item2)).ToList();
        var report = MainWindow.ReportLines(entries).ToList();
        Assert.StartsWith("=== Сводка журнала", report[0]);
        int logPart = report.FindIndex(l => l.StartsWith("=== Журнал без подробностей"));
        Assert.True(logPart > 0);
        Assert.DoesNotContain(report.Skip(logPart), l => l.Contains("DNS через сервер"));
        Assert.Contains(report.Skip(logPart), l => l.Contains("Проверка API сервиса"));
    }

    [Fact]
    public void Sanitize_hides_user_and_computer_names_anywhere_but_not_generic_words()
    {
        var text = Diagnostics.Sanitize("Подключено: DNS CHIMERION — Ivanov PC; хост ZORK-PC; Ivanovich; права Admin",
            null, userName: "Ivanov", machineName: "ZORK-PC");
        Assert.Contains("DNS CHIMERION — [пользователь] PC", text);
        Assert.Contains("хост [компьютер]", text);
        Assert.Contains("Ivanovich", text); // часть другого слова не трогаем
        Assert.Equal("права Admin", Diagnostics.Sanitize("права Admin", null, userName: "Admin", machineName: "PC"));
    }

    [Fact]
    public void Sanitize_hides_url_queries_and_key_like_path_parts_but_keeps_domain()
    {
        var text = Diagnostics.Sanitize("DoH https://dns.example.test/dns-query/a1B2c3D4e5F6g7H8i9 и https://example.test/page?session=abc#x, статья https://habr.example/articles/123456/",
            null, userName: "Ivanov", machineName: "ZORK-PC");
        Assert.Contains("https://dns.example.test/dns-query/[ключ]", text);
        Assert.Contains("https://example.test/page?[скрыто]", text);
        Assert.Contains("https://habr.example/articles/123456/", text);
        Assert.DoesNotContain("a1B2c3D4e5F6g7H8i9", text);
        Assert.DoesNotContain("session=abc", text);
    }

    [Fact]
    public void Google_sorry_that_lets_through_counts_as_passed()
    {
        Assert.True(ServiceAccess.IsGoogleSorry("https://www.google.com/sorry/index?continue=https://gemini.google.com/app"));
        Assert.True(ServiceAccess.PassesWithoutCaptcha(302, "https://gemini.google.com/app?google_abuse=GOOGLE_ABUSE_EXEMPTION%3DID%3D1"));
        Assert.False(ServiceAccess.PassesWithoutCaptcha(429, null));
        Assert.False(ServiceAccess.PassesWithoutCaptcha(200, null));
    }

    [Fact]
    public async Task Google_sorry_is_requested_once_and_only_on_google()
    {
        int calls = 0;
        using var pass = new HttpClient(new Fake(_ =>
        {
            calls++;
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://gemini.google.com/app?google_abuse=GOOGLE_ABUSE_EXEMPTION%3DID%3D1");
            return r;
        }));
        Assert.True(await ServiceAccess.GoogleSorryPassesAsync(pass, "https://www.google.com/sorry/index?continue=x", default));
        Assert.Equal(1, calls);
        Assert.False(await ServiceAccess.GoogleSorryPassesAsync(pass, "https://evil.example/sorry", default));
        Assert.False(await ServiceAccess.GoogleSorryPassesAsync(pass, "http://www.google.com/sorry/index", default));
        Assert.Equal(1, calls);

        using var captcha = new HttpClient(new Fake(_ => new HttpResponseMessage((HttpStatusCode)429)));
        Assert.False(await ServiceAccess.GoogleSorryPassesAsync(captcha, "https://www.google.com/sorry/index?continue=x", default));
    }

    [Fact]
    public async Task Error_text_skips_task_plumbing_and_marks_http_timeout()
    {
        Exception caught;
        try { await ThrowTimeoutAsync(); throw new InvalidOperationException(); }
        catch (TaskCanceledException e) { caught = e; }
        var text = SupportTrace.Error(caught);
        Assert.Contains("тайм-аут", text);
        Assert.DoesNotContain("TaskAwaiter", text);
        Assert.DoesNotContain("ExceptionDispatchInfo", text);
        Assert.Contains("LogDigestTests.ThrowTimeoutAsync", text);
    }

    private static async Task ThrowTimeoutAsync()
    {
        await Task.Yield();
        throw new TaskCanceledException("private", new TimeoutException("private"));
    }

    [Fact]
    public void Autostart_note_says_whether_the_other_copy_is_older()
    {
        Assert.Equal("версия 0.1.167, старее этой", AutoStart.OtherCopyNote(new Version(0, 1, 167), new Version(0, 1, 176)));
        Assert.Equal("версия 0.1.180, новее этой", AutoStart.OtherCopyNote(new Version(0, 1, 180), new Version(0, 1, 176, 0)));
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
