using System.Threading.Channels;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Глубокий парсер: стартует со списка источников и результатов поиска, обходит ссылки по
/// ключевым словам на заданную глубину, листает Telegram-каналы и собирает IP / DoH-адреса.
/// У каждого найденного сервера запоминается точная страница-источник.
/// </summary>
public static partial class SourceParser
{
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b", RegexOptions.None, 2000)]
    private static partial Regex Ipv4Rx();

    [GeneratedRegex(@"https://[^\s""'<>]+?/dns-query[^\s""'<>]*", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex DohRx();

    /// <summary>Имена DNS-хостов без /dns-query: dns.example.ru, dot.dns.x.ru, xbox-dns.ru, router.comss.one…</summary>
    [GeneratedRegex(@"(?<![\w.@/-])((?:(?:dns\d*|dot|doh|router|free|pro|secure)\.)(?:[a-z0-9-]+\.)*[a-z0-9-]+\.[a-z]{2,}|(?:[a-z0-9-]+\.)*[a-z0-9]+-dns\.[a-z]{2,})(?![\w-])", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex DnsHostRx();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|td|h\d|pre|code)>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BlockTagRx();

    [GeneratedRegex(@"<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex ScriptRx();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, 2000)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"<a\s[^>]*?href\s*=\s*[""']([^""'#]+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex LinkRx();

    private static readonly string[] DnsContextWords =
    {
        "dns", "днс", "doh", "сервер", "server", "primary", "secondary", "основн", "резерв",
        "предпочит", "альтернатив", "nameserver", "адрес",
    };

    private static readonly string[] BinaryExt =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".pdf", ".zip", ".rar", ".7z",
        ".exe", ".msi", ".apk", ".mp4", ".mp3", ".webm", ".woff", ".woff2", ".ttf", ".css", ".js",
    };

    private const int MaxPageBytes = 3 * 1024 * 1024;

    /// <summary>Состояние обхода для индикатора прогресса.</summary>
    public sealed record CrawlProgress(int Pages, int MaxPages, int Queued, int Found, int Errors, string Current);

    /// <summary>Не больше двух одновременных запросов к одному сайту — чтобы форумы не банили.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> HostGates = new();

    private sealed record Job(Uri Url, int Depth, string SourceName, string Type, int TgPage = 0);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false, // переадресации обрабатываем сами: проверяем адрес и не отдаем Cookie чужому сайту
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            // Адрес проверяем в момент соединения, а не один раз заранее: иначе DNS rebinding
            // подменит адрес между проверкой и подключением. Публичны только IPv4-адреса.
            ConnectCallback = async (ctx, ct) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var addrs = IPAddress.TryParse(host, out var lit) ? new[] { lit } : await Dns.GetHostAddressesAsync(host, ct);
                var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsPublic(a.ToString()))
                         ?? throw new HttpRequestException($"адрес {host} не публичный, запрос отменен");
                var sock = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await sock.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                    return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
                }
                catch { sock.Dispose(); throw; }
            },
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
        return h;
    }

    public static async Task<List<DnsServer>> FetchAllAsync(
        AppSettings st, Action<string> log, IProgress<CrawlProgress>? progress,
        Action<DnsServer>? onFound, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cookies = st.HostCookies.ToDictionary(kv => kv.Key, kv => Storage.Unprotect(kv.Value), StringComparer.OrdinalIgnoreCase);
        HostGates.Clear(); // словарь по хостам не растет между запусками
        var found = new ConcurrentDictionary<string, DnsServer>();
        var visited = new ConcurrentDictionary<string, byte>();
        var exclude = st.ExcludeIps.ToHashSet();
        int pages = 0, errors = 0;

        var level = new List<Job>();
        foreach (var s in st.Sources.Where(s => s.Enabled))
            if (TryUri(s.Url, out var u)) level.Add(new Job(Normalize(u), 0, s.Name, s.Type));

        if (st.DiscoveryEnabled)
        {
            var discovered = await DiscoverAsync(st, log, ct);
            level.AddRange(discovered.Select(u => new Job(u, 0, "Поиск: " + u.Host, "text")));
        }
        log($"Стартовых страниц: {level.Count}, глубина {st.CrawlDepth}, лимит {st.CrawlMaxPages} стр.");

        int queued = level.Count;
        void Report(string current) => progress?.Report(new CrawlProgress(
            Math.Min(Volatile.Read(ref pages), st.CrawlMaxPages), st.CrawlMaxPages,
            Math.Max(0, Volatile.Read(ref queued)), found.Count, Volatile.Read(ref errors), current));

        // Очередь вместо раундов: страница обработана, ее ссылки сразу уходят в работу, не дожидаясь остальных страниц
        var queue = Channel.CreateUnbounded<Job>();
        int outstanding = 0;
        void Enqueue(Job j)
        {
            if (visited.ContainsKey(j.Url.AbsoluteUri)) return;
            Interlocked.Increment(ref outstanding);
            Interlocked.Increment(ref queued);
            queue.Writer.TryWrite(j);
        }
        foreach (var j in level) Enqueue(j);
        if (outstanding == 0) queue.Writer.TryComplete();

        async Task ProcessAsync(Job job, CancellationToken c)
        {
                    if (!visited.TryAdd(job.Url.AbsoluteUri, 0)) return;
                    Interlocked.Decrement(ref queued);
                    if (Interlocked.Increment(ref pages) > st.CrawlMaxPages) return;
                    Report(job.Url.Host);

                    string body;
                    bool isHtml;
                    try
                    {
                        var gate = HostGates.GetOrAdd(job.Url.Host, _ => new SemaphoreSlim(2));
                        await gate.WaitAsync(c);
                        try { (body, isHtml) = await DownloadAsync(job.Url, c); }
                        finally { gate.Release(); }
                    }
                    catch (Exception e) when (!c.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref errors);
                        if (job.Depth == 0) log($"«{job.SourceName}»: {e.GetBaseException().Message}");
                        return;
                    }

                    var pageUrl = job.Url.AbsoluteUri;
                    List<DnsServer> items;
                    try
                    {
                        // Разбор тяжелый (регулярки по всей странице) — уводим с потоков ввода-вывода.
                        items = await Task.Run(() => job.Type.Equals("json", StringComparison.OrdinalIgnoreCase)
                            ? SafeParseJson(body, job.SourceName, pageUrl)
                            : ParseText(body, job.SourceName, pageUrl, isHtml), c);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        Interlocked.Increment(ref errors);
                        return; // слишком тяжелая/вредоносная страница
                    }

                    int added = 0;
                    foreach (var it in items)
                    {
                        if (exclude.Contains(it.Primary) || IsNoise(it)) continue;
                        if (found.TryAdd(it.Key, it))
                        {
                            added++;
                            onFound?.Invoke(it); // показываем в списке сразу, не дожидаясь конца обхода
                        }
                    }
                    if (added > 0) { log($"+{added}: {pageUrl}"); Report(job.Url.Host); }

                    // Поиск новых сервисов: у найденного DNS-хоста (dns.example.ru) открываем главную сайта —
                    // там обычно инструкция с IPv4-адресами, которых нет в статье, где сервис упомянули.
                    if (job.Depth < st.CrawlDepth)
                    {
                        foreach (var h in items.Select(i => Uri.TryCreate(i.DohUrl, UriKind.Absolute, out var du) ? du.Host : null)
                                               .Where(h => h != null).Distinct())
                        {
                            var site = BaseHost(h!);
                            foreach (var home in new[] { $"https://{site}/", $"https://{h}/" })
                                if (Uri.TryCreate(home, UriKind.Absolute, out var hu) && !IsBlocked(st, hu))
                                    Enqueue(new Job(hu, job.Depth + 1, "Новый: " + site, "text"));
                        }
                    }

                    if (!isHtml) return;
                    List<(Uri Link, string Text)> links;
                    try { links = ExtractLinks(body, job.Url).ToList(); }
                    catch (RegexMatchTimeoutException) { return; }
                    foreach (var (link, text) in links)
                    {
                        // Telegram-каналы и темы форумов (4PDA и др.): листаем страницы, не тратя глубину.
                        if (IsPaging(job.Url, link))
                        {
                            if (job.TgPage < st.TelegramPages)
                                Enqueue(job with { Url = link, TgPage = job.TgPage + 1 });
                            continue;
                        }
                        if (job.Depth >= st.CrawlDepth) continue;
                        if (ShouldFollow(st, job.Url, link, text))
                            Enqueue(new Job(link, job.Depth + 1, job.SourceName, "text"));
                    }
        }

        // Страница, которая не отвечает, не должна держать весь поиск: у каждой свой жесткий лимит времени.
        // Если поиск долго ничего не получает, журнал покажет, какие адреса еще в работе.
        var inflight = new ConcurrentDictionary<string, DateTime>();
        long lastProgressTicks = DateTime.UtcNow.Ticks;
        using var finished = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (true)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), finished.Token); }
                catch (OperationCanceledException) { return; }
                var idle = DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastProgressTicks), DateTimeKind.Utc);
                if (!inflight.IsEmpty && idle > TimeSpan.FromSeconds(30))
                    log($"Поиск ждет ответа ({inflight.Count} стр.): {string.Join(", ", inflight.Keys.Take(3))}");
            }
        });

        var workers = Enumerable.Range(0, Math.Clamp(st.CrawlParallelism, 1, 16)).Select(_ => Task.Run(async () =>
        {
            await foreach (var job in queue.Reader.ReadAllAsync(ct))
            {
                var started = DateTime.Now;
                var url = job.Url.AbsoluteUri;
                inflight[url] = started;
                using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                jobCts.CancelAfter(TimeSpan.FromSeconds(45));
                try { await ProcessAsync(job, jobCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                    log($"Страница не ответила за 45 с: {url}");
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                    log($"Ошибка на странице {url}: {e.GetBaseException().Message}");
                }
                finally
                {
                    inflight.TryRemove(url, out DateTime _);
                    Volatile.Write(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                    var took = DateTime.Now - started;
                    if (took.TotalSeconds >= 10) log($"Медленная страница ({took.TotalSeconds:F0} с): {url}");
                    if (Interlocked.Decrement(ref outstanding) == 0) queue.Writer.TryComplete();
                }
            }
        }, ct)).ToArray();
        try { await Task.WhenAll(workers); }
        finally { finished.Cancel(); }
        log($"Парсер завершен: страниц {Math.Min(pages, st.CrawlMaxPages)}, ошибок {errors}, серверов {found.Count}");
        return found.Values.ToList();
    }

    // ---------- Поиск новых источников ----------

    private static async Task<List<Uri>> DiscoverAsync(AppSettings st, Action<string> log, CancellationToken ct)
    {
        // Запросы к поисковикам независимы: идут параллельно, но не больше трех сразу, чтобы не получить блокировку
        var queries = st.DiscoveryQueries.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
        using var gate = new SemaphoreSlim(3);
        var perQuery = await Task.WhenAll(queries.Select(async q =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var urls = await SearchDuckDuckGoAsync(q, ct);
                if (urls.Count == 0) urls = await SearchBingAsync(q, ct);
                // Посадочные страницы сервисов (в домене есть dns) — первыми.
                var take = urls.Where(u => !IsBlocked(st, u))
                               .OrderByDescending(u => u.Host.Contains("dns", StringComparison.OrdinalIgnoreCase))
                               .Take(st.DiscoveryResultsPerQuery).ToList();
                log($"Поиск «{q}»: {take.Count} ссылок");
                return take;
            }
            finally { gate.Release(); }
        }));
        return perQuery.SelectMany(x => x).DistinctBy(u => u.AbsoluteUri).ToList();
    }

    private static async Task<List<Uri>> SearchDuckDuckGoAsync(string q, CancellationToken ct)
    {
        try
        {
            var html = await Http.GetStringAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(q), ct);
            var list = new List<Uri>();
            foreach (Match m in Regex.Matches(html, @"class=""result__a""[^>]*href=""([^""]+)""|href=""([^""]+)""[^>]*class=""result__a""", RegexOptions.None, TimeSpan.FromSeconds(2)))
            {
                var href = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                if (href.StartsWith("//")) href = "https:" + href;
                if (TryUri(href, out var u) && u.Host.EndsWith("duckduckgo.com"))
                {
                    var target = HttpUtility.ParseQueryString(u.Query)["uddg"];
                    if (!TryUri(target, out u)) continue;
                }
                if (u != null) list.Add(Normalize(u));
            }
            return list;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(); }
    }

    private static async Task<List<Uri>> SearchBingAsync(string q, CancellationToken ct)
    {
        try
        {
            var html = await Http.GetStringAsync("https://www.bing.com/search?setlang=ru&q=" + Uri.EscapeDataString(q), ct);
            var list = new List<Uri>();
            foreach (Match m in Regex.Matches(html, @"<li class=""b_algo""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""", RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
                if (TryUri(WebUtility.HtmlDecode(m.Groups[1].Value), out var u)) list.Add(Normalize(u));
            return list;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(); }
    }

    // ---------- Загрузка и ссылки ----------

    private static async Task<(string Body, bool IsHtml)> DownloadAsync(Uri url, CancellationToken ct)
    {
        HttpResponseMessage? resp = null;
        for (int hop = 0; ; hop++)
        {
            if (!IsSafeTarget(url))
                throw new InvalidOperationException($"заблокирован адрес локальной сети: {url.Host}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Cookie отправляются только на тот домен, для которого заданы, и только по HTTPS.
            if (url.Scheme == Uri.UriSchemeHttps && MatchCookie(url.Host, out var cookie))
                req.Headers.TryAddWithoutValidation("Cookie", cookie);
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc && hop < 5)
            {
                url = Normalize(new Uri(url, loc));
                resp.Dispose();
                continue;
            }
            break;
        }
        using var _ = resp!;
        resp.EnsureSuccessStatusCode();
        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (media.Length > 0 && !media.StartsWith("text/") && !media.Contains("json") && !media.Contains("xml"))
            throw new InvalidDataException($"пропущено: {media}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buf.Write(chunk, 0, n);
            if (buf.Length > MaxPageBytes) break;
        }
        var bytes = buf.ToArray();
        var body = DecodeBody(bytes, resp.Content.Headers.ContentType?.CharSet);
        bool isHtml = media.Contains("html") || (media.Length == 0 && body.Contains("<html", StringComparison.OrdinalIgnoreCase));
        return (body, isHtml);
    }

    /// <summary>Парсер ходит только в интернет: по имени отсекаем localhost, .local, .lan и порты не для веба.
    /// Сами адреса проверяет ConnectCallback в момент соединения (см. CreateHttp).</summary>
    private static bool IsSafeTarget(Uri u)
    {
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
        if (!u.IsDefaultPort && u.Port is not (80 or 443 or 8080 or 8443)) return false;
        return !(u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                 u.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || !u.Host.Contains('.'));
    }

    /// <summary>Учитываем кодировку страницы (4PDA и многие старые форумы отдают windows-1251).</summary>
    private static string DecodeBody(byte[] bytes, string? charset)
    {
        Encoding enc = Encoding.UTF8;
        charset ??= Regex.Match(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096)),
            @"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } m ? m.Groups[1].Value : null;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { enc = Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { }
        }
        return enc.GetString(bytes);
    }

    /// <summary>Cookie для сайтов, где нужен вход (настройка hostCookies в settings.json).</summary>
    private static Dictionary<string, string> Cookies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cookie только для точного домена из настроек или его поддоменов. Не «родительского» домена:
    /// иначе cookie одного сайта уходили бы на соседние.</summary>
    private static bool MatchCookie(string host, out string cookie)
    {
        foreach (var kv in Cookies)
            if (host.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                cookie = kv.Value;
                return true;
            }
        cookie = "";
        return false;
    }

    private static string BaseHost(string host)
    {
        var parts = host.Split('.');
        return parts.Length > 2 ? string.Join('.', parts[^2..]) : host;
    }

    private static IEnumerable<(Uri Link, string Text)> ExtractLinks(string html, Uri baseUri)
    {
        foreach (Match m in LinkRx().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            if (href.StartsWith("mailto:") || href.StartsWith("javascript:") || href.StartsWith("tg:")) continue;
            if (!Uri.TryCreate(baseUri, href, out var u)) continue;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) continue;
            // 4PDA и похожие форумы оборачивают внешние ссылки: /stat/go?url=…
            if (u.AbsolutePath.Contains("/stat/go") && TryUri(HttpUtility.ParseQueryString(u.Query)["url"], out var real)) u = real;
            var text = WebUtility.HtmlDecode(TagRx().Replace(m.Groups[2].Value, " ")).Trim();
            yield return (Normalize(GithubToRaw(u)), text);
        }
    }

    /// <summary>Ссылки Telegram, которые не ведут на превью канала: боты, «поделиться», приглашения. В них нет списков серверов.</summary>
    private static bool IsTelegramNonChannel(Uri u)
    {
        if (u.Host is not ("t.me" or "telegram.me" or "www.t.me")) return false;
        return !u.AbsolutePath.Trim('/').StartsWith("s/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldFollow(AppSettings st, Uri from, Uri link, string text)
    {
        if (IsBlocked(st, link) || IsTelegramNonChannel(link)) return false;
        var path = link.AbsolutePath.ToLowerInvariant();
        if (BinaryExt.Any(path.EndsWith)) return false;
        if (link.Host != from.Host && !st.FollowExternalLinks) return false;

        // Домены с «dns» в имени (magicdns.ru, dns-ai.ru, xbox-dns.ru…) — почти всегда сами сервисы: открываем всегда.
        if (link.Host.Contains("dns", StringComparison.OrdinalIgnoreCase) && link.Host != from.Host) return true;
        var hay = (Uri.UnescapeDataString(link.AbsoluteUri) + " " + text).ToLowerInvariant();
        return st.LinkKeywords.Any(k => hay.Contains(k.ToLowerInvariant()));
    }

    private static bool IsPaging(Uri from, Uri link)
    {
        if (!from.Host.Equals(link.Host, StringComparison.OrdinalIgnoreCase)) return false;

        // Telegram: t.me/s/канал?before=N
        if (from.Host == "t.me")
            return link.AbsolutePath.StartsWith("/s/") && link.Query.Contains("before=") &&
                   link.AbsolutePath.Equals(from.AbsolutePath, StringComparison.OrdinalIgnoreCase);

        // Форумы IPB (4PDA): index.php?showtopic=ID&st=N — та же тема, другая страница.
        var a = HttpUtility.ParseQueryString(from.Query);
        var b = HttpUtility.ParseQueryString(link.Query);
        var topic = a["showtopic"];
        if (topic != null && topic == b["showtopic"] && b["st"] != null && b["view"] == null) return true;
        var forum = a["showforum"];
        return forum != null && forum == b["showforum"] && b["st"] != null && b["showtopic"] == null;
    }

    private static bool IsBlocked(AppSettings st, Uri u) =>
        st.BlockedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                                 u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>github.com/u/r/blob/branch/file → raw.githubusercontent.com/u/r/branch/file</summary>
    private static Uri GithubToRaw(Uri u)
    {
        if (u.Host != "github.com") return u;
        var seg = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length >= 5 && seg[2] == "blob")
            return new Uri($"https://raw.githubusercontent.com/{seg[0]}/{seg[1]}/{string.Join('/', seg.Skip(3))}");
        return u;
    }

    private static Uri Normalize(Uri u)
    {
        var b = new UriBuilder(u) { Fragment = "" };
        // Убираем трекинговые параметры, чтобы не открывать одну страницу дважды.
        var qs = HttpUtility.ParseQueryString(b.Query);
        foreach (var k in qs.AllKeys.OfType<string>().Where(k => k.StartsWith("utm_")).ToList()) qs.Remove(k);
        b.Query = qs.ToString();
        if ((b.Scheme == "https" && b.Port == 443) || (b.Scheme == "http" && b.Port == 80)) b.Port = -1;
        return b.Uri;
    }

    private static bool TryUri(string? s, out Uri u)
    {
        u = null!;
        return !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s, UriKind.Absolute, out u!) &&
               (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
    }

    // ---------- Разбор содержимого ----------

    /// <summary>
    /// IP на одной строке — пара основной/резервный. Для HTML-страниц IP берется только если
    /// рядом есть слова про DNS, чтобы не собирать случайные числа из статей.
    /// </summary>
    public static List<DnsServer> ParseText(string raw, string sourceName, string pageUrl, bool isHtml)
    {
        var text = raw;
        if (isHtml)
        {
            text = ScriptRx().Replace(text, " ");
            text = BlockTagRx().Replace(text, "\n");
            text = WebUtility.HtmlDecode(TagRx().Replace(text, " "));
        }
        var lines = text.Split('\n');
        var host = Uri.TryCreate(pageUrl, UriKind.Absolute, out var pu) ? pu.Host : sourceName;
        var list = new List<DnsServer>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var ips = Ipv4Rx().Matches(line).Select(m => m.Value).Where(IsPublic).Distinct().ToList();
            if (ips.Count > 0 && (!isHtml || HasDnsContext(lines, i)))
            {
                list.Add(new DnsServer
                {
                    Name = host,
                    Primary = ips[0],
                    Secondary = ips.Count > 1 ? ips[1] : null,
                    Source = sourceName,
                    SourceUrl = pageUrl,
                });
            }
            foreach (Match m in DohRx().Matches(line))
            {
                var url = m.Value.TrimEnd('.', ',', ')', ';');
                if (!IsValidDoh(url, out var u)) continue;
                list.Add(new DnsServer
                {
                    Name = u.Host,
                    DohUrl = url,
                    Source = sourceName,
                    SourceUrl = pageUrl,
                });
            }

            // Хосты, указанные просто именем (так их публикуют для «Частного DNS» Android / DoT).
            // Для Windows используем DoH по стандартному пути /dns-query.
            if (isHtml && !HasDnsContext(lines, i)) continue;
            foreach (Match m in DnsHostRx().Matches(line))
            {
                var h = m.Groups[1].Value.ToLowerInvariant().TrimEnd('.');
                if (h.EndsWith(".png") || h.EndsWith(".txt") || h.EndsWith(".html") || h.EndsWith(".apk")) continue;
                if (h == host) continue; // ссылка на сам сайт-источник
                list.Add(new DnsServer
                {
                    Name = h,
                    DohUrl = $"https://{h}/dns-query",
                    Source = sourceName,
                    SourceUrl = pageUrl,
                });
            }
        }
        return list;
    }

    private static bool HasDnsContext(string[] lines, int i)
    {
        var from = Math.Max(0, i - 3);
        var to = Math.Min(lines.Length - 1, i + 1);
        var ctx = string.Join(" ", lines[from..(to + 1)]).ToLowerInvariant();
        return DnsContextWords.Any(ctx.Contains);
    }

    private sealed class JsonItem
    {
        public string? Name { get; set; }
        public string? Primary { get; set; }
        public string? Secondary { get; set; }
        public string? Doh { get; set; }
        public string? Url { get; set; }
    }

    private static List<DnsServer> SafeParseJson(string json, string sourceName, string pageUrl)
    {
        try { return ParseJson(json, sourceName, pageUrl); }
        catch (JsonException) { return ParseText(json, sourceName, pageUrl, false); }
    }

    public static List<DnsServer> ParseJson(string json, string sourceName, string pageUrl)
    {
        var items = JsonSerializer.Deserialize<List<JsonItem>>(json, Storage.Options) ?? new();
        return items
            .Where(i => IsPublic(i.Primary ?? "") || IsValidDoh(i.Doh, out _))
            .Select(i => new DnsServer
            {
                Name = string.IsNullOrWhiteSpace(i.Name) ? $"{sourceName}: {i.Primary ?? i.Doh}" : i.Name!,
                Primary = IsPublic(i.Primary ?? "") ? i.Primary! : "",
                Secondary = IsPublic(i.Secondary ?? "") ? i.Secondary : null,
                DohUrl = IsValidDoh(i.Doh, out _) ? i.Doh : null,
                Source = sourceName,
                SourceUrl = TryUri(i.Url, out var u) ? u.AbsoluteUri : pageUrl,
            })
            .ToList();
    }

    /// <summary>Обычные публичные DNS (не Smart DNS): к ИИ не помогают, в список не берем.</summary>
    private static readonly string[] PlainResolverHosts =
    {
        "dns.google", "dns.google.com", "cloudflare-dns.com", "dns.cloudflare.com", "one.one.one.one",
        "security.cloudflare-dns.com", "family.cloudflare-dns.com", "dns.quad9.net", "dns9.quad9.net",
        "dns.yandex.ru", "common.dot.dns.yandex.net", "safe.dot.dns.yandex.net", "family.dot.dns.yandex.net",
        "dns.adguard.com", "dns.adguard-dns.com", "dns-unfiltered.adguard.com", "unfiltered.adguard-dns.com",
        "family.adguard-dns.com", "doh.opendns.com", "dns.nextdns.io", "doh.mullvad.net", "dns.mullvad.net",
        "doh.dns.sb", "dns.twnic.tw", "doh.libredns.gr", "dnsforge.de", "dns.dismail.de", "doh.blahdns.com",
    };

    private static readonly string[] PlainResolverIps =
    {
        "1.1.1.2", "1.0.0.2", "1.1.1.3", "1.0.0.3", "9.9.9.10", "9.9.9.11", "149.112.112.10", "149.112.112.11",
        "77.88.8.2", "77.88.8.88", "77.88.8.3", "77.88.8.7", "94.140.14.14", "94.140.15.15", "94.140.14.15",
        "94.140.15.16", "94.140.14.140", "94.140.14.141", "185.228.168.9", "185.228.169.9", "76.76.2.0", "76.76.10.0",
        "208.67.222.123", "208.67.220.123", "64.6.64.6", "64.6.65.6", "8.26.56.26", "8.20.247.20", "4.2.2.1", "4.2.2.2",
        "156.154.70.1", "156.154.71.1", "156.154.70.2", "156.154.71.2", "195.46.39.39", "195.46.39.40",
    };

    /// <summary>Заглушки из инструкций: ваш-домен.com, example.com, your-id и т.п.</summary>
    private static bool IsPlaceholderHost(string host) =>
        host.Any(ch => ch > 127) || host.StartsWith("xn--") || host.Contains(".xn--") ||
        new[] { "example", "your", "ваш", "domain", "my-dns", "mydns", "server.com", "test." }
            .Any(w => host.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static bool IsNoise(DnsServer s)
    {
        if (!string.IsNullOrEmpty(s.Primary) && PlainResolverIps.Contains(s.Primary)) return true;
        if (Uri.TryCreate(s.DohUrl, UriKind.Absolute, out var u))
            return IsPlaceholderHost(u.Host) || PlainResolverHosts.Contains(u.Host.ToLowerInvariant());
        return false;
    }

    /// <summary>DoH-адрес со страницы: только https, без логина в адресе, разумной длины, не локальный.</summary>
    public static bool IsValidDoh(string? url, out Uri u)
    {
        u = null!;
        return url is { Length: < 300 } && Uri.TryCreate(url, UriKind.Absolute, out u!) &&
               u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) &&
               u.Host.Contains('.') && !u.IsLoopback &&
               (!IPAddress.TryParse(u.Host, out var ip) || IsPublic(ip.ToString()));
    }

    /// <summary>Отсекаем частные, служебные и мусорные адреса.</summary>
    public static bool IsPublic(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = a.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                 (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                 (b[0] == 169 && b[1] == 254) ||
                 (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                 (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 192 && b[1] == 0 && b[2] == 2) ||
                 (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
    }
}
