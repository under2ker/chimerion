using System.Net;
using System.Net.Http;
using System.Text.Json;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>Снимок состояния сети: что видит пользователь до и после подключения.</summary>
public sealed class NetSnapshot
{
    public DateTime Taken { get; init; } = DateTime.Now;
    public string PublicIp { get; set; } = "—";
    public string Location { get; set; } = "—";
    public string Provider { get; set; } = "—";
    public string SystemDns { get; set; } = "—";
    /// <summary>Сервис → (адрес, на который его резолвит система; доступен ли).</summary>
    public List<ServiceState> Services { get; } = new();
    /// <summary>Дополнительные хосты сервисов (вход, сайт, CDN). Не входят в счетчики «N из M», только в журнал.</summary>
    public List<ServiceState> Hosts { get; } = new();
    /// <summary>Резолверы, которые на самом деле дошли до тестового сервера (тест утечки). Пусто, если тест не запускался.</summary>
    public List<ResolverInfo> Resolvers { get; } = new();
    /// <summary>Откуда ИИ видят соединение сейчас (через DNS системы): адрес, страна, город, владелец сети.</summary>
    public Egress? Egress { get; set; }
    public string? EgressCity { get; set; }
    public string? EgressOrg { get; set; }
}

/// <summary>Состояние сервиса. Access — короткий статус для плитки, Note — причина подробно (подсказка и журнал).</summary>
public sealed record ServiceState(string Name, string Host, string ResolvedIp, bool Proxied, string Access, string? Note = null, Egress? Egress = null);

