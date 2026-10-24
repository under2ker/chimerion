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

    // Один запрос на адрес прокси за всю проверку: у многих серверов один прокси, а частые одинаковые запросы
    // с одного адреса Google считает подозрительными и начинает показывать капчу (так и было в прогонах).
    // Ключ — сайт, путь и адрес прокси; результат общий для всех серверов с этим прокси.
    private readonly ConcurrentDictionary<string, Task<(Probe, string)>> _siteCache = new();
    private readonly ConcurrentDictionary<string, Task<Egress?>> _traceCache = new();

    /// <summary>Контекст UI-потока: свойства серверов меняем только в нем,
    /// чтобы живая сортировка/фильтр WPF не получали события из фоновых потоков.</summary>
    private readonly SynchronizationContext? _ui;

    /// <summary>ProxyIp — адрес, который сервер выдал вместо настоящего (Smart DNS): это и есть прокси сервиса.</summary>
    private sealed record TargetResult(string Part, bool Ok, long? Ms, bool Compared, bool Proxied, string? Error, string? ProxyIp = null);

    private readonly CheckOptions _opt;

    public ServerChecker(AppSettings settings, CheckOptions? options = null)
    {
        _settings = settings;
        _opt = options ?? CheckOptions.Full;
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
        var all = _settings.Targets.Where(t => t.Enabled).ToList();
        // Проверка одного сервиса: остальные сохраняют прежний статус, стабильность от такой проверки не меняется
        var targets = _opt.Only == null ? all : all.Where(t => _opt.Only.Contains(t.Name)).ToList();
        bool subset = targets.Count < all.Count;
        if (targets.Count == 0) { await OnUi(() => s.Status = ServerStatus.Unknown); return; }

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
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    try { samples.Add((await ResolveViaAsync(s, targets[0].Host, ct).ConfigureAwait(false)).Ms); }
                    catch (Exception) when (!ct.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException)
            {
                // «Стоп» во время замеров: строка не должна остаться в состоянии «Проверка…»
                await OnUi(() => s.Status = ServerStatus.Unknown);
                throw;
            }
            samples.Sort();
            median = samples[samples.Count / 2];
            jitter = samples.Count > 1 ? samples[^1] - samples[0] : null;
        }

        bool noProxy = results.Any(r => r.Compared) && !results.Any(r => r.Compared && r.Proxied);
        // Итог по всем сервисам: проверенные сейчас и, при частичной проверке, прежние у остальных
        var prevChips = s.Chips.ToDictionary(c => c.Name, c => c.State);
        var parts = all.Select(t =>
        {
            int i = targets.IndexOf(t);
            if (i >= 0) return results[i].Part;
            return prevChips.GetValueOrDefault(t.Name) switch { "ok" => $"{t.Name} ✓", "region" => $"{t.Name} ✗ регион", _ => $"{t.Name} ✗" };
        }).ToList();
        int okAll = parts.Count(p => p.Contains('✓'));
        var status = okAll == 0 ? ServerStatus.Fail : okAll == all.Count ? ServerStatus.Ok : ServerStatus.Partial;
        var error = results.Select(r => r.Error).FirstOrDefault(e => e != null);
        if (noProxy && status != ServerStatus.Fail)
        {
            status = ServerStatus.Fail;
            error = "Не подменяет адреса ИИ-сервисов, обычный DNS";
        }

        // Адрес прокси Smart DNS: если новый — сбрасываем его страну, чтобы определить заново
        var proxyIp = results.Select(r => r.ProxyIp).FirstOrDefault(x => x != null);

        // Настоящий выход для каждого ИИ: адрес и страна, которые видит сервис. Только у рабочих сервисов с подменой,
        // все три параллельно. На статусы не влияет: не определили — просто нет данных о выходе
        var egressBy = new Dictionary<string, Egress>();
        if (status != ServerStatus.Fail)
        {
            var jobs = targets.Select((t, i) => (t, i))
                .Where(x => results[x.i].Ok && results[x.i].Proxied)
                .Select(async x => (x.t.Name, E: await TraceServiceAsync(s, x.t, ct).ConfigureAwait(false)))
                .ToList();
            foreach (var (name, e) in await Task.WhenAll(jobs).ConfigureAwait(false))
                if (e != null) egressBy[name] = e;
        }

        await OnUi(() =>
        {
            if (proxyIp != null && proxyIp != s.ProxyIp) s.SetProxy(proxyIp);
            // Выход проверенных сервисов: новый записываем, у неработающих сейчас — убираем устаревший
            foreach (var t in targets)
                if (egressBy.TryGetValue(t.Name, out var e)) s.EgressBy[t.Name] = e; else s.EgressBy.Remove(t.Name);
            s.NotifyEgress();
            s.LatencyMs = median;
            s.JitterMs = jitter;
            s.NoProxy = noProxy;
            s.Summary = string.Join(" · ", parts);
            s.LastError = error;
            s.LastChecked = DateTime.Now;
            bool appeared = status is ServerStatus.Ok or ServerStatus.Partial && s.Status is not (ServerStatus.Ok or ServerStatus.Partial);
            s.Status = status;
            if (appeared) MarkAppeared(s);
            if (subset) return; // частичная проверка не считается в стабильность
            s.ChecksTotal++;
            if (status != ServerStatus.Fail) { s.ChecksOk++; s.FailStreak = 0; } else s.FailStreak++;
            // Стабильность по каждому ИИ отдельно: для списка «лучший для ChatGPT, Claude, Gemini, для всех»
            s.ServiceChecks++;
            for (int i = 0; i < targets.Count; i++)
                if (results[i].Ok) s.ServiceOk[targets[i].Name] = s.ServiceOk.GetValueOrDefault(targets[i].Name) + 1;
            if (targets.Count > 0 && ok == targets.Count) s.AllOk++;
            s.NotifyStability();
        });
    }

    /// <summary>Сервер впервые стал рабочим: его строка появляется с анимацией, флаг снимается через секунду.</summary>
    private async void MarkAppeared(DnsServer s)
    {
        s.JustAdded = true;
        await Task.Delay(1200).ConfigureAwait(false);
        await OnUi(() => s.JustAdded = false);
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
            // Адрес не совпал с эталоном, но это сеть самого сервиса (у Google много адресов): подмены нет
            if (proxied && await GeoLocation.IsOwnedByAsync(ips[0].ToString(), t.OwnerOrgs, ct).ConfigureAwait(false))
                proxied = false;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* эталон недоступен — подмену не оцениваем */ }

        // Подмены нет — запрос пойдет на настоящий сервер, HTTPS-проверку можно пропустить.
        if (compared && !proxied)
            return new($"{t.Name} ✗ (без подмены)", false, ms, true, false, null);

        // Сайт сервиса и (в глубокой проверке) его дополнительные адреса проверяются одновременно с API, а не после:
        // время проверки сервера — самый медленный из запросов, а не их сумма. Если API закрыт, их результат не нужен,
        // и они останавливаются. Решение то же: сервис засчитан, только если API открыт и ни один адрес не закрыт.
        using var side = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var web = SplitWebCheck(t);
        var webTask = web == null ? null : ProbeSiteAsync(s, web, side.Token, ct);
        var extra = _opt.Deep
            ? t.ExtraHosts.Select(h => (Host: h, Task: ProbeSiteAsync(s, new CheckTarget { Name = t.Name, Host = h, Path = "/" }, side.Token, ct))).ToList()
            : new();

        var (state, note) = await ProbeHttpsAsync(ips[0], t, ct).ConfigureAwait(false);
        if (state == Probe.Ok && webTask != null)
        {
            var (webState, webNote) = await webTask.ConfigureAwait(false);
            if (webState == Probe.Blocked) (state, note) = (Probe.Blocked, $"сайт {web!.Host}: {webNote}");
        }
        if (state == Probe.Ok)
        {
            foreach (var (host, task) in extra)
            {
                var (hs, hn) = await task.ConfigureAwait(false);
                if (hs == Probe.Blocked) { (state, note) = (Probe.Blocked, $"адрес {host}: {hn}"); break; }
            }
        }
        side.Cancel(); // оставшиеся проверки адресов больше не нужны

        // Капча Google — не региональная блокировка: в браузере ее иногда можно пройти. Отдельная пометка
        var mark = state switch { Probe.Ok => "✓", Probe.Blocked when note.Contains("капч") => "✗ капча", Probe.Blocked => "✗ регион", _ => "✗" };
        return new($"{t.Name} {mark}", state == Probe.Ok, ms, compared, proxied,
                   state == Probe.Ok ? null : $"{t.Name}: {note}", proxied ? ips[0].ToString() : null);
    }

    /// <summary>
    /// Режим проверки. Only — только эти сервисы (остальные сохраняют прежний статус). Deep — глубокая:
    /// кроме API и сайта проверяются все дополнительные адреса сервиса, а проверка идет в несколько проходов.
    /// </summary>
    public sealed record CheckOptions(IReadOnlyCollection<string>? Only = null, bool Deep = false)
    {
        public static readonly CheckOptions Full = new();
        public int Rounds => Deep ? 3 : 1;
    }

    /// <summary>Сайт для второй проверки из настройки WebCheck («chatgpt.com/» → хост и путь). Null, если не задан.</summary>
    internal static CheckTarget? SplitWebCheck(CheckTarget t)
    {
        if (string.IsNullOrWhiteSpace(t.WebCheck)) return null;
        var v = t.WebCheck.Trim();
        int slash = v.IndexOf('/');
        var host = slash < 0 ? v : v[..slash];
        var path = slash < 0 ? "/" : v[slash..];
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) return null;
        return new CheckTarget { Name = t.Name, Host = host, Path = path };
    }

    /// <summary>Выход сервиса через этот сервер: адрес его прокси для сайта сервиса и trace Cloudflare через этот адрес.</summary>
    private async Task<Egress?> TraceServiceAsync(DnsServer s, CheckTarget t, CancellationToken ct)
    {
        try
        {
            var (ips, _) = await ResolveViaAsync(s, EgressTrace.SiteFor(t), ct).ConfigureAwait(false);
            if (ips.Count == 0) return null;
            var host = EgressTrace.TraceHostFor(t.Name);
            // Один trace на адрес прокси за проверку (см. _traceCache)
            return await _traceCache.GetOrAdd($"{host}|{ips[0]}", _ => EgressTrace.TraceAsync(ips[0], _timeout, ct, host))
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Сайт или адрес сервиса через этот сервер. Ошибка и отмена — «неясно» (Error), а не «закрыто»:
    /// не ответивший адрес не должен ни закрывать сервис, ни считаться открытым.</summary>
    /// <summary>
    /// Запрос к сайту общий для всех серверов с тем же прокси (см. _siteCache). Он идет с токеном всей проверки
    /// (batchCt): отмена ненужной проверки у одного сервера (ct) не должна обрывать общий запрос для остальных.
    /// </summary>
    private async Task<(Probe, string)> ProbeSiteAsync(DnsServer s, CheckTarget site, CancellationToken ct, CancellationToken batchCt)
    {
        try
        {
            var (ips, _) = await ResolveViaAsync(s, site.Host, ct).ConfigureAwait(false);
            if (ips.Count == 0) return (Probe.Error, "пустой ответ DNS");
            var ip = ips[0];
            return await _siteCache.GetOrAdd($"{site.Host}{site.Path}|{ip}", _ => ProbeHttpsAsync(ip, site, batchCt, isWeb: true))
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) { return (Probe.Error, e.GetBaseException().Message); }
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
    private async Task<(Probe, string)> ProbeHttpsAsync(IPAddress ip, CheckTarget t, CancellationToken ct, bool isWeb = false)
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
            var verdict = isWeb ? ServiceAccess.ClassifyWeb(t, code, body, resp.Headers.Location?.ToString())
                                : ServiceAccess.Classify(t, code, body);
            return verdict switch
            {
                ServiceAccess.Result.Open => (Probe.Ok, $"HTTP {code}"),
                ServiceAccess.Result.Closed when isWeb && (resp.Headers.Location?.ToString().Contains("google.com/sorry") ?? false)
                    => (Probe.Blocked, "Google показывает капчу на адресе прокси (в браузере ее иногда можно пройти)"),
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
