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
}

public sealed record ServiceState(string Name, string Host, string ResolvedIp, bool Proxied, string Access);

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
    private static HttpClient NewHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(6),
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<NetSnapshot> TakeAsync(AppSettings st, CancellationToken ct = default)
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
            Name = $"{t.Name}: {h}", Host = h, Path = "/", BlockedMarkers = t.BlockedMarkers,
        }, http, ct, web: true))).ToList();
        await Task.WhenAll(svcTasks.Cast<Task>().Concat(hostTasks).Append(ipTask).Append(dnsTask));
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
        bool proxied = false;
        try
        {
            // Через системный резолвер — то есть ровно так, как будет работать браузер.
            var ips = await Dns.GetHostAddressesAsync(t.Host, System.Net.Sockets.AddressFamily.InterNetwork, ct);
            resolved = ips.FirstOrDefault()?.ToString() ?? "—";
            try
            {
                var (real, _) = await DnsWire.ResolveDohAsync(st.ReferenceDoh, t.Host, TimeSpan.FromSeconds(6), ct);
                proxied = ips.Length > 0 && !ips.Any(real.Contains);
            }
            catch (Exception) { }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { resolved = "не резолвится"; }

        string access;
        try
        {
            using var resp = await http.GetAsync($"https://{t.Host}{t.Path}", HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await ReadLimitedAsync(resp, ct);
            int code = (int)resp.StatusCode;
            access = (web ? ServiceAccess.ClassifyWeb(t, code, body) : ServiceAccess.Classify(t, code, body)) switch
            {
                ServiceAccess.Result.Open => "✓ доступен",
                ServiceAccess.Result.Closed => "✗ доступ закрыт",
                _ => "✗ сервис не отвечает",
            };
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { access = "✗ " + Friendly(e); }

        return new ServiceState(t.Name, t.Host, resolved, proxied, access);
    }
}
