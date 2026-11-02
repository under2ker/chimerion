namespace Dnex.Services;

/// <summary>Итог по истории проверок сервера. Медиана и P95 только по успешным проверкам.</summary>
public sealed record QualitySummary(int Count, int OkCount, long? MedianMs, long? P95Ms, string? WorstPart, int WorstPercent, int CurrentRun, int MaxRun, DateTime? First, DateTime? Last)
{
    public int Failed => Count - OkCount;

    /// <summary>Доля успешных проверок, проценты.</summary>
    public double OkRate => Count == 0 ? 0 : OkCount * 100.0 / Count;

    /// <summary>Заметные всплески: P95 выше медианы в полтора раза и больше чем на 30 мс. Иначе разница ни о чем не говорит.</summary>
    public bool HasSpikes => MedianMs is { } m && P95Ms is { } p && p >= m * 1.5 && p >= m + 30;
}

/// <summary>Один столбец графика: проверки внутри интервала времени.</summary>
public sealed record QualityBucket(DateTime Start, DateTime End, long? MedianMs, long? MinMs, long? MaxMs, int Count, int Failed, IReadOnlyList<DateTime> FailTimes)
{
    public int FailPercent => Count == 0 ? 0 : Failed * 100 / Count;
}

/// <summary>Расчет по истории проверок, которую каждый сервер уже хранит (до 14 дней, до 336 точек).</summary>
public static class QualityStats
{
    public static readonly string[] Parts = { "ночью (0:00-6:00)", "утром (6:00-12:00)", "днем (12:00-18:00)", "вечером (18:00-24:00)" };

    public static int PartOf(DateTime t) => t.Hour / 6;

    /// <summary>Доступность с одним знаком. 100 только если сбоев не было: 99,96% не округляется до идеала.</summary>
    public static double RoundRate(QualitySummary s)
    {
        var rate = Math.Round(s.OkRate, 1, MidpointRounding.AwayFromZero);
        return s.Failed > 0 && rate >= 100 ? 99.9 : rate;
    }

    /// <summary>Оценка доступности: Ok от 99%, Warn от 95%, ниже Bad.</summary>
    public static string Grade(double rate) => rate >= 99 ? "Ok" : rate >= 95 ? "Warn" : "Bad";

    /// <summary>Ширина столбца графика под выбранный период: 24 часа, 7 или 14 дней.</summary>
    public static int BucketHours(int days) => days <= 1 ? 1 : days <= 7 ? 6 : 12;

    /// <summary>Верхняя граница шкалы: ближайшее сверху круглое число вида 1, 2, 5, умноженное на степень десяти.</summary>
    public static long NiceTop(long value)
    {
        value = Math.Max(10, value);
        for (long step = 10; ; step *= 10)
            foreach (var k in new long[] { 1, 2, 5 })
                if (step * k >= value) return step * k;
    }

    /// <summary>Значение по рангу (ближайший ранг) в отсортированном списке.</summary>
    private static long Rank(List<long> sorted, double share) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Count * share) - 1)];

    public static QualitySummary Summarize(IReadOnlyList<Models.CheckPoint> points, DateTime now, int days = 14)
    {
        var list = points.Where(p => now - p.T <= TimeSpan.FromDays(days)).OrderBy(p => p.T).ToList();
        if (list.Count == 0) return new QualitySummary(0, 0, null, null, null, 0, 0, 0, null, null);
        int ok = list.Count(p => p.Ok);
        var ms = list.Where(p => p.Ok && p.Ms is > 0).Select(p => p.Ms!.Value).OrderBy(x => x).ToList();
        long? median = ms.Count == 0 ? null : Rank(ms, 0.5);
        long? p95 = ms.Count < 5 ? null : Rank(ms, 0.95);

        // Серии сбоев: самая длинная за период и идущая прямо сейчас
        int run = 0, maxRun = 0;
        foreach (var p in list)
        {
            run = p.Ok ? 0 : run + 1;
            maxRun = Math.Max(maxRun, run);
        }

        // Время суток с заметно худшим результатом: от трех проверок, от 20% сбоев и хуже среднего на 15 пунктов
        int overall = (list.Count - ok) * 100 / list.Count;
        string? worst = null; int worstPct = 0;
        for (int part = 0; part < 4; part++)
        {
            var inPart = list.Where(p => PartOf(p.T) == part).ToList();
            if (inPart.Count < 3) continue;
            int pct = inPart.Count(p => !p.Ok) * 100 / inPart.Count;
            if (pct >= 20 && pct >= overall + 15 && pct > worstPct) { worst = Parts[part]; worstPct = pct; }
        }
        return new QualitySummary(list.Count, ok, median, p95, worst, worstPct, run, maxRun, list[0].T, list[^1].T);
    }

    /// <summary>Проверки по интервалам, выровненным по часам суток. from: начало оси, без него первая проверка.</summary>
    public static List<QualityBucket> Buckets(IReadOnlyList<Models.CheckPoint> points, DateTime now, int hours, DateTime? from = null)
    {
        var list = points.Where(p => p.T <= now && now - p.T <= TimeSpan.FromDays(14) && (from == null || p.T >= from)).OrderBy(p => p.T).ToList();
        var result = new List<QualityBucket>();
        if (list.Count == 0 && from == null) return result;
        var first = from ?? list[0].T;
        var origin = first.Date.AddHours(first.Hour / hours * hours);
        for (var start = origin; start < now; start = start.AddHours(hours))
        {
            var end = start.AddHours(hours);
            var inside = list.Where(p => p.T >= start && p.T < end).ToList();
            var ok = inside.Where(p => p.Ok && p.Ms is > 0).Select(p => p.Ms!.Value).OrderBy(x => x).ToList();
            result.Add(new QualityBucket(start, end,
                ok.Count == 0 ? null : Rank(ok, 0.5),
                ok.Count == 0 ? null : ok[0],
                ok.Count == 0 ? null : ok[^1],
                inside.Count, inside.Count(p => !p.Ok), inside.Where(p => !p.Ok).Select(p => p.T).ToList()));
        }
        return result;
    }

    /// <summary>Участки линии: подряд идущие столбцы с измерениями. Столбец без измерений рвет линию, значения не придумываются.</summary>
    public static List<List<int>> Segments(IReadOnlyList<QualityBucket> buckets)
    {
        var result = new List<List<int>>();
        List<int>? current = null;
        for (int i = 0; i < buckets.Count; i++)
        {
            if (buckets[i].MedianMs is not > 0) { current = null; continue; }
            if (current == null) result.Add(current = new List<int>());
            current.Add(i);
        }
        return result;
    }
}
