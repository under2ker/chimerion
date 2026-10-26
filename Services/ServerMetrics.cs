using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Раздельные показатели сервера вместо одной оценки. Одно число прячет разные отказы: сервер может отвечать на
/// DNS, но не открывать ни один сервис; работать с ChatGPT, но не с Claude; быть быстрым, но проверенным один раз
/// неделю назад. Поэтому выбор сервера смотрит на показатели по отдельности, а оценка 0–100 остается только
/// для сортировки таблицы.
/// </summary>
public sealed record ServerMetrics(
    int? Availability,      // % проверок за 7 дней, в которых сервер ответил на DNS-запрос
    int CompatibleNow,      // сколько сервисов работает через сервер по последней проверке
    int ServicesTotal,      // сколько сервисов проверялось
    int? Stability,         // % полных проверок, когда работал хотя бы один сервис
    long? LatencyMs,
    long? JitterMs,
    TimeSpan? Age,          // сколько прошло с последней проверки
    double Confidence)      // 0..1: сколько проверок и насколько они согласованы
{
    /// <summary>Проверен не дольше часа назад: результат можно считать текущим.</summary>
    public bool Fresh => Age is { } a && a <= TimeSpan.FromHours(1);

    /// <summary>Проверен больше суток назад: сеть могла измениться, результат устарел.</summary>
    public bool Stale => Age is not { } a || a > TimeSpan.FromDays(1);

    public string ConfidenceLevel => Confidence >= 0.75 ? "высокая" : Confidence >= 0.4 ? "средняя" : "низкая";

    public static ServerMetrics Of(DnsServer s, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var week = s.History.Where(p => t - p.T <= TimeSpan.FromDays(7)).ToList();
        // Пинг в истории есть, только если сервер ответил на запрос: так отличаем «не отвечает» от «отвечает, но сервисы закрыты»
        int? availability = week.Count > 0 ? week.Count(p => p.Ms != null) * 100 / week.Count
                          : s.LastChecked == null ? null : s.LatencyMs != null ? 100 : 0;
        var chips = s.Chips;
        int? stability = s.ChecksTotal == 0 ? null : s.ChecksOk * 100 / s.ChecksTotal;
        return new ServerMetrics(availability, chips.Count(c => c.State == "ok"), chips.Count, stability,
            s.LatencyMs, s.JitterMs, s.LastChecked is { } lc ? t - lc : null, ConfidenceOf(s));
    }

    /// <summary>
    /// Уверенность: число проверок (10 и больше — полное доверие) с поправкой на согласованность. Сервер, который
    /// через раз то работает, то нет, при той же доле успехов заслуживает меньше доверия, чем стабильно работающий.
    /// </summary>
    public static double ConfidenceOf(DnsServer s)
    {
        if (s.ChecksTotal == 0) return 0;
        double amount = Math.Min(1, s.ChecksTotal / 10.0);
        var h = s.History.TakeLast(20).ToList();
        double flips = h.Count < 2 ? 0 : h.Zip(h.Skip(1)).Count(x => x.First.Ok != x.Second.Ok) / (double)(h.Count - 1);
        return Math.Round(amount * (1 - flips), 2);
    }

    /// <summary>Расшифровка для подсказки к оценке: каждый показатель отдельной строкой.</summary>
    public string Describe()
    {
        string Age2(TimeSpan a) => a.TotalMinutes < 1 ? "только что"
            : a.TotalHours < 1 ? $"{(int)a.TotalMinutes} мин назад"
            : a.TotalDays < 1 ? $"{(int)a.TotalHours} ч назад"
            : $"{(int)a.TotalDays} дн назад";
        var lines = new List<string>
        {
            "Доступность: " + (Availability is { } av ? $"{av}% проверок за 7 дней с ответом на DNS" : "нет данных"),
            "Совместимость: " + (ServicesTotal == 0 ? "нет данных" : $"работает {CompatibleNow} из {ServicesTotal} сервисов"),
            "Стабильность: " + (Stability is { } st ? $"{st}% проверок успешны" : "нет данных"),
            "Задержка: " + (LatencyMs is { } ms ? (JitterMs is { } j ? $"{ms} мс, разброс {j} мс" : $"{ms} мс") : "нет данных"),
            "Свежесть: " + (Age is { } a ? "проверен " + Age2(a) + (Stale ? ", результат мог устареть" : "") : "не проверялся"),
            $"Уверенность: {ConfidenceLevel}",
        };
        return string.Join("\n", lines);
    }
}
