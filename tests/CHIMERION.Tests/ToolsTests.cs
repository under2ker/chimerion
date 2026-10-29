using Dnex.Services.Tools;
using Xunit;

namespace Dnex.Tests;

public class ToolsTests
{
    // Начало стратегии из zapret-discord-youtube 1.10.4 (general (FAKE TLS AUTO).bat), сокращено
    private const string Bat = "@echo off\r\nchcp 65001 > nul\r\ncd /d \"%~dp0\"\r\ncall service.bat status_zapret\r\n" +
        "set \"BIN=%~dp0bin\\\"\r\nset \"LISTS=%~dp0lists\\\"\r\ncd /d %BIN%\r\n\r\n" +
        "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% --wf-udp=443,%GameFilterUDP% ^\r\n" +
        "--filter-tcp=443 --hostlist=\"%LISTS%list-google.txt\" --dpi-desync-fake-tls=^! --dpi-desync-fake-quic=\"%BIN%quic_initial_www_google_com.bin\"\r\n";

    [Fact]
    public void Strategy_args_are_taken_from_bat_with_paths_and_escapes()
    {
        var a = ZapretTool.ParseArgs(Bat, @"C:\Z\bin\", @"C:\Z\lists\", games: false)!;
        Assert.Equal("--wf-tcp=80,443,12 --wf-udp=443,12  --filter-tcp=443 --hostlist=\"C:\\Z\\lists\\list-google.txt\" " +
                     "--dpi-desync-fake-tls=! --dpi-desync-fake-quic=\"C:\\Z\\bin\\quic_initial_www_google_com.bin\"", a);
    }

    [Fact]
    public void Game_filter_opens_high_ports()
    {
        var a = ZapretTool.ParseArgs(Bat, @"C:\Z\bin\", @"C:\Z\lists\", games: true)!;
        Assert.StartsWith("--wf-tcp=80,443,1024-65535 --wf-udp=443,1024-65535", a);
    }

    [Fact]
    public void Bat_without_winws_gives_null() =>
        Assert.Null(ZapretTool.ParseArgs("@echo off\r\necho hi\r\n", @"C:\Z\bin\", @"C:\Z\lists\", false));

    [Theory]
    [InlineData("general.bat", "Основная")]
    [InlineData("general (ALT2).bat", "ALT2")]
    [InlineData("general (FAKE TLS AUTO ALT3).bat", "FAKE TLS AUTO ALT3")]
    public void Strategy_titles(string file, string title) => Assert.Equal(title, ZapretTool.StrategyTitle(file));

    [Theory]
    [InlineData("v1.11.2", "1.11.2")]
    [InlineData("1.10.4", "1.10.4")]
    [InlineData("v2.0-beta", "2.0")]
    public void Release_tags(string tag, string version) => Assert.Equal(Version.Parse(version), GitHubRelease.ParseTag(tag));

    [Fact]
    public void Telegram_link_is_built_like_the_proxy_does()
    {
        Assert.Equal("tg://proxy?server=127.0.0.1&port=1443&secret=dd0123abcd",
            TgProxyTool.ProxyLinkFrom("{\"host\":\"127.0.0.1\",\"port\":1443,\"secret\":\"0123abcd\"}"));
        Assert.Equal("tg://proxy?server=127.0.0.1&port=2000&secret=ddff",
            TgProxyTool.ProxyLinkFrom("{\"host\":\"0.0.0.0\",\"port\":2000,\"secret\":\"ff\"}"));
        Assert.Null(TgProxyTool.ProxyLinkFrom("{\"host\":\"127.0.0.1\"}"));
        Assert.Null(TgProxyTool.ProxyLinkFrom("не json"));
    }
}

public class EgressWatchTests
{
    [Fact]
    public void Change_of_country_on_same_server_is_reported()
    {
        var w = new Dnex.Services.EgressWatch();
        Assert.Empty(w.Update("s1", new[] { ("Claude", (string?)"NL"), ("ChatGPT", (string?)"NL") }));
        Assert.Empty(w.Update("s1", new[] { ("Claude", (string?)"RU"), ("ChatGPT", (string?)"NL") })); // один раз: ждем подтверждения
        var c = w.Update("s1", new[] { ("Claude", (string?)"RU"), ("ChatGPT", (string?)"NL") });
        var only = Assert.Single(c);
        Assert.Equal(("Claude", "NL", "RU", true), (only.Service, only.From, only.To, only.ToClosed));
    }

    [Fact]
    public void New_server_and_missing_data_are_not_changes()
    {
        var w = new Dnex.Services.EgressWatch();
        w.Update("s1", new[] { ("Claude", (string?)"NL") });
        Assert.Empty(w.Update("s2", new[] { ("Claude", (string?)"DE") }));   // другой сервер: заново
        Assert.Empty(w.Update("s2", new[] { ("Claude", (string?)null) }));   // проверка не дала страну
        Assert.Empty(w.Update(null, new[] { ("Claude", (string?)"FR") }));   // не подключено
        w.Update("s2", new[] { ("Claude", (string?)"DE") });
        Assert.Empty(w.Update("s2", new[] { ("Claude", (string?)"FR") }));
        var c = Assert.Single(w.Update("s2", new[] { ("Claude", (string?)"FR") }));
        Assert.False(c.ToClosed);
    }

    [Fact]
    public void Single_jump_and_back_is_not_reported()
    {
        // Журнал пользователя: Windows разово ушла на запасной DNS с выходом в Казахстане и вернулась. Пугать незачем
        var w = new Dnex.Services.EgressWatch();
        w.Update("s1", new[] { ("Claude", (string?)"NL") });
        Assert.Empty(w.Update("s1", new[] { ("Claude", (string?)"KZ") }));
        Assert.Empty(w.Update("s1", new[] { ("Claude", (string?)"NL") }));
        Assert.Empty(w.Update("s1", new[] { ("Claude", (string?)"KZ") }));
        Assert.Empty(w.Update("s1", new[] { ("Claude", (string?)"NL") }));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Report_hides_personal_data_but_keeps_server_addresses()
    {
        var text = "Внешний 88.86.206.62, сервер 83.217.211.149, роутер 192.168.1.1, wsl 172.20.0.5, " +
                   @"путь C:\Users\Alexandr\AppData, почта user.name@gmail.com";
        var s = Dnex.Services.Diagnostics.Sanitize(text, "88.86.206.62", "Alexandr");
        Assert.DoesNotContain("88.86.206.62", s);
        Assert.DoesNotContain("83.217.211.149", s);
        Assert.DoesNotContain("192.168.1.1", s);
        Assert.DoesNotContain("172.20.0.5", s);
        Assert.DoesNotContain("Alexandr", s);
        Assert.DoesNotContain("gmail", s);
    }

    [Fact]
    public void Long_issue_link_is_cut_to_the_limit()
    {
        var url = Dnex.Services.Diagnostics.IssueUrl("Проблема", new string('я', 20000), 7000);
        Assert.True(url.Length <= 7000);
        Assert.Contains(Uri.EscapeDataString("отчет обрезан"), url);
        Assert.StartsWith("https://github.com/under2ker/chimerion/issues/new?title=", url);
    }
}

public class UpdateSignatureTests
{
    [Fact]
    public void Only_archive_signed_by_the_key_is_accepted()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var data = System.Text.Encoding.UTF8.GetBytes("архив CHIMERION");
        var sig = Convert.ToBase64String(key.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.True(Dnex.Services.Updater.VerifySignature(data, sig, pub));
        Assert.False(Dnex.Services.Updater.VerifySignature(System.Text.Encoding.UTF8.GetBytes("подмена"), sig, pub));
        Assert.False(Dnex.Services.Updater.VerifySignature(data, sig));       // встроенный ключ автора: чужая подпись не проходит
        Assert.False(Dnex.Services.Updater.VerifySignature(data, "не base64", pub));
    }
}

public class StrategyRankTests
{
    [Fact]
    public void More_open_services_first_then_faster()
    {
        var r = ZapretTool.Rank(new[]
        {
            new ZapretTool.StrategyResult(null, false, false, 0),
            new ZapretTool.StrategyResult("general (ALT).bat", true, false, 100),
            new ZapretTool.StrategyResult("general (ALT2).bat", true, true, 900),
            new ZapretTool.StrategyResult("general.bat", true, true, 300),
        });
        Assert.Equal(new[] { "Основная", "ALT2", "ALT", "Без обхода" }, r.Select(x => x.Title));
    }
}

public class HistoryTests
{
    [Fact]
    public void History_keeps_two_weeks_and_reports_week()
    {
        var s = new Dnex.Models.DnsServer();
        var now = DateTime.Now;
        s.AddHistory(true, 40, now.AddDays(-20));                 // старше 14 дней: выпадет
        for (int i = 0; i < 6; i++) s.AddHistory(true, 50, now.AddDays(-10).AddHours(i)); // прошлая неделя: 100%
        s.AddHistory(true, 30, now.AddHours(-3));
        s.AddHistory(false, null, now.AddHours(-2));
        s.AddHistory(true, 90, now.AddHours(-1));
        s.AddHistory(false, null, now);
        Assert.Equal(10, s.History.Count);
        Assert.Contains("работал в 2 из 4 проверок (50%)", s.HistoryText);
        Assert.Contains("пинг 30–90 мс", s.HistoryText);
        Assert.Contains("Стал хуже: неделей раньше 100%", s.HistoryText);
    }
}

public class RemoteCatalogTests
{
    [Fact]
    public void Signed_catalog_expiry_and_legacy_date_are_enforced()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string Sign(string json) => Convert.ToBase64String(key.SignData(System.Text.Encoding.UTF8.GetBytes(json), System.Security.Cryptography.HashAlgorithmName.SHA256));
        string Day(int offset) => DateTime.UtcNow.AddDays(offset).ToString("yyyy-MM-dd");
        string Json(int updated, int? expires = null) => System.Text.Json.JsonSerializer.Serialize(new { updated = Day(updated), expires = expires is int n ? Day(n) : null });
        var fresh = Json(-1, 1);
        var expired = Json(-10, -1);
        var old = Json(-181);
        var future = Json(10);
        Assert.NotNull(Dnex.Services.RemoteCatalog.Parse(fresh, Sign(fresh), pub));
        Assert.Null(Dnex.Services.RemoteCatalog.Parse(expired, Sign(expired), pub));
        Assert.Null(Dnex.Services.RemoteCatalog.Parse(old, Sign(old), pub));
        Assert.Null(Dnex.Services.RemoteCatalog.Parse(future, Sign(future), pub));
    }

    [Fact]
    public void Catalog_is_accepted_only_with_valid_signature()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var json = Dnex.Services.RemoteCatalog.Serialize(new Dnex.Services.RemoteCatalog.Catalog
        {
            Updated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Servers = { new() { Name = "Пример", Primary = "203.0.113.5", Doh = "https://dns.example/dns-query", Site = "https://example.org/" } },
        });
        var sig = Convert.ToBase64String(key.SignData(System.Text.Encoding.UTF8.GetBytes(json), System.Security.Cryptography.HashAlgorithmName.SHA256));
        var c = Dnex.Services.RemoteCatalog.Parse(json, sig, pub)!;
        var rows = Dnex.Services.RemoteCatalog.Servers(c).ToList();
        Assert.Equal(2, rows.Count);                                   // IP и DoH отдельными строками
        Assert.Equal("Пример (DoH)", rows[1].Name);
        Assert.Null(Dnex.Services.RemoteCatalog.Parse(json.Replace("203.0.113.5", "203.0.113.6"), sig, pub)); // подмена адреса
    }
}

public class SplitDnsTests
{
    [Fact]
    public void Ai_domains_cover_services_but_not_whole_google()
    {
        var st = new Dnex.Models.AppSettings();
        st.SplitDns.ExtraAiDomains.Add("perplexity.ai");
        var ns = Dnex.Services.SplitDns.Namespaces(st);
        Assert.Contains("chatgpt.com", ns);
        Assert.Contains(".chatgpt.com", ns);          // поддомены
        Assert.Contains(".claude.ai", ns);
        Assert.Contains("gemini.google.com", ns);
        Assert.DoesNotContain(".google.com", ns);      // остальной Google идет через общий DNS
        Assert.DoesNotContain(".googleapis.com", ns);
        Assert.DoesNotContain("accounts.google.com", ns); // вход в Google общий для всех сервисов
        Assert.Contains(".perplexity.ai", ns);
        Assert.Equal(ns.Count, ns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("https://Perplexity.ai/search", "perplexity.ai")]
    [InlineData("*.example.org", "example.org")]
    [InlineData(" .mistral.ai ", "mistral.ai")]
    [InlineData("не домен", "")]
    [InlineData("localhost", "")]
    public void User_domains_are_cleaned(string input, string expected) => Assert.Equal(expected, Dnex.Services.SplitDns.Clean(input));
}

public class SiteCheckTests
{
    [Theory]
    [InlineData("https://uiverse.io/", "uiverse.io")]
    [InlineData("Uiverse.io/elements?x=1", "uiverse.io")]
    [InlineData("http://www.example.org", "www.example.org")]
    [InlineData("не сайт", "")]
    public void Host_is_taken_from_link_or_name(string input, string host) => Assert.Equal(host, Dnex.Services.SiteCheck.HostOf(input));
}

public class BypassListsTests
{
    [Fact]
    public void Lists_are_cleaned_to_valid_domains_and_networks()
    {
        var d = RemoteLists.Clean("# коммент\ninstagram.com\n*.fbcdn.net\nне домен\nlocalhost\nInstagram.com\n", ip: false);
        Assert.Equal(new[] { "instagram.com", "fbcdn.net" }, d);
        var ip = RemoteLists.Clean("104.16.0.0/12\n157.240.0.174\n0.0.0.1/32\n2a03:2880::/32\nмусор\n", ip: true);
        Assert.Equal(new[] { "104.16.0.0/12", "157.240.0.174", "2a03:2880::/32" }, ip);
    }

    [Fact]
    public void Embedded_bypass_sets_are_signed_and_complete()
    {
        var m = typeof(Dnex.Services.BypassCatalog).GetMethod("Embedded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var data = (Dnex.Services.BypassCatalog.Data?)m.Invoke(null, null);
        Assert.NotNull(data);                                   // подпись встроенной копии сошлась
        var meta = data!.Groups.Single(g => g.Id == "meta");
        Assert.Contains("cdninstagram.com", meta.Domains);
        Assert.Contains("fbcdn.net", meta.Domains);
        Assert.True(data.Groups.Count >= 10);
    }
}

public class StrategyRankWithSitesTests
{
    [Fact]
    public void Own_sites_count_in_the_score()
    {
        var r = ZapretTool.Rank(new[]
        {
            new ZapretTool.StrategyResult("general (ALT9).bat", true, true, 300, new[] { "uiverse.io" }, new[] { "hexium.gg" }),
            new ZapretTool.StrategyResult("general (ALT2).bat", true, true, 500, new[] { "uiverse.io", "hexium.gg" }, System.Array.Empty<string>()),
            new ZapretTool.StrategyResult(null, false, false, 0, System.Array.Empty<string>(), new[] { "uiverse.io", "hexium.gg" }),
        });
        Assert.Equal("ALT2", r[0].Title);                 // открыла все, хоть и медленнее
        Assert.Equal((4, 4), (r[0].Score, r[0].MaxScore));
        Assert.Equal((3, 4), (r[1].Score, r[1].MaxScore));
    }
}
