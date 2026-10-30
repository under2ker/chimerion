using System.Net;

namespace Dnex.Services;

/// <summary>Итог измерения скорости DNS-сервера. Отдельно от пинга (один запрос) и от доступа к нейросетям.</summary>
public sealed record SpeedResult(int Ok, int Total, long MedianMs, long SlowMs, double QueriesPerSecond)
{
    /// <summary>Оценка словами: быстрый, обычный, медленный или нестабильный (часть запросов не получила ответа).</summary>
    public string Grade =>
        Total == 0 || Ok == 0 ? "нет ответов"
        : Ok < Total * 0.75 ? "нестабильный"
        : MedianMs < 60 ? "быстрый"
        : MedianMs < 200 ? "обычный"
        : "медленный";

    public string Summary =>
        Ok == 0
            ? "ни один запрос не получил ответа"
            : $"{Grade}: медиана {MedianMs} мс, самые медленные {SlowMs} мс, {QueriesPerSecond:0.#} запросов в секунду, ответов {Ok} из {Total}";
}

/// <summary>
/// Измерение скорости DNS-сервера: несколько десятков запросов к разным популярным именам, по несколько одновременно.
/// Пинг показывает время одного ответа, а скорость показывает, как сервер справляется с потоком запросов, который
/// браузер создает при открытии обычной страницы. Имена нейросетей не используются: это измерение самого DNS.
/// </summary>
public static class SpeedTest
{
    /// <summary>Разные популярные имена: ответы большинства лежат в кэшах серверов, поэтому меряется скорость отклика, а не дальний обход.</summary>
    public static readonly string[] Hosts =
    {
        "wikipedia.org", "example.com", "microsoft.com", "apple.com", "github.com", "cloudflare.com", "mozilla.org", "python.org",
        "kernel.org", "debian.org", "ubuntu.com", "nginx.org", "openstreetmap.org", "archive.org", "w3.org", "gnu.org",
    };

    public const int Concurrency = 4;

    /// <param name="resolve">Один запрос к серверу: имя → (адреса, время мс). Подменяется в тестах.</param>
    public static async Task<SpeedResult> MeasureAsync(Func<string, CancellationToken, Task<(List<IPAddress> Ips, long Ms)>> resolve,
        CancellationToken ct, IProgress<int>? progress = null)
    {
        var times = new List<long>();
        int done = 0, ok = 0;
        var gate = new SemaphoreSlim(Concurrency);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Hosts.Select(async host =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var (ips, ms) = await resolve(host, ct).ConfigureAwait(false);
                if (ips.Count > 0) { lock (times) times.Add(ms); Interlocked.Increment(ref ok); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* запрос без ответа: считается в «ответов X из Y» */ }
            finally { gate.Release(); progress?.Report(Interlocked.Increment(ref done)); }
        })).ConfigureAwait(false);
        sw.Stop();
        times.Sort();
        if (times.Count == 0) return new SpeedResult(0, Hosts.Length, 0, 0, 0);
        long median = times[times.Count / 2];
        // Медленные: девяностый процентиль (при 16 запросах это второй с конца)
        long slow = times[Math.Min(times.Count - 1, (int)Math.Ceiling(times.Count * 0.9) - 1)];
        return new SpeedResult(ok, Hosts.Length, median, slow, ok / Math.Max(0.001, sw.Elapsed.TotalSeconds));
    }
}
