using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Проверка сервера в три шага для каждого ИИ-сервиса:
/// 1) отвечает ли сервер и как быстро;
/// 2) подменяет ли он адрес сервиса (сравнение с эталонным резолвером);
/// 3) открывается ли API сервиса через полученный IP без ошибки «регион не поддерживается».
/// </summary>
public class ServerChecker
{
    private enum Probe { Ok, Blocked, Error }

    private readonly AppSettings _settings;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, Task<HashSet<IPAddress>>> _reference = new();

    /// <summary>Контекст UI-потока: свойства серверов меняем только в нем,
    /// чтобы живая сортировка/фильтр WPF не получали события из фоновых потоков.</summary>
    private readonly SynchronizationContext? _ui;

    /// <summary>ProxyIp — адрес, который сервер выдал вместо настоящего (Smart DNS): это и есть прокси сервиса.</summary>
    private sealed record TargetResult(string Part, bool Ok, long? Ms, bool Compared, bool Proxied, string? Error, string? ProxyIp = null);

    public ServerChecker(AppSettings settings)
    {
        _settings = settings;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 2, 30));
        _ui = SynchronizationContext.Current;
    }

    private Task OnUi(Action a)
    {
        if (_ui == null || SynchronizationContext.Current == _ui) { a(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ => { try { a(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }, null);
        return tcs.Task;
    }

    public async Task CheckManyAsync(IReadOnlyList<DnsServer> servers, IProgress<int>? progress, CancellationToken ct)
    {
        // Эталонные адреса получаем один раз до старта, а не в каждом потоке.
        await Task.WhenAll(_settings.Targets.Where(t => t.Enabled)
            .Select(t => GetReferenceAsync(t.Host).ContinueWith(_ => { }, TaskScheduler.Default)));

        int done = 0;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_settings.Parallelism, 1, 64), CancellationToken = ct };
        await Parallel.ForEachAsync(servers, opts, async (s, c) =>
        {
            try { await CheckAsync(s, c).ConfigureAwait(false); }
            finally { progress?.Report(Interlocked.Increment(ref done)); }
        });
    }

    public async Task CheckAsync(DnsServer s, CancellationToken ct)
    {
        await OnUi(() => s.Status = ServerStatus.Checking);
        var targets = _settings.Targets.Where(t => t.Enabled).ToList();

        TargetResult[] results;
        try
        {
            // Все сервисы проверяются параллельно — время проверки сервера = самый медленный сервис, а не сумма.
            results = await Task.WhenAll(targets.Select(t => CheckTargetAsync(s, t, ct))).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await OnUi(() => s.Status = ServerStatus.Unknown);
            throw;
        }

        int ok = results.Count(r => r.Ok);
        long? best = results.Where(r => r.Ms.HasValue).Select(r => r.Ms).Min();
        long? median = best, jitter = null;

        // Пинг: еще два замера, медиана и разброс.
        if (best is not null && ok > 0)
        {
            var samples = new List<long> { best.Value };
            for (int i = 0; i < 2; i++)
            {
                try { samples.Add((await ResolveViaAsync(s, targets[0].Host, ct).ConfigureAwait(false)).Ms); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            samples.Sort();
            median = samples[samples.Count / 2];
            jitter = samples.Count > 1 ? samples[^1] - samples[0] : null;
        }

        bool noProxy = results.Any(r => r.Compared) && !results.Any(r => r.Compared && r.Proxied);
        var status = targets.Count == 0 || ok == 0 ? ServerStatus.Fail
                   : ok == targets.Count ? ServerStatus.Ok : ServerStatus.Partial;
        var error = results.Select(r => r.Error).FirstOrDefault(e => e != null);
        if (noProxy && status != ServerStatus.Fail)
        {
            status = ServerStatus.Fail;
            error = "Не подменяет адреса ИИ-сервисов, обычный DNS";
        }

        // Адрес прокси Smart DNS: если новый — сбрасываем его страну, чтобы определить заново
        var proxyIp = results.Select(r => r.ProxyIp).FirstOrDefault(x => x != null);

        await OnUi(() =>
        {
            if (proxyIp != null && proxyIp != s.ProxyIp) s.SetProxy(proxyIp);
            s.LatencyMs = median;
            s.JitterMs = jitter;
            s.NoProxy = noProxy;
            s.Summary = string.Join(" · ", results.Select(r => r.Part));
            s.LastError = error;
            s.LastChecked = DateTime.Now;
            s.Status = status;
            s.ChecksTotal++;
            if (status != ServerStatus.Fail) { s.ChecksOk++; s.FailStreak = 0; } else s.FailStreak++;
            s.NotifyStability();
        });
    }

    private async Task<TargetResult> CheckTargetAsync(DnsServer s, CheckTarget t, CancellationToken ct)
    {
        List<IPAddress> ips;
        long ms;
        try
        {
            (ips, ms) = await ResolveViaAsync(s, t.Host, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new($"{t.Name}: нет DNS", false, null, false, false, e.Message);
        }
        if (ips.Count == 0) return new($"{t.Name}: пустой ответ", false, ms, false, false, $"{t.Name}: пустой ответ");

        bool compared = false, proxied = false;
        try
        {
            var refIps = await GetReferenceAsync(t.Host).ConfigureAwait(false);
            compared = true;
            proxied = !ips.Any(refIps.Contains);
        }
        catch { /* эталон недоступен — подмену не оцениваем */ }

        // Подмены нет — запрос пойдет на настоящий сервер, HTTPS-проверку можно пропустить.
        if (compared && !proxied)
            return new($"{t.Name} ✗ (без подмены)", false, ms, true, false, null);

        var (state, note) = await ProbeHttpsAsync(ips[0], t, ct).ConfigureAwait(false);
        var mark = state switch { Probe.Ok => "✓", Probe.Blocked => "✗ регион", _ => "✗" };
        return new($"{t.Name} {mark}", state == Probe.Ok, ms, compared, proxied,
                   state == Probe.Ok ? null : $"{t.Name}: {note}", proxied ? ips[0].ToString() : null);
    }

    /// <summary>Только пинг, без проверки сервисов: три DNS-запроса, медиана и разброс. Null, если сервер не ответил ни разу.</summary>
    public async Task<(long Median, long? Jitter)?> MeasurePingAsync(DnsServer s, CancellationToken ct)
    {
        var host = _settings.Targets.FirstOrDefault(t => t.Enabled)?.Host ?? "api.openai.com";
        var samples = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            try { samples.Add((await ResolveViaAsync(s, host, ct).ConfigureAwait(false)).Ms); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        if (samples.Count == 0) return null;
        samples.Sort();
        return (samples[samples.Count / 2], samples.Count > 1 ? samples[^1] - samples[0] : null);
    }

    public async Task<(List<IPAddress> Ips, long Ms)> ResolveViaAsync(DnsServer s, string host, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(s.DohUrl))
            return await DnsWire.ResolveDohAsync(s.DohUrl, host, _timeout, ct);

        if (!IPAddress.TryParse(s.Primary, out var primary))
            throw new InvalidOperationException("Нет корректного IP и DoH-адреса");

        try
        {
            return await DnsWire.ResolveUdpAsync(primary, host, _timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!ct.IsCancellationRequested)
        {
            // UDP-пакет мог потеряться: один быстрый повтор.
            try { return await DnsWire.ResolveUdpAsync(primary, host, _timeout / 2, ct).ConfigureAwait(false); }
            catch when (IPAddress.TryParse(s.Secondary, out _) && !ct.IsCancellationRequested)
            {
                return await DnsWire.ResolveUdpAsync(IPAddress.Parse(s.Secondary!), host, _timeout, ct).ConfigureAwait(false);
            }
        }
        catch when (IPAddress.TryParse(s.Secondary, out _) && !ct.IsCancellationRequested)
        {
            return await DnsWire.ResolveUdpAsync(IPAddress.Parse(s.Secondary!), host, _timeout, ct);
        }
    }

    /// <summary>Эталонные IP хоста. Кэш общий на проверку, но не привязан к отмене одного прогона:
    /// неудачную попытку удаляем из кэша, и следующая повторит ее, а не будет отравлена.</summary>
    private Task<HashSet<IPAddress>> GetReferenceAsync(string host)
    {
        var task = _reference.GetOrAdd(host, h => ResolveReferenceAsync(h, CancellationToken.None));
        if (task.IsFaulted || task.IsCanceled) _reference.TryRemove(new KeyValuePair<string, Task<HashSet<IPAddress>>>(host, task));
        return task;
    }

    private async Task<HashSet<IPAddress>> ResolveReferenceAsync(string host, CancellationToken ct)
    {
        try
        {
            return (await DnsWire.ResolveDohAsync(_settings.ReferenceDoh, host, _timeout, ct)).Ips.ToHashSet();
        }
        catch when (!ct.IsCancellationRequested)
        {
            return (await DnsWire.ResolveDohAsync(_settings.ReferenceDohFallback, host, _timeout, ct)).Ips.ToHashSet();
        }
    }

    /// <summary>HTTPS-запрос к сервису через конкретный IP с правильным SNI и проверкой сертификата.</summary>
    private async Task<(Probe, string)> ProbeHttpsAsync(IPAddress ip, CheckTarget t, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = _timeout,
            UseCookies = false,
            // Сертификат проверяется штатно (имя хоста = SNI). Прокси с поддельным сертификатом не пройдет.
            SslOptions = { CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck },
            ConnectCallback = async (_, c) =>
            {
                var sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await sock.ConnectAsync(new IPEndPoint(ip, 443), c);
                    return new NetworkStream(sock, ownsSocket: true);
                }
                catch { sock.Dispose(); throw; }
            },
        };
        using var http = new HttpClient(handler) { Timeout = _timeout + _timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DNEX/0.1");

        try
        {
            using var resp = await http.GetAsync($"https://{t.Host}{t.Path}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Читаем не больше 64 КБ: для поиска маркера блокировки этого достаточно, а чужой прокси не забьет память.
            var buf = new byte[64 * 1024];
            int len = 0, n;
            await using (var st = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                while (len < buf.Length && (n = await st.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false)) > 0) len += n;
            var body = System.Text.Encoding.UTF8.GetString(buf, 0, len);
            int code = (int)resp.StatusCode;
            return ServiceAccess.Classify(t, code, body) switch
            {
                ServiceAccess.Result.Open => (Probe.Ok, $"HTTP {code}"),
                ServiceAccess.Result.Closed => (Probe.Blocked, $"HTTP {code}, доступ закрыт"),
                _ => (Probe.Error, $"HTTP {code}, сервис не отвечает нормально"),
            };
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return (Probe.Error, e.GetBaseException().Message);
        }
    }
}
