using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Автоподбор: порядок кандидатов и разбор результата проверки нейросетей.</summary>
public class AutoPickTests
{
    private static ServiceState Svc(string name, string access) => new(name, name + ".example", "203.0.113.5", true, access);

    [Fact]
    public void Open_closed_and_unconfirmed_are_counted_apart()
    {
        var t = AutoPick.Tally(new[] { Svc("ChatGPT", "✓ доступен"), Svc("Claude", "✗ доступ закрыт"), Svc("Gemini", "✗ не подтвержден") });
        Assert.Equal((1, 1, 1), t);
    }

    [Fact]
    public void Good_mode_has_an_open_service_and_nothing_closed()
    {
        Assert.True(AutoPick.IsGood((2, 0, 1)));   // Gemini без ключа API не подтвердить: это не отказ
        Assert.True(AutoPick.IsGood((3, 0, 0)));
        Assert.False(AutoPick.IsGood((2, 1, 0)));  // один закрыт
        Assert.False(AutoPick.IsGood((0, 0, 3)));  // ничего не подтверждено
    }

    [Fact]
    public void More_open_and_fewer_closed_scores_higher()
    {
        Assert.True(AutoPick.Score((3, 0, 0)) > AutoPick.Score((2, 1, 0)));
        Assert.True(AutoPick.Score((2, 1, 0)) > AutoPick.Score((0, 3, 0)));
        Assert.Equal(AutoPick.Score((2, 0, 0)), AutoPick.Score((2, 0, 5))); // неподтвержденные не влияют
    }

    private static DnsServer S(string ip, ServerStatus st, int ok, int total, long ms, bool junk = false) =>
        new() { Name = ip, Primary = ip, Status = st, ChecksOk = ok, ChecksTotal = total, LatencyMs = ms, NoProxy = junk };

    [Fact]
    public void Candidates_are_working_servers_best_first_and_limited()
    {
        var list = new List<DnsServer>();
        for (int i = 1; i <= 9; i++) list.Add(S($"203.0.113.{i}", ServerStatus.Ok, 3, 3, 20 + i * 10));
        list.Add(S("203.0.113.50", ServerStatus.Fail, 0, 3, 30));
        list.Add(S("203.0.113.51", ServerStatus.Unknown, 0, 0, 30));
        list.Add(S("203.0.113.52", ServerStatus.Ok, 3, 3, 15, junk: true)); // обычный DNS не подходит
        var c = AutoPick.Candidates(list);
        Assert.Equal(AutoPick.MaxTries, c.Count);
        Assert.DoesNotContain(c, s => s.Primary is "203.0.113.50" or "203.0.113.51" or "203.0.113.52");
        Assert.Equal("203.0.113.1", c[0].Primary);   // самый быстрый среди равных по стабильности
    }

    [Fact]
    public void Country_fit_wins_over_score()
    {
        var a = S("203.0.113.1", ServerStatus.Ok, 3, 3, 20);
        var b = S("203.0.113.2", ServerStatus.Ok, 3, 3, 300);
        var c = AutoPick.Candidates(new[] { a, b }, s => s == b ? 1 : 0);
        Assert.Equal("203.0.113.2", c[0].Primary); // страна выхода привычная: подбор не уводит аккаунт в другую страну
    }

    [Fact]
    public void No_working_servers_gives_no_candidates()
        => Assert.Empty(AutoPick.Candidates(new[] { S("203.0.113.9", ServerStatus.Fail, 0, 3, 10) }));
}
