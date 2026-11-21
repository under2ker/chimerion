using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Поиск новых серверов: досрочная остановка, отбор ссылок Telegram, отсев не ответивших, новые запросы.</summary>
public class SearchTuneUpTests
{
    [Fact]
    public void Crawl_stops_early_only_for_deep_pages_after_the_limit()
    {
        int limit = SourceParser.StalePages;
        Assert.False(SourceParser.IsStale(limit, 0, depth: 1));          // ровно предел еще терпим
        Assert.True(SourceParser.IsStale(limit + 1, 0, depth: 1));       // дольше предела без находок: хватит
        Assert.False(SourceParser.IsStale(900, 700, depth: 3));          // недавно была находка
        Assert.False(SourceParser.IsStale(5000, 0, depth: 0));           // стартовые страницы обходим всегда
    }

    [Fact]
    public void Telegram_channels_become_feeds_and_other_links_are_dropped()
    {
        Assert.Equal("https://t.me/s/dns_club_ru", SourceParser.PrepareUrl(new Uri("https://t.me/dns_club_ru"))!.AbsoluteUri);
        var feed = new Uri("https://t.me/s/dns_club_ru");
        Assert.Equal(feed, SourceParser.PrepareUrl(feed));
        Assert.Null(SourceParser.PrepareUrl(new Uri("https://t.me/kandinsky21_bot")));   // бот
        Assert.Null(SourceParser.PrepareUrl(new Uri("https://t.me/joinchat/AAAA")));      // приглашение
        Assert.Null(SourceParser.PrepareUrl(new Uri("https://t.me/+abcdef")));            // закрытый чат
        var site = new Uri("https://wifigid.ru/poleznoe-i-interesnoe/dns-dlya-ii");
        Assert.Same(site, SourceParser.PrepareUrl(site));                                  // обычные сайты без изменений
    }

    private static DnsServer Tried(ServerStatus status, int total, int ok) => new()
    {
        Name = "s" + Guid.NewGuid().ToString("N")[..6], Status = status, ChecksTotal = total, ChecksOk = ok,
    };

    [Fact]
    public void Dead_newcomers_are_those_that_never_answered()
    {
        var dead = Tried(ServerStatus.Fail, 1, 0);
        var alive = Tried(ServerStatus.Ok, 1, 1);
        var partial = Tried(ServerStatus.Partial, 1, 1);
        var notChecked = new DnsServer { Name = "u", Status = ServerStatus.Unknown };   // проверка не успела или откатилась при сбое сети
        var manual = Tried(ServerStatus.Fail, 1, 0); manual.Manual = true;
        var active = Tried(ServerStatus.Fail, 1, 0);

        var result = ServerListGuard.DeadNewcomers(new[] { dead, alive, partial, notChecked, manual, active }, active.Id);

        Assert.Equal(new[] { dead }, result);
    }

    [Fact]
    public void Server_that_answered_before_is_not_dead_even_if_it_fails_now()
    {
        var flaky = Tried(ServerStatus.Fail, 3, 1);
        Assert.Empty(ServerListGuard.DeadNewcomers(new[] { flaky }, null));
    }

    [Fact]
    public void New_discovery_queries_are_added_once_to_existing_settings()
    {
        var st = new AppSettings();
        st.DiscoveryQueries.Clear();
        st.DiscoveryQueries.Add("своя фраза пользователя");
        Catalog.Merge(st);
        Catalog.Merge(st);   // повторный запуск не плодит дубли

        Assert.Single(st.DiscoveryQueries, q => q == "GeoHide DNS ChatGPT Gemini");
        Assert.Contains("smart dns chatgpt russia without vpn", st.DiscoveryQueries);
        Assert.Equal(st.DiscoveryQueries.Count, st.DiscoveryQueries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("своя фраза пользователя", st.DiscoveryQueries);   // своя строка не потеряна
    }
}
