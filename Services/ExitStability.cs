using Dnex.Models;

namespace Dnex.Services;

/// <summary>Одно наблюдение выхода сервера: когда, с какого адреса и из какой страны сервис видел запрос.</summary>
public sealed record ExitSeen(DateTime At, string Ip, string Country);

/// <summary>
/// Постоянство выхода сервера по накопленным проверкам (без новых запросов): держит ли сервер страну и как часто
/// меняет адрес. Смена адреса при той же стране бывает нормальной (у прокси несколько узлов), поэтому опасностью
/// не объявляется и в выборе не штрафуется. Это показатель стабильности соединения, а не вероятности блокировки.
/// </summary>
public static class ExitStability
{
    public enum Kind { Unknown, Stable, Rotates, CountryChanges }
    public sealed record Verdict(Kind Kind, string Text, int Checks);

    /// <summary>Сколько наблюдений хранить у сервера.</summary>
    public const int Keep = 12;
    /// <summary>Наблюдения ближе этого по времени сливаются: пачка проверок подряд не должна выглядеть историей.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);
    /// <summary>Учитываются наблюдения не старше этого.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);
    /// <summary>Меньше проверок — судить рано.</summary>
    public const int MinChecks = 3;

    /// <summary>Добавить наблюдение: близкое по времени к последнему заменяет его, хранится не больше Keep.</summary>
    public static void Note(List<ExitSeen> seen, ExitSeen e)
    {
        if (seen.Count > 0 && e.At - seen[^1].At < MinGap) seen[^1] = e;
        else seen.Add(e);
        if (seen.Count > Keep) seen.RemoveRange(0, seen.Count - Keep);
    }

    public static Verdict Assess(IReadOnlyList<ExitSeen> seen, DateTime now)
    {
        var recent = seen.Where(x => now - x.At <= Window).OrderBy(x => x.At).ToList();
        int n = recent.Count;
        if (n < MinChecks) return new Verdict(Kind.Unknown, "Мало проверок, чтобы судить о постоянстве выхода", n);
        var countries = recent.Select(x => x.Country.ToUpperInvariant()).Distinct().ToList();
        if (countries.Count > 1)
            return new Verdict(Kind.CountryChanges, $"Сервер менял страну выхода: {string.Join(", ", countries.Select(GeoLocation.CountryName))}", n);
        int ipChanges = recent.Zip(recent.Skip(1)).Count(p => p.First.Ip != p.Second.Ip);
        if (ipChanges >= Math.Max(2, n / 3))
            return new Verdict(Kind.Rotates, $"Адрес выхода меняется ({ipChanges} из {n - 1} раз), страна та же", n);
        return new Verdict(Kind.Stable, $"Выход стабилен по последним {n} проверкам", n);
    }

    /// <summary>Для выбора замены и запасного: стабильный выше, менявший страну ниже, остальные на равных.</summary>
    public static int Rank(DnsServer s, DateTime now) => Assess(s.ExitSeen, now).Kind switch
    {
        Kind.Stable => 1,
        Kind.CountryChanges => -1,
        _ => 0,
    };
}
