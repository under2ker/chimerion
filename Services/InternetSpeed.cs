using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Dnex.Services;

/// <summary>Сервер замера: адрес файла для скачивания и адрес для отправки, если он ее принимает.</summary>
public sealed record SpeedSource(string Name, string Download, string? Upload);

/// <summary>Этап замера для шкал окна.</summary>
public enum SpeedPhase { Ping, Download, Upload }

public sealed record SpeedProgress(SpeedPhase Phase, double Mbit, double Fraction);

/// <summary>Итог замера. Null: этап не удался.</summary>
public sealed record InternetResult(string? Source, long? PingMs, long? JitterMs, double? DownMbit, double? UpMbit, string? Error = null);

/// <summary>
/// Замер скорости интернета. Основной сервер Cloudflare, запасные нужны, когда провайдер режет канал до него: короткая проба
/// выбирает первый сервер с нормальной скоростью, иначе самый быстрый из ответивших. Первые секунды разгона TCP не считаются.
/// </summary>
public static class InternetSpeed
{
    public static readonly SpeedSource[] Sources =
    {
        new("Cloudflare", "https://speed.cloudflare.com/__down?bytes=25000000", "https://speed.cloudflare.com/__up"), // больше 50 МБ за запрос Cloudflare не отдает (403)
        new("Selectel", "https://speedtest.selectel.ru/100MB", null),
        new("Hetzner", "https://fsn1-speed.hetzner.com/100MB.bin", null),
    };

    private const int Streams = 4, UploadStreams = 3, UploadBytes = 8 * 1024 * 1024;
    private const double GoodPilotMbit = 5;

    internal static HttpMessageHandler? Handler;
    internal static TimeSpan PilotTime = TimeSpan.FromSeconds(3.5), DownTime = TimeSpan.FromSeconds(8), UpTime = TimeSpan.FromSeconds(6), WarmUp = TimeSpan.FromSeconds(1.5);

    public static double Mbit(long bytes, double seconds) => seconds <= 0 ? 0 : bytes * 8 / 1_000_000.0 / seconds;

