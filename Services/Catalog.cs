using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Встроенный каталог Smart DNS и стартовых источников. Каталог только подсказывает, что проверить:
/// работает ли сервер, решает проверка. Данные с официальных страниц сервисов (октябрь 2026).
/// </summary>
public static class Catalog
{
    private sealed record Entry(string Name, string Site, string[] Ips, string? Doh);

    private static readonly Entry[] Known =
    {
        new("MagicDNS",        "https://magicdns.ru/",                         new[] { "109.120.132.43", "94.249.177.102" }, "https://dns.magicdns.ru/dns-query"),
        new("Keyoneday DNS",   "https://keyoneday.ru/dns.html",                new[] { "212.67.13.51" },                     "https://dns.keyoneday.ru/dns-query"),
        new("DNS-AI",          "https://dns-ai.ru/",                           new[] { "194.87.196.13", "45.151.102.219" },  "https://dns.dns-ai.ru/dns-query"),
        new("Zapret DNS",      "https://wiki.zapret.moe/Zapret/zapret-dns",    new[] { "83.217.211.149" },                   "https://dns.zapret.moe/dns-query"),
        new("GeoHide",         "https://geohide.ru/",                          new[] { "159.194.200.33", "37.230.192.51" },  "https://geohide.ru/dns-query"),
        new("GeoHide EU",      "https://github.com/Kam300/KodVsego-SmartDNS",  new[] { "143.20.64.192", "45.13.237.213" },  null),
        new("Comss DNS",       "https://www.comss.ru/page.php?id=16095",       new[] { "83.220.169.155", "212.109.195.93" }, "https://dns.comss.one/dns-query"),
        new("Bezmezhau",       "https://github.com/Kam300/KodVsego-SmartDNS",  new[] { "143.20.64.55", "31.58.216.201" },   "https://dns.bezmezhau.com/dns-query"),
    };

    /// <summary>Каждый сервис даёт две записи: обычный DNS по IP и DoH — они проверяются отдельно.</summary>
    public static IEnumerable<DnsServer> Servers()
    {
        foreach (var e in Known)
        {
            yield return new DnsServer
            {
                Name = e.Name, Primary = e.Ips[0], Secondary = e.Ips.Length > 1 ? e.Ips[1] : null,
                Source = "Каталог", SourceUrl = e.Site,
            };
            if (e.Doh != null)
                yield return new DnsServer { Name = e.Name + " (DoH)", DohUrl = e.Doh, Source = "Каталог", SourceUrl = e.Site };
        }
    }

    /// <summary>Стартовые страницы, которые всегда добавляются к источникам из settings.json.</summary>
    public static readonly SourceConfig[] Sources =
    {
        new() { Name = "DnsSwitch presets (GitHub)", Url = "https://raw.githubusercontent.com/WhIteGh0s1/DnsSwitch/main/presets.txt" },
        new() { Name = "KodVsego SmartDNS (GitHub)", Url = "https://raw.githubusercontent.com/Kam300/KodVsego-SmartDNS/main/README.md" },
        new() { Name = "MagicDNS (GitHub)", Url = "https://raw.githubusercontent.com/MAECTRO/magicdns/main/README.md" },
        new() { Name = "MagicDNS", Url = "https://magicdns.ru/" },
        new() { Name = "Keyoneday", Url = "https://keyoneday.ru/dns.html" },
        new() { Name = "DNS-AI", Url = "https://dns-ai.ru/" },
        new() { Name = "Zapret DNS", Url = "https://wiki.zapret.moe/Zapret/zapret-dns" },
        new() { Name = "wifigid: DNS для ИИ", Url = "https://wifigid.ru/poleznoe-i-interesnoe/dns-dlya-ii" },
        new() { Name = "Техноварик", Url = "https://trofimovsky.ru/article/dns-dlya-gemini-i-neirosetei-rossiya-2026-chatgpt-claude-grok-bez-vpn/" },
        new() { Name = "Xbox DNS", Url = "https://xbox-dns.ru/" },
    };

    /// <summary>Поисковые фразы, по которым находятся именно посадочные страницы новых сервисов,
    /// а не статьи-обзоры: такие сайты почти всегда содержат инструкцию «Предпочтительный DNS» и адрес dns-query.</summary>
    public static readonly string[] DiscoveryQueries =
    {
        "\"предпочтительный DNS\" ChatGPT Claude без VPN",
        "\"частный DNS\" нейросети Gemini Claude",
        "\"dns-query\" ChatGPT Claude Gemini",
        "\"Назначение DNS-сервера\" нейросети",
        "smart DNS для нейросетей бесплатно",
        "DNS для Claude из России",
        "DNS для Gemini без VPN новый",
        "DNS разблокировка ИИ сервисов сайт",
        "свой DNS ChatGPT Spotify без VPN",
        "github smart dns chatgpt claude russia",
    };

    /// <summary>Добавляет встроенные источники и запросы к настройкам пользователя (без дублей).</summary>
    public static void Merge(AppSettings st)
    {
        var urls = st.Sources.Select(s => s.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Sources.Where(s => !urls.Contains(s.Url.TrimEnd('/'))))
            st.Sources.Add(new SourceConfig { Name = s.Name, Url = s.Url, Type = s.Type });
        var qs = st.DiscoveryQueries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        st.DiscoveryQueries.AddRange(DiscoveryQueries.Where(q => !qs.Contains(q)));
    }
}
