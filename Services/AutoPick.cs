using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Автоподбор рабочего режима: программа сама пробует лучшие серверы по очереди и останавливается на первом, через который
/// открываются нейросети. Здесь выбор кандидатов и разбор результата проверки, само подключение делает главное окно.
/// Подбор идет только по DNS: серверы, и каждый из них подключается как обычно (с DoH, если он у сервера есть, и с «Двумя DNS»,
/// если они включены в настройках). Страну выхода подбор сам не меняет: подходят только серверы, прошедшие защиту аккаунтов.
/// </summary>
public static class AutoPick
{
    /// <summary>Сколько серверов пробовать. Каждая попытка это смена DNS и проверка, больше пяти было бы долго.</summary>
    public const int MaxTries = 5;

    /// <summary>Кандидаты по порядку: сначала те, что лучше подходят по стране выхода, потом по оценке.</summary>
    public static List<DnsServer> Candidates(IEnumerable<DnsServer> usable, Func<DnsServer, int>? fit = null, Guid? currentOrder = null) =>
        usable.Where(s => s.Status is ServerStatus.Ok or ServerStatus.Partial && !s.IsJunk)
              .OrderByDescending(s => fit?.Invoke(s) ?? 0).ThenByDescending(s => s.Score).ThenBy(s => s.LatencyMs ?? long.MaxValue)
              .Take(MaxTries).ToList();

    /// <summary>Сколько сервисов открывается, закрыто и пока не подтверждено (Gemini без ключа API не подтвердить, это не отказ).</summary>
    public static (int Open, int Closed, int Unknown) Tally(IEnumerable<ServiceState> services)
    {
        int open = 0, closed = 0, unknown = 0;
        foreach (var s in services)
        {
            var a = s.Access ?? "";
            if (a.StartsWith('✓')) open++;
            else if (a.Contains("не подтвержден", StringComparison.OrdinalIgnoreCase) || a.Contains("не проверен", StringComparison.OrdinalIgnoreCase)) unknown++;
            else closed++;
        }
        return (open, closed, unknown);
    }

    /// <summary>Режим подходит, если хоть одна нейросеть открывается и ни одна не закрыта.</summary>
    public static bool IsGood((int Open, int Closed, int Unknown) t) => t.Open >= 1 && t.Closed == 0;

    /// <summary>Чем больше открытых и меньше закрытых, тем лучше. Неподтвержденные не считаются ни за, ни против.</summary>
    public static int Score((int Open, int Closed, int Unknown) t) => t.Open * 10 - t.Closed * 10;
}