    /// <summary>Медиана времени ответа и джиттер: среднее отличие соседних замеров.</summary>
    public static (long Median, long Jitter) PingStats(IReadOnlyList<long> samples)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        long jitter = samples.Count < 2 ? 0 : (long)Math.Round(samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average());
        return (sorted[(sorted.Count - 1) / 2], jitter);
    }

    private static HttpClient NewClient() => new(Handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }, Handler == null) { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<InternetResult> RunAsync(IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        using var http = NewClient();
        var (source, error) = await PickAsync(http, ct).ConfigureAwait(false);
        if (source == null) return new InternetResult(null, null, null, null, null, error);
        var (ping, jitter) = await PingAsync(source, progress, ct).ConfigureAwait(false);
        var down = await TransferAsync(SpeedPhase.Download, DownTime, Streams, progress, (add, token) => DownloadOnce(http, source.Download, add, token), ct).ConfigureAwait(false);
        // Отправку принимает только основной сервер
        var upUrl = source.Upload ?? Sources.Select(s => s.Upload).First(u => u != null)!;
        var up = await TransferAsync(SpeedPhase.Upload, UpTime, UploadStreams, progress, (add, token) => UploadOnce(http, upUrl, add, token), ct).ConfigureAwait(false);
        return new InternetResult(source.Name, ping, jitter, down, up);
    }

    /// <summary>Короткая проба серверов по порядку.</summary>
    private static async Task<(SpeedSource?, string?)> PickAsync(HttpClient http, CancellationToken ct)
    {
        SpeedSource? best = null;
        double bestMbit = 0;
        string? error = null;
        foreach (var source in Sources)
        {
            ct.ThrowIfCancellationRequested();
            long got = 0;
            double firstByte = -1;
            var sw = Stopwatch.StartNew();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(PilotTime);
            // Скорость пробы считается с первого байта: у далеких серверов ответ приходит через пару секунд
            void Add(long n) { if (Interlocked.Add(ref got, n) == n) firstByte = sw.Elapsed.TotalSeconds; }
            try { await DownloadOnce(http, source.Download, Add, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception e) when (!ct.IsCancellationRequested) { error = $"{source.Name}: {e.GetBaseException().Message}"; continue; }
            double mbit = firstByte < 0 ? 0 : Mbit(Interlocked.Read(ref got), sw.Elapsed.TotalSeconds - firstByte);
            if (mbit >= GoodPilotMbit) return (source, null);
            if (mbit > bestMbit) { best = source; bestMbit = mbit; }
        }
        return (best, best == null ? error ?? "ни один сервер не передал данные" : null);
    }

    /// <summary>Одна проба задержки: время TCP-подключения к адресу, без DNS и без обработки на сервере.</summary>
    internal static Func<IPAddress, CancellationToken, Task<long>> Connect = async (ip, ct) =>
    {
        using var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        var sw = Stopwatch.StartNew();
        await socket.ConnectAsync(new IPEndPoint(ip, 443), ct).ConfigureAwait(false);
        return sw.ElapsedMilliseconds;
    };

    internal static Func<string, CancellationToken, Task<IPAddress[]>> Resolve = (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    /// <summary>Пинг до сервера замера: несколько TCP-подключений к уже найденному адресу, первое не считается.</summary>
    private static async Task<(long?, long?)> PingAsync(SpeedSource source, IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        const int count = 7;
        IPAddress? ip;
        try { ip = (await Resolve(new Uri(source.Download).Host, ct).ConfigureAwait(false)).OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault(); }
        catch (Exception) when (!ct.IsCancellationRequested) { ip = null; }
        if (ip == null) return (null, null);
        var samples = new List<long>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(3));
                long ms = await Connect(ip, limit.Token).ConfigureAwait(false);
                if (i > 0) samples.Add(ms);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            progress?.Report(new SpeedProgress(SpeedPhase.Ping, 0, (i + 1.0) / count));
        }
        if (samples.Count == 0) return (null, null);
        var (median, jitter) = PingStats(samples);
        return (median, jitter);
    }

    /// <summary>Несколько потоков повторяют передачу до конца времени. Скорость считается после разгона.</summary>
    private static async Task<double?> TransferAsync(SpeedPhase phase, TimeSpan time, int streams, IProgress<SpeedProgress>? progress,
        Func<Action<long>, CancellationToken, Task> once, CancellationToken ct)
    {
        long total = 0, atWarm = -1;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(time);
        var sw = Stopwatch.StartNew();
        double warmAt = 0;
        double Speed(double t, long bytes) => atWarm >= 0 && t > warmAt ? Mbit(bytes - atWarm, t - warmAt) : Mbit(bytes, t);
        var workers = Enumerable.Range(0, streams).Select(_ => Task.Run(async () =>
        {
            while (!limit.IsCancellationRequested)
            {
                try { await once(n => Interlocked.Add(ref total, n), limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception) when (!limit.IsCancellationRequested) { await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); }
            }
        })).ToList();
        var all = Task.WhenAll(workers);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(150, CancellationToken.None)).ConfigureAwait(false);
            double t = sw.Elapsed.TotalSeconds;
            long now = Interlocked.Read(ref total);
            if (atWarm < 0 && t >= WarmUp.TotalSeconds) { atWarm = now; warmAt = t; }
            progress?.Report(new SpeedProgress(phase, Speed(t, now), Math.Min(1, t / time.TotalSeconds)));
        }
        await all.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        long sent = Interlocked.Read(ref total);
        return sent == 0 ? null : Speed(sw.Elapsed.TotalSeconds, sent);
    }

    private static async Task DownloadOnce(HttpClient http, string url, Action<long> add, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return;
            add(n);
        }
    }

    private static async Task UploadOnce(HttpClient http, string url, Action<long> add, CancellationToken ct)
    {
        using var content = new CountingContent(UploadBytes, add);
        using var resp = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Тело отправки, считающее отданные байты по ходу передачи.</summary>
    private sealed class CountingContent(int size, Action<long> add) : HttpContent
    {
        private static readonly byte[] Chunk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64 * 1024);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (int sent = 0; sent < size; sent += Chunk.Length)
            {
                await stream.WriteAsync(Chunk, cancellationToken).ConfigureAwait(false);
                add(Chunk.Length);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length) { length = (size + Chunk.Length - 1) / Chunk.Length * (long)Chunk.Length; return true; }
    }
}
