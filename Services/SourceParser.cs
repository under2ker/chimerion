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
    private static int _capLogged;
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b", RegexOptions.None, 2000)]
    private static partial Regex Ipv4Rx();

    // Кандидаты в IPv6: группы шестнадцатеричных цифр через двоеточие. Точная проверка — IPAddress.TryParse и IsPublic6
    [GeneratedRegex(@"(?<![0-9A-Fa-f:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f:.])", RegexOptions.None, 2000)]
    private static partial Regex Ipv6Rx();

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

    // Записи вида tls://dns.example.ru или quic://dns.example.ru — DoT/DoQ, тот же сервер доступен по DoH
    [GeneratedRegex(@"\b(?:tls|quic|h3)://([a-z0-9][a-z0-9.-]*\.[a-z]{2,})", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex TlsHostRx();

    // Любое доменное имя: берется только в строках про «частный DNS» / DoT, где адрес публикуют без приставки dns.
    [GeneratedRegex(@"(?<![\w.@/:-])((?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24})(?![\w.-]*[/@])(?![\w-])", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex AnyHostRx();

    /// <summary>Признаки строки с адресом для «Частного DNS» Android или DoT.</summary>
    private static readonly string[] PrivateDnsWords =
    {
        "частн", "private dns", "dns over tls", "dns-over-tls", "dot:", "dot ", "имя хоста", "hostname", "tls://",
    };

    /// <summary>Сайты, которые упоминаются рядом с DNS, но сами DNS-серверами не являются.</summary>
    private static readonly string[] NotDnsHosts =
    {
        "github.com", "githubusercontent.com", "t.me", "telegram.org", "youtube.com", "youtu.be", "google.com", "apple.com",
        "microsoft.com", "wikipedia.org", "vk.com", "4pda.to", "habr.com", "comss.ru", "openai.com", "chatgpt.com",
        "anthropic.com", "claude.ai", "gemini.google.com", "googleapis.com", "android.com", "play.google.com", "yandex.ru",
        "github.io", "itch.io", "nip.io", "sslip.io", "last.fm", "gitlab.io", "pages.dev", "vercel.app", "netlify.app",
    };

    /// <summary>Имя похоже на файл (dns.comss.one.mobileconfig, reset-dns.cmd), а не на адрес сервера.</summary>
    private static readonly string[] FileExt =
    {
        ".mobileconfig", ".cmd", ".bat", ".ps1", ".sh", ".conf", ".cfg", ".json", ".yml", ".yaml", ".md", ".txt",
        ".html", ".htm", ".php", ".exe", ".apk", ".zip", ".png", ".jpg", ".svg", ".xml", ".ini", ".reg", ".py",
    };

    private static bool LooksLikeFile(string host) => FileExt.Any(host.EndsWith) || BinaryExt.Any(host.EndsWith);

    /// <summary>Локальные и служебные зоны и расширения кода: такие имена не бывают публичными DNS-серверами.</summary>
    private static readonly string[] BadTlds = { "lan", "local", "home", "internal", "localdomain", "corp", "py", "map", "ts", "cs", "go", "rs", "lock" };

    /// <summary>Имя точно не адрес Smart DNS: файл, локальная зона, хостинг или известный сайт, а не сервер.</summary>
    private static bool IsJunkHost(string host)
    {
        var h = host.ToLowerInvariant();
        if (IPAddress.TryParse(h, out _)) return false; // DoH по IP решает список исключений
        return LooksLikeFile(h) || BadTlds.Contains(h[(h.LastIndexOf('.') + 1)..])
            || NotDnsHosts.Any(n => h == n || h.EndsWith("." + n));
    }

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

    /// <param name="seeds">
    /// Страницы, где в прошлые разы нашлись рабочие серверы: их обходим с самого начала, как источники. Там же
    /// обычно появляются новые адреса, а пустые страницы со временем выпадают сами.
    /// </param>
    public static async Task<List<DnsServer>> FetchAllAsync(
        AppSettings st, Action<string> log, IProgress<CrawlProgress>? progress,
        Action<DnsServer>? onFound, CancellationToken ct, IEnumerable<string>? seeds = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cookies = st.HostCookies.ToDictionary(kv => kv.Key, kv => Storage.Unprotect(kv.Value), StringComparer.OrdinalIgnoreCase);
        HostGates.Clear(); // словарь по хостам не растет между запусками
        Array.Clear(_engineHits);
        _ddgCaptcha = false;
        _braveLimited = false;
        var found = new ConcurrentDictionary<string, DnsServer>();
        Interlocked.Exchange(ref _capLogged, 0);
        var visited = new ConcurrentDictionary<string, byte>();
        var hostPages = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var exclude = st.ExcludeIps.ToHashSet();
        int pages = 0, errors = 0;

        var level = new List<Job>();
        foreach (var s in st.Sources.Where(s => s.Enabled))
            if (TryUri(s.Url, out var u)) level.Add(new Job(Normalize(u), 0, s.Name, s.Type));
        int fromSeeds = 0;
        foreach (var s in seeds ?? Enumerable.Empty<string>())
            if (TryUri(s, out var u) && !IsBlocked(st, u) && level.All(j => j.Url.AbsoluteUri != Normalize(u).AbsoluteUri))
            {
                level.Add(new Job(Normalize(u), 0, "Удачная страница: " + u.Host, "text"));
                fromSeeds++;
            }
        log($"Источников: {level.Count}" + (fromSeeds > 0 ? $" (из них {fromSeeds} страниц, где раньше нашлись рабочие серверы)" : "") +
            $", глубина {st.CrawlDepth}, лимит {st.CrawlMaxPages} стр. Поиск новых источников идет параллельно");

        int queued = 0;
        void Report(string current) => progress?.Report(new CrawlProgress(
            Math.Min(Volatile.Read(ref pages), st.CrawlMaxPages), st.CrawlMaxPages,
            Math.Max(0, Volatile.Read(ref queued)), found.Count, Volatile.Read(ref errors), current));

        // Очередь вместо раундов: страница обработана, ее ссылки сразу уходят в работу, не дожидаясь остальных страниц
        var queue = Channel.CreateUnbounded<Job>();
        int outstanding = 0;
        void Enqueue(Job j)
        {
            if (visited.ContainsKey(j.Url.AbsoluteUri) || IsAdult(j.Url)) return;
            Interlocked.Increment(ref outstanding);
            Interlocked.Increment(ref queued);
            queue.Writer.TryWrite(j);
        }
        // Поиск источников (DuckDuckGo, Bing, GitHub) идет одновременно с обходом: пока поисковики отвечают,
        // известные источники уже обходятся, а найденные страницы встают в очередь по мере ответа.
        // Лишняя единица в outstanding держит очередь открытой, пока поиск источников не закончен.
        Interlocked.Increment(ref outstanding);
        foreach (var j in level) Enqueue(j);
        var discovery = Task.Run(async () =>
        {
            try
            {
                if (st.DiscoveryEnabled)
                    await DiscoverAsync(st, log, ct, u => Enqueue(new Job(u, 1, "Поиск: " + u.Host, "text")));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { log("Поиск источников: " + e.GetBaseException().Message); }
            finally { if (Interlocked.Decrement(ref outstanding) == 0) queue.Writer.TryComplete(); }
        });

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

                    // Сотни адресов на одной странице: общий каталог публичных DNS (public-dns.info дал 62 тысячи), а не подборка
                    // Smart DNS. Страницу целиком пропускаем и по ее ссылкам не идем
                    if (items.Count > ServerListGuard.MaxPerPage || ServerListGuard.IsMassHost(job.Url.Host))
                    {
                        if (items.Count > 0) log($"Пропущено {items.Count} адресов: {pageUrl} похож на общий список DNS, а не на подборку Smart DNS");
                        return;
                    }
                    int added = 0;
                    foreach (var it in items)
                    {
                        if (found.Count >= ServerListGuard.MaxNewPerSearch)
                        {
                            if (Interlocked.Exchange(ref _capLogged, 1) == 0) log($"Найдено уже {ServerListGuard.MaxNewPerSearch} серверов: остальные страницы этого поиска не добавляются");
                            break;
                        }
                        // Обычные публичные DNS и в виде DoH по IP (https://1.1.1.1/dns-query) — не Smart DNS
                        var dohHost = Uri.TryCreate(it.DohUrl, UriKind.Absolute, out var du0) ? du0.Host : null;
                        if (exclude.Contains(it.Primary) || (dohHost != null && (exclude.Contains(dohHost) || IsJunkHost(dohHost))) || IsNoise(it)) continue;
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
                                if (!IsPlaceholderHost(site) && Uri.TryCreate(home, UriKind.Absolute, out var hu) && !IsBlocked(st, hu))
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
                        if (!ShouldFollow(st, job.Url, link, text, job.Depth)) continue;
                        // Не больше 120 страниц с одного сайта: широкий обход не должен съесть весь лимит
                        if (hostPages.AddOrUpdate(link.Host, 1, (_, v) => v + 1) > 120) continue;
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
                jobCts.CancelAfter(TimeSpan.FromSeconds(30)); // зависшая страница не держит обходчик дольше 30 с
                try { await ProcessAsync(job, jobCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                    log($"Страница не ответила за 30 с: {url}");
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
        try { await Task.WhenAll(workers); await discovery; }
        finally { finished.Cancel(); }
        log($"Парсер завершен: страниц {Math.Min(pages, st.CrawlMaxPages)}, ошибок {errors}, серверов {found.Count}");
        return found.Values.ToList();
    }

    // ---------- Поиск новых источников ----------

    /// <summary>Новые источники из поисковиков и GitHub. onUrl получает каждую ссылку сразу, как только ответил ее запрос.</summary>
    private static async Task<List<Uri>> DiscoverAsync(AppSettings st, Action<string> log, CancellationToken ct, Action<Uri>? onUrl = null)
    {
        // Сначала страницы из ночного поиска сервера общей карты: они не зависят от того, пускают ли поисковики вас
        var serverPages = (await Crowd.PagesAsync(ct)).Where(p => TryUri(p, out _)).Select(p => Normalize(new Uri(p))).Where(u => !IsBlocked(st, u) && !IsAdult(u)).ToList();
        foreach (var u in serverPages) onUrl?.Invoke(u);
        if (serverPages.Count > 0) log($"Общая карта: {serverPages.Count} страниц из поиска на сервере");

        // Запросы к поисковикам независимы: идут параллельно, но не больше трех сразу, чтобы не получить блокировку
        var queries = st.DiscoveryQueries.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
        using var gate = new SemaphoreSlim(3);
        var perQuery = await Task.WhenAll(queries.Select(async q =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Все поисковики сразу: выдачи у них разные, а какой-то может не ответить или попросить капчу
                var brave = SearchBraveAsync(q, queries.IndexOf(q), ct);
                var ddg = SearchDuckDuckGoAsync(q, ct);
                var bing = SearchBingAsync(q, ct);
                var (b, d, g) = (await brave, await ddg, await bing);
                Interlocked.Add(ref _engineHits[0], b.Count); Interlocked.Add(ref _engineHits[1], d.Count); Interlocked.Add(ref _engineHits[2], g.Count);
                var urls = b.Concat(d).Concat(g).DistinctBy(u => u.AbsoluteUri).ToList();
                // Посадочные страницы сервисов (в домене есть dns) — первыми.
                var take = urls.Where(u => !IsBlocked(st, u))
                               .OrderByDescending(u => u.Host.Contains("dns", StringComparison.OrdinalIgnoreCase))
                               .Take(st.DiscoveryResultsPerQuery).ToList();
                log($"Поиск «{q}»: {take.Count} ссылок");
                foreach (var u in take) onUrl?.Invoke(u);
                return take;
            }
            finally { gate.Release(); }
        }));
        log($"Поисковики: Brave {_engineHits[0]}{(_braveLimited ? " (попросил реже, остаток пропущен)" : "")}, DuckDuckGo {_engineHits[1]}{(_ddgCaptcha ? " (просит капчу, пропущен)" : "")}, Bing {_engineHits[2]} ссылок");
        // Репозитории GitHub со списками DNS для ИИ-сервисов: обновляются чаще статей. README берем сразу файлом:
        // HTML-страница репозитория тяжелая, а список почти всегда лежит в README
        var repos = new List<Uri>();
        foreach (var q in GitHubQueries)
        {
            var found = await SearchGitHubAsync(q, ct);
            foreach (var u in found.Where(u => repos.All(r => r.AbsoluteUri != u.AbsoluteUri)))
            {
                repos.Add(u);
                onUrl?.Invoke(u);
                if (ReadmeOf(u) is { } readme) onUrl?.Invoke(readme);
            }
            if (found.Count > 0) log($"GitHub «{q}»: {found.Count} репозиториев");
        }
        return perQuery.SelectMany(x => x).Concat(repos).DistinctBy(u => u.AbsoluteUri).ToList();
    }

    // Без входа GitHub дает 10 запросов поиска в минуту: держим шесть
    private static readonly string[] GitHubQueries = { "dns chatgpt", "smart dns ai", "dns gemini claude", "ai dns russia", "smartdns russia", "doh chatgpt" };

    // Сколько ссылок дал каждый поисковик за этот поиск: Brave, DuckDuckGo, Bing
    private static readonly int[] _engineHits = new int[3];
    private static volatile bool _ddgCaptcha;

    /// <summary>github.com/user/repo → raw-файл README из ветки по умолчанию.</summary>
    public static Uri? ReadmeOf(Uri repo)
    {
        if (repo.Host != "github.com") return null;
        var seg = repo.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return seg.Length == 2 ? new Uri($"https://raw.githubusercontent.com/{seg[0]}/{seg[1]}/HEAD/README.md") : null;
    }

    /// <summary>Сколько запросов за поиск отправлять в Brave: чаще он отвечает «слишком много запросов» (429).</summary>
    private const int BraveQueries = 8;
    private static readonly SemaphoreSlim BraveGate = new(1);
    private static volatile bool _braveLimited;

    /// <summary>
    /// Brave Search: обычная HTML-выдача, ссылки на сайты стоят прямо в href. Запросы строго по одному с паузой и не
    /// больше BraveQueries за поиск; после первого 429 до конца поиска Brave не спрашиваем.
    /// </summary>
    private static async Task<List<Uri>> SearchBraveAsync(string q, int index, CancellationToken ct)
    {
        if (index >= BraveQueries || _braveLimited) return new();
        await BraveGate.WaitAsync(ct);
        try
        {
            if (_braveLimited) return new();
            using var resp = await Http.GetAsync("https://search.brave.com/search?source=web&q=" + Uri.EscapeDataString(q), ct);
            if ((int)resp.StatusCode == 429) { _braveLimited = true; return new(); }
            if (!resp.IsSuccessStatusCode) return new();
            var list = ParseBrave(await resp.Content.ReadAsStringAsync(ct));
            await Task.Delay(1500, ct); // пауза перед следующим запросом
            return list;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(); }
        finally { BraveGate.Release(); }
    }

    /// <summary>Ссылки на сайты из выдачи Brave, без ссылок самого Brave и его картинок.</summary>
    public static List<Uri> ParseBrave(string html)
    {
        var list = new List<Uri>();
        foreach (Match m in Regex.Matches(html, @"href=""(https?://[^""]+)""", RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            if (!TryUri(WebUtility.HtmlDecode(m.Groups[1].Value), out var u)) continue;
            var h = u.Host.ToLowerInvariant();
            if (h.EndsWith("brave.com") || h.EndsWith("bravesoftware.com") || h.StartsWith("imgs.") || h.StartsWith("tiles.")) continue;
            list.Add(Normalize(u));
        }
        return list.DistinctBy(u => u.AbsoluteUri).ToList();
    }

    /// <summary>Поиск репозиториев через API GitHub (без входа: 10 запросов в минуту, хватает на четыре запроса).</summary>
    private static async Task<List<Uri>> SearchGitHubAsync(string q, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/search/repositories?sort=updated&per_page=8&q=" + Uri.EscapeDataString(q));
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.TryGetProperty("html_url", out var h) ? h.GetString() : null)
                .Where(h => TryUri(h, out _)).Select(h => new Uri(h!)).ToList();
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(); }
    }

    private static async Task<List<Uri>> SearchDuckDuckGoAsync(string q, CancellationToken ct)
    {
        try
        {
            if (_ddgCaptcha) return new(); // уже попросил капчу в этом поиске: дальше не спрашиваем
            var html = await Http.GetStringAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(q), ct);
            if (html.Contains("anomaly", StringComparison.OrdinalIgnoreCase) && !html.Contains("result__a")) { _ddgCaptcha = true; return new(); }
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

    /// <summary>
    /// Ссылка выдачи Bing бывает переходной (bing.com/ck/a?…&amp;u=a1&lt;base64&gt;): настоящий адрес закодирован в параметре u.
    /// Без раскодирования обход открывал саму переходную страницу (12–16 с) и дальше не шел. Null, если адрес не достать.
    /// </summary>
    public static Uri? UnwrapBing(Uri u)
    {
        if (!u.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)) return u;
        var code = HttpUtility.ParseQueryString(u.Query)["u"];
        if (string.IsNullOrEmpty(code) || !code.StartsWith("a1")) return null;
        try
        {
            var b64 = code[2..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var target = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return TryUri(target, out var real) && !real.Host.EndsWith("bing.com") ? real : null;
        }
        catch (FormatException) { return null; }
    }

    private static async Task<List<Uri>> SearchBingAsync(string q, CancellationToken ct)
    {
        try
        {
            var html = await Http.GetStringAsync("https://www.bing.com/search?setlang=ru&q=" + Uri.EscapeDataString(q), ct);
            var list = new List<Uri>();
            foreach (Match m in Regex.Matches(html, @"<li class=""b_algo""[^>]*>.*?<a[^>]*href=""(https?://[^""]+)""", RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
                if (TryUri(WebUtility.HtmlDecode(m.Groups[1].Value), out var u) && UnwrapBing(u) is { } real) list.Add(Normalize(real));
            return list;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(); }
    }

    // ---------- Загрузка и ссылки ----------

    /// <summary>
    /// Лента канала t.me/s/… через сервер общей карты: из России t.me часто не открывается напрямую. Null — не лента
    /// канала или сервер не ответил (тогда пробуем напрямую).
    /// </summary>
    internal static Func<Uri, CancellationToken, Task<string?>> TelegramRelay = TelegramRelayAsync;

    private static async Task<string?> TelegramRelayAsync(Uri url, CancellationToken ct)
    {
        if (RelayUrl(url) is not { } relay) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.GetAsync(relay, cts.Token);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(cts.Token) : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>t.me/s/канал?before=N → адрес той же ленты на сервере общей карты.</summary>
    public static Uri? RelayUrl(Uri url)
    {
        if (url.Host != "t.me") return null;
        var seg = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length != 2 || seg[0] != "s" || !Regex.IsMatch(seg[1], "^[A-Za-z][A-Za-z0-9_]{4,31}$")) return null;
        var before = HttpUtility.ParseQueryString(url.Query)["before"];
        return new Uri($"{Crowd.Endpoint}/v1/tg?ch={seg[1]}" + (before != null && Regex.IsMatch(before, @"^\d{1,10}$") ? $"&before={before}" : ""));
    }

    private static async Task<(string Body, bool IsHtml)> DownloadAsync(Uri url, CancellationToken ct)
    {
        if (await TelegramRelay(url, ct) is { } feed) return (feed, true);
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
            yield return (Normalize(TelegramToFeed(GithubToRaw(u))), text);
        }
    }

    /// <summary>
    /// t.me/канал → t.me/s/канал: открытая лента канала, ее можно читать без входа. Боты, приглашения, стикеры и
    /// ссылки на отдельные сообщения чужих чатов остаются как есть (их отсекает IsTelegramNonChannel).
    /// </summary>
    public static Uri TelegramToFeed(Uri u)
    {
        if (u.Host is not ("t.me" or "telegram.me" or "www.t.me")) return u;
        var seg = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length is < 1 or > 2 || seg[0] == "s") return u;
        var name = seg[0];
        if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{4,31}$") || name.EndsWith("bot", StringComparison.OrdinalIgnoreCase)
            || name is "joinchat" or "addstickers" or "addemoji" or "share" or "proxy" or "socks" or "iv" or "setlanguage" or "contact") return u;
        // t.me/канал/123 — сообщение: лента с ним начинается рядом
        return new Uri(seg.Length == 2 && int.TryParse(seg[1], out var id) ? $"https://t.me/s/{name}?before={id + 1}" : $"https://t.me/s/{name}");
    }

    /// <summary>Ссылки Telegram, которые не ведут на превью канала: боты, «поделиться», приглашения. В них нет списков серверов.</summary>
    private static bool IsTelegramNonChannel(Uri u)
    {
        if (u.Host is not ("t.me" or "telegram.me" or "www.t.me")) return false;
        return !u.AbsolutePath.Trim('/').StartsWith("s/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Служебные страницы сайтов: вход, поиск, профили, теги. В них списков серверов не бывает.</summary>
    private static readonly string[] NavWords =
    {
        "login", "logout", "register", "signup", "sign-in", "signin", "auth", "privacy", "terms", "cookie", "search",
        "/tag/", "/tags/", "/user/", "/users/", "/profile", "/share", "/cart", "/account", "/subscribe", "/rss", "/feed",
        "/stargazers", "/watchers", "/forks", "/issues", "/pulls", "/actions", "/projects", "/security", "/pulse", "/graphs",
    };

    private static bool ShouldFollow(AppSettings st, Uri from, Uri link, string text, int depth = 1)
    {
        if (IsBlocked(st, link) || IsTelegramNonChannel(link)) return false;
        var path = link.AbsolutePath.ToLowerInvariant();
        if (BinaryExt.Any(path.EndsWith)) return false;
        var full = (path + link.Query).ToLowerInvariant();
        if (NavWords.Any(full.Contains)) return false;

        // Файлы в репозиториях GitHub (уже переведены в raw): это и есть списки
        if (link.Host == "raw.githubusercontent.com") return true;
        // GitHub: внутри того же репозитория — по файлам и папкам, без проверки слов
        if (from.Host == "github.com" && link.Host == "github.com")
        {
            var a = from.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var b = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return a.Length >= 2 && b.Length >= 2 && a[0] == b[0] && a[1] == b[1]
                && (b.Length == 2 || b[2] is "tree" or "blob" or "commits" or "releases" or "wiki");
        }
        // Стартовая страница: ее сайт обходим широко, по всем ссылкам (лимит страниц на сайт — у вызывающего)
        if (depth == 0 && link.Host == from.Host) return true;

        if (link.Host != from.Host && !st.FollowExternalLinks) return false;

        // Сайт по IP-адресу (https://1.0.0.1/…) — страница обычного резолвера, а не список Smart DNS
        if (IPAddress.TryParse(link.Host, out _)) return false;
        // Лента Telegram-канала: открываем, если в имени канала или тексте ссылки есть слова про DNS, ИИ или обход
        if (link.Host == "t.me" && link.AbsolutePath.StartsWith("/s/"))
        {
            var tg = (link.AbsolutePath + " " + text).ToLowerInvariant();
            return tg.Contains("dns") || tg.Contains("днс") || StrongWords.Any(tg.Contains);
        }
        // Домены с «dns» в имени (magicdns.ru, dns-ai.ru, xbox-dns.ru…) — почти всегда сами сервисы: открываем всегда.
        if (link.Host.Contains("dns", StringComparison.OrdinalIgnoreCase) && link.Host != from.Host) return true;
        var hay = (Uri.UnescapeDataString(link.AbsoluteUri) + " " + text).ToLowerInvariant();
        // На чужой сайт — только по словам про ИИ и обход блокировок: одно слово «dns» уводило в энциклопедии
        // и справки (статьи про корневые серверы, Google DNS), где Smart DNS не бывает
        if (link.Host != from.Host) return StrongWords.Any(hay.Contains);
        return st.LinkKeywords.Any(k => hay.Contains(k.ToLowerInvariant()));
    }

    // Сайты для взрослых, казино и ставки: в прогоне поиск выдал такой сайт, и обход ушел по его страницам.
    // Списков DNS там не бывает, такие адреса не открываются никогда
    private static readonly string[] AdultHostWords =
    {
        "porn", "porno", "xxx", "sex", "hentai", "xvideo", "xhamster", "erotic", "eroti", "casino", "kazino",
        "1xbet", "betting", "slots", "vulkan", "escort", "nude", "milf",
    };
    private static readonly string[] AdultPathWords = { "porn", "порно", "xxx", "hentai", "sex-", "-sex", "секс", "эрот", "casino", "казино" };

    public static bool IsAdult(Uri u)
    {
        var host = u.Host.ToLowerInvariant();
        if (AdultHostWords.Any(host.Contains)) return true;
        var rest = Uri.UnescapeDataString(u.PathAndQuery).ToLowerInvariant();
        return AdultPathWords.Any(rest.Contains);
    }

    private static readonly string[] StrongWords =
    {
        "chatgpt", "openai", "gpt", "gemini", "claude", "нейросет", "нейро", "ии-", "-ai", "ai-", "smart", "doh",
        "без-vpn", "bez-vpn", "без vpn", "геоблок", "unblock", "обход", "разблок",
    };

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
            var ips6 = Ipv6Rx().Matches(line).Select(m => m.Value).Where(IsPublic6).Select(v => IPAddress.Parse(v).ToString()).Distinct().ToList();
            bool context = !isHtml || HasDnsContext(lines, i);
            if (ips.Count > 0 && context)
            {
                list.Add(new DnsServer
                {
                    Name = host,
                    Primary = ips[0],
                    Secondary = ips.Count > 1 ? ips[1] : null,
                    Ipv6 = ips6.Take(2).ToList(), // IPv6 в той же строке — это адреса того же сервера
                    Source = sourceName,
                    SourceUrl = pageUrl,
                });
            }
            else if (ips6.Count > 0 && context)
            {
                list.Add(new DnsServer
                {
                    Name = host,
                    Primary = ips6[0],
                    Secondary = ips6.Count > 1 ? ips6[1] : null,
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

            // DoT/DoQ-записи (tls://…): тот же сервер, для Windows берем DoH по стандартному пути
            foreach (Match m in TlsHostRx().Matches(line))
                AddHost(m.Groups[1].Value);

            // Строка про «Частный DNS» / DoT: адрес там часто без приставки dns. (eu.geohide.ru), берем любой хост
            if (HasPrivateDnsContext(lines, i))
                foreach (Match m in AnyHostRx().Matches(line))
                    AddHost(m.Groups[1].Value);

            // Хосты, указанные просто именем (так их публикуют для «Частного DNS» Android / DoT).
            // Для Windows используем DoH по стандартному пути /dns-query.
            if (isHtml && !HasDnsContext(lines, i)) continue;
            foreach (Match m in DnsHostRx().Matches(line))
            {
                var h = m.Groups[1].Value.ToLowerInvariant().TrimEnd('.');
                if (LooksLikeFile(h) || NotDnsHosts.Any(n => h == n || h.EndsWith("." + n))) continue;
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

        void AddHost(string raw)
        {
            var h = raw.ToLowerInvariant().TrimEnd('.');
            if (h == host || h.Split('.').Length < 2 || LooksLikeFile(h)) return;
            if (NotDnsHosts.Any(n => h == n || h.EndsWith("." + n))) return;
            if (list.Any(x => x.DohUrl == $"https://{h}/dns-query")) return;
            list.Add(new DnsServer { Name = h, DohUrl = $"https://{h}/dns-query", Source = sourceName, SourceUrl = pageUrl });
        }
    }

    private static bool HasPrivateDnsContext(string[] lines, int i)
    {
        var from = Math.Max(0, i - 2);
        var to = Math.Min(lines.Length - 1, i + 1);
        var ctx = string.Join(" ", lines[from..(to + 1)]).ToLowerInvariant();
        return PrivateDnsWords.Any(ctx.Contains);
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
    /// <summary>Глобальный IPv6 (2000::/3), кроме адресов для документации (2001:db8::/32).</summary>
    public static bool IsPublic6(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        var b = a.GetAddressBytes();
        return (b[0] & 0xE0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
    }

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
