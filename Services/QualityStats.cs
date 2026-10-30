namespace Dnex.Services;

/// <summary>Итог по истории проверок сервера: сколько ответов, как быстро, когда сбои.</summary>
public sealed record QualitySummary(int Count, int OkCount, long? MedianMs, long? SlowMs, string? WorstPart, int WorstPercent, int Outages, DateTime? First)
{
    public int OkPercent => Count == 0 ? 0 : OkCount * 100 / Count;

    /// <summary>Выводы словами для заголовка графика.</summary>
    public string Text
    {
        get
        {
            if (Count < 3) return "Пока мало проверок: график появится, когда их наберется несколько";
            var parts = new List<string> { $"отвечал в {OkPercent}% проверок ({OkCount} из {Count})" };
            if (MedianMs is { } m) parts.Add($"обычный ответ {m} мс" + (SlowMs is { } s && s > m * 2 ? $", в плохие моменты до {s} мс" : ""));
            if (Outages > 0) parts.Add($"провалов подряд: {Outages}");
            var joined = string.Join(", ", parts);
            var text = char.ToUpper(joined[0]) + joined[1..] + ".";
            if (WorstPart != null) text += $" Чаще всего сбои {WorstPart}: {WorstPercent}% проверок не прошли.";
            return text;
        }
    }
}

/// <summary>Расчет по истории проверок, которую каждый сервер уже хранит (до 14 дней, до 200 точек).</summary>
public static class QualityStats
{
    public static readonly string[] Parts = { "ночью (0:00-6:00)", "утром (6:00-12:00)", "днем (12:00-18:00)", "вечером (18:00-24:00)" };

    public static int PartOf(DateTime t) => t.Hour / 6;

    public static QualitySummary Summarize(IReadOnlyList<Models.CheckPoint> points, DateTime now, int days = 14)
    {
        var list = points.Where(p => now - p.T <= TimeSpan.FromDays(days)).OrderBy(p => p.T).ToList();
        if (list.Count == 0) return new QualitySummary(0, 0, null, null, null, 0, 0, null);
        int ok = list.Count(p => p.Ok);
        var ms = list.Where(p => p.Ok && p.Ms is > 0).Select(p => p.Ms!.Value).OrderBy(x => x).ToList();
        long? median = ms.Count == 0 ? null : ms[ms.Count / 2];
        long? slow = ms.Count < 5 ? null : ms[Math.Min(ms.Count - 1, ms.Count * 9 / 10)];

        // Провал: две проверки подряд без ответа
        int outages = 0, run = 0;
        foreach (var p in list)
        {
            if (!p.Ok) { run++; if (run == 2) outages++; }
            else run = 0;
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
        return new QualitySummary(list.Count, ok, median, slow, worst, worstPct, outages, list[0].T);
    }
}