/// <summary>
/// Smart DNS не меняет внешний IP — это нормально. Подключение видно по другому:
/// адреса ИИ-сервисов в системе меняются на адреса прокси, и сервис перестает отвечать «регион не поддерживается».
/// </summary>
public static class ConnectionProbe
{
    /// <summary>
    /// Новый клиент на каждую проверку. Общий клиент держит открытые соединения: после отключения Smart DNS
    /// запрос шел бы по старому соединению к адресу прокси, и сервис показывался бы доступным.
    /// </summary>
    private static HttpClient NewHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
        })
        { Timeout = TimeSpan.FromSeconds(10) };
        // Сайты за Cloudflare по-разному отвечают запросу без браузерной подписи; так ответ ближе к тому, что видит браузер
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CHIMERION/0.1");
        return http;
    }

    /// <summary>Снимок состояния. leakTest — еще и тест утечки DNS: при подключении, отключении и по кнопке,
    /// но не в проверке по таймеру, чтобы не нагружать чужой тестовый сервис.</summary>
    public static async Task<NetSnapshot> TakeAsync(AppSettings st, CancellationToken ct = default, bool leakTest = false)
    {
        var snap = new NetSnapshot();
        using var http = NewHttp();
        var ipTask = FillPublicIpAsync(snap, http, ct);
        var dnsTask = DnsSwitcher.GetCurrentAsync().ContinueWith(t => snap.SystemDns = t.IsCompletedSuccessfully ? t.Result : "—", ct);

        var targets = st.Targets.Where(t => t.Enabled).ToList();
        var svcTasks = targets.Select(t => ProbeServiceAsync(st, t, http, ct)).ToList();
        // Дополнительные хосты: тот же ответ API-признаков, но имя с хостом, чтобы строка в журнале было понятно
        var hostTasks = targets.SelectMany(t => t.ExtraHosts.Select(h => ProbeServiceAsync(st, new CheckTarget
        {
            Name = $"{t.Name}: {h}", Host = h, Path = "/", BlockedMarkers = t.BlockedMarkers, OwnerOrgs = t.OwnerOrgs,
        }, http, ct, web: true))).ToList();
        var leakTask = leakTest ? LeakTest.RunAsync() : Task.FromResult(new List<ResolverInfo>());
        var egressTask = Task.Run(async () =>
        {
            snap.Egress = await EgressTrace.TraceAsync(null, TimeSpan.FromSeconds(6), ct);
            if (snap.Egress != null)
            {
                var (_, city) = await GeoLocation.LookupAsync(snap.Egress.Ip, ct);
                snap.EgressCity = city;
                snap.EgressOrg = await GeoLocation.OrgAsync(snap.Egress.Ip, ct);
            }
        }, ct);
        await Task.WhenAll(svcTasks.Cast<Task>().Concat(hostTasks).Append(ipTask).Append(dnsTask).Append(leakTask).Append(egressTask));
        snap.Resolvers.AddRange(leakTask.Result);
        snap.Services.AddRange(svcTasks.Select(t => t.Result));
        snap.Hosts.AddRange(hostTasks.Select(t => t.Result));
        return snap;
    }

    private static async Task FillPublicIpAsync(NetSnapshot snap, HttpClient http, CancellationToken ct)
    {
        try
        {
            // ipinfo.io по HTTPS: внешний IP, страна, провайдер. Ничего, кроме самого запроса, не отправляется.
            var json = await http.GetStringAsync("https://ipinfo.io/json", ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Get(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            snap.PublicIp = Get("ip") is { Length: > 0 and < 64 } ip ? ip : "—";
            snap.Location = string.Join(", ", new[] { Get("country"), Get("region"), Get("city") }.Where(x => x.Length > 0));
            snap.Provider = Get("org") is { Length: > 0 } org ? org : "—";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            try { snap.PublicIp = (await http.GetStringAsync("https://api.ipify.org", ct)).Trim(); } catch (Exception) { }
        }
    }

    /// <summary>Читаем не больше 64 КБ: для поиска маркера блокировки этого достаточно, большой ответ не даст ложную ошибку.</summary>
    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        int len = 0, n;
        await using var st = await resp.Content.ReadAsStreamAsync(ct);
        while (len < buf.Length && (n = await st.ReadAsync(buf.AsMemory(len), ct)) > 0) len += n;
        return System.Text.Encoding.UTF8.GetString(buf, 0, len);
    }

    /// <summary>Понятные короткие причины вместо текста исключений .NET.</summary>
    public static string Friendly(Exception e) => e.GetBaseException() switch
    {
        TimeoutException or TaskCanceledException or OperationCanceledException => "нет ответа",
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut => "нет соединения",
        System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused => "соединение отклонено",
        System.Net.Sockets.SocketException => "нет соединения",
        System.Security.Authentication.AuthenticationException => "неверный сертификат",
        HttpRequestException h when h.Message.Contains("ConnectTimeout") => "нет соединения",
        _ => "ошибка соединения",
    };

    private static async Task<ServiceState> ProbeServiceAsync(AppSettings st, CheckTarget t, HttpClient http, CancellationToken ct, bool web = false)
    {
        string resolved = "—";
        bool proxied = false, compared = false;
        try
        {
            // Через системный резолвер — то есть ровно так, как будет работать браузер.
            var ips = await Dns.GetHostAddressesAsync(t.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct);
            resolved = ips.FirstOrDefault()?.ToString() ?? "—";
            try
            {
                var (real, _) = await DnsWire.ResolveDohAsync(st.ReferenceDoh, t.Host, TimeSpan.FromSeconds(6), ct);
                proxied = ips.Length > 0 && !ips.Any(real.Contains);
                // Не совпал с эталоном, но принадлежит самому сервису (у Google много адресов): подмены нет
                if (proxied && await GeoLocation.IsOwnedByAsync(resolved, t.OwnerOrgs, ct)) proxied = false;
                compared = true;
            }
            catch (Exception) { }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { resolved = "не резолвится"; }

        string access;
        string? note = null;
        try
        {
            var result = await ProbeAsync(http, t, web, ct);
            access = result switch
            {
                ServiceAccess.Result.Open => "✓ доступен",
                ServiceAccess.Result.Closed => "✗ доступ закрыт",
                _ => "✗ сервис не отвечает",
            };

            if (!web && result == ServiceAccess.Result.Open)
            {
                // API без ключа отвечает одинаково из закрытого региона и через прокси (Gemini):
                // без подмены адреса доступ не подтвержден
                if (t.RequiresProxy && compared && !proxied)
                {
                    access = "✗ не подтвержден";
                    note = "Без подмены адреса доступ не подтвердить: без ключа API сервис отвечает одинаково из любого региона";
                }
                // Сайт сервиса: если он закрыт (регион, капча Google), в браузере сервисом не пользоваться
                else if (ServerChecker.SplitWebCheck(t) is { } site)
                {
                    try
                    {
                        if (await ProbeAsync(http, site, web: true, ct) == ServiceAccess.Result.Closed)
                        {
                            access = "✗ доступ закрыт";
                            note = $"API отвечает, но сайт {site.Host} закрыт: страница «недоступно в регионе» или капча Google";
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested) { /* сайт не ответил — остается результат API */ }
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { access = "✗ " + Friendly(e); }

        // Откуда сервис видит соединение (только основной адрес сервиса, через DNS системы, как браузер)
        Egress? egress = null;
        if (!web)
        {
            try
            {
                var site = EgressTrace.SiteFor(t);
                var traceHost = EgressTrace.TraceHostFor(t.Name);
                IPAddress? via = null;
                if (site != traceHost)
                    via = (await Dns.GetHostAddressesAsync(site, System.Net.Sockets.AddressFamily.InterNetwork, ct)).FirstOrDefault();
                if (site == traceHost || via != null)
                    egress = await EgressTrace.TraceAsync(via, TimeSpan.FromSeconds(6), ct, traceHost);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        return new ServiceState(t.Name, t.Host, resolved, proxied, access, note, egress);
    }

    /// <summary>Один запрос к API или сайту и вердикт по ответу. Для сайта учитывается адрес редиректа.</summary>
    private static async Task<ServiceAccess.Result> ProbeAsync(HttpClient http, CheckTarget t, bool web, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"https://{t.Host}{t.Path}", HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await ReadLimitedAsync(resp, ct);
        int code = (int)resp.StatusCode;
        return web ? ServiceAccess.ClassifyWeb(t, code, body, resp.Headers.Location?.ToString())
                   : ServiceAccess.Classify(t, code, body);
    }
}
