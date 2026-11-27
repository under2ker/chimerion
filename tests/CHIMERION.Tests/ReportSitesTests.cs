using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Названия сайтов человека в отчете поддержки (аудит P-04).</summary>
public class ReportSitesTests
{
    private static readonly string[] Keep = { "chatgpt.com", "claude.ai", "gemini.google.com" };

    [Fact]
    public void Watched_sites_become_numbered_and_ai_hosts_stay()
    {
        var log = "12:00 Проверка сайтов: example.org: не открывается у вас. Включите обход\n" +
                  "12:05 Проверка сайтов: www.example.org снова открывается\n" +
                  "12:06 Проверка сайтов: my-bank.ru: заблокирован провайдером\n" +
                  "12:07 Проверка сайтов: https://Example.org/login недоступен\n" +
                  "12:08 chatgpt.com: 200, claude.ai: 200\n";
        var r = Diagnostics.HideSites(log, new[] { "my-bank.ru", "example.org", "chatgpt.com" }, Keep);
        Assert.DoesNotContain("example.org", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("my-bank", r);
        Assert.Contains("Проверка сайтов: [сайт 1]: не открывается", r);   // example.org — первый по алфавиту
        Assert.Contains("Проверка сайтов: [сайт 1] снова открывается", r); // поддомен под тем же номером
        Assert.Contains("https://[сайт 1]/login", r);
        Assert.Contains("Проверка сайтов: [сайт 2]: заблокирован", r);
        Assert.Contains("chatgpt.com: 200, claude.ai: 200", r);
        Assert.Contains("заменены на [сайт N]", r);
    }

    [Fact]
    public void Similar_names_are_not_touched()
    {
        var r = Diagnostics.HideSites("notexample.org и example.org.evil.net и example.org", new[] { "example.org" }, Keep);
        Assert.Contains("notexample.org", r);
        Assert.Contains("example.org.evil.net", r);
        Assert.EndsWith("и [сайт 1]" + Environment.NewLine + "Названия сайтов из наблюдения заменены на [сайт N]." + Environment.NewLine, r);
    }

    [Fact]
    public void Nothing_watched_report_is_unchanged()
    {
        const string text = "Отчет CHIMERION\n[ок] Подключено\n";
        Assert.Equal(text, Diagnostics.HideSites(text, Array.Empty<string>(), Keep));
        Assert.Equal(text, Diagnostics.HideSites(text, new[] { "example.org" }, Keep)); // сайт в отчете не встретился
    }

    [Fact]
    public void Full_report_uses_watch_list()
    {
        var prev = Diagnostics.WatchedHosts;
        Diagnostics.WatchedHosts = () => new[] { "secret-site.org" };
        try
        {
            var report = new DiagReport(new List<DiagItem>(), null, DateTime.Now);
            var text = Diagnostics.ToText(report, new[] { "12:00 Проверка сайтов: secret-site.org: не открывается" });
            Assert.DoesNotContain("secret-site", text);
            Assert.Contains("[сайт 1]", text);
        }
        finally { Diagnostics.WatchedHosts = prev; }
    }
}
