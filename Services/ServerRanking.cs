using Dnex.Models;

namespace Dnex.Services;

/// <summary>Выбор лучших серверов: отдельно для каждого ИИ-сервиса и для всех сразу.</summary>
public static class ServerRanking
{
    /// <summary>Лучший сервер категории и его стабильность по сервису. Key — имя сервиса или «*» для всех сразу.</summary>
    public sealed record TopPick(string Title, string Key, DnsServer? Best, int? Stability);

    /// <summary>
    /// Кандидат должен работать с сервисом сейчас, не быть мусором и иметь проверки по сервисам. Порядок: стабильность
    /// по сервису, затем уверенность (больше проверок и меньше скачков «работает / не работает»), затем пинг.
    /// Так сервер со 100% за одну проверку не обгоняет сервер с 95% за сорок.
    /// </summary>
    public static List<TopPick> PickBest(IEnumerable<DnsServer> servers, IReadOnlyList<string> services)
    {
        var list = servers as IReadOnlyCollection<DnsServer> ?? servers.ToList();
        var cats = services.Select(n => (Title: n, Key: n,
                Stab: (Func<DnsServer, int?>)(s => s.ServiceStability(n)),
                Works: (Func<DnsServer, bool>)(s => s.Chips.Any(c => c.Name == n && c.State == "ok"))))
            .ToList();
        if (services.Count > 1)
            cats.Add((services.Count == 3 ? "Все три" : "Все сразу", "*", s => s.AllStability, s => s.Status == ServerStatus.Ok));

        var picks = new List<TopPick>();
        foreach (var c in cats)
        {
            var best = list.Where(s => !s.IsJunk && s.ServiceChecks > 0 && c.Works(s) && c.Stab(s) is > 0)
                .OrderByDescending(s => c.Stab(s))
                .ThenByDescending(s => ServerMetrics.ConfidenceOf(s))
                .ThenByDescending(s => s.ServiceChecks)
                .ThenBy(s => s.LatencyMs ?? long.MaxValue)
                .FirstOrDefault();
            picks.Add(new TopPick(c.Title, c.Key, best, best == null ? null : c.Stab(best)));
        }
        return picks;
    }
}
