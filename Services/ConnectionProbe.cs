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
}

public sealed record ServiceState(string Name, string Host, string ResolvedIp, bool Proxied, string Access);

/// <summary>
/// Smart DNS не меняет внешний IP — это нормально. Подключение видно по другому:
/// адреса ИИ-сервисов в системе меняются на адреса прокси, и сервис перестаёт отвечать «регион не поддерживается».
/// </summary>
public static class ConnectionProbe
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(6),
    })
    { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 256 * 1024 };

    public static async Task<NetSnapshot> TakeAsync(AppSettings st, CancellationToken ct = default)
    {
        var snap = new NetSnapshot();
        var ipTask = FillPublicIpAsync(snap, ct);
        var dnsTask = DnsSwitcher.GetCurrentAsync().ContinueWith(t => snap.SystemDns = t.IsCompletedSuccessfully ? t.Result : "—", ct);

        var targets = st.Targets.Where(t => t.Enabled).ToList();
        var svcTasks = targets.Select(t => ProbeServiceAsync(st, t, ct)).ToList();
        await Task.WhenAll(svcTasks.Cast<Task>().Append(ipTask).Append(dnsTask));
        snap.Services.AddRange(svcTasks.Select(t => t.Result));
        return snap;
    }

    private static async Task FillPublicIpAsync(NetSnapshot snap, CancellationToken ct)
    {
        try
        {
            // ipinfo.io по HTTPS: внешний IP, страна, провайдер. Ничего, кроме самого запроса, не отправляется.
            var json = await Http.GetStringAsync("https://ipinfo.io/json", ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Get(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            snap.PublicIp = Get("ip") is { Length: > 0 and < 64 } ip ? ip : "—";
            snap.Location = string.Join(", ", new[] { Get("country"), Get("region"), Get("city") }.Where(x => x.Length > 0));
            snap.Provider = Get("org") is { Length: > 0 } org ? org : "—";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            try { snap.PublicIp = (await Http.GetStringAsync("https://api.ipify.org", ct)).Trim(); } catch (Exception) { }
        }
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

    private static async Task<ServiceState> ProbeServiceAsync(AppSettings st, CheckTarget t, CancellationToken ct)
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
            using var resp = await Http.GetAsync($"https://{t.Host}{t.Path}", HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (body.Length > 64 * 1024) body = body[..(64 * 1024)];
            int code = (int)resp.StatusCode;
            access = t.BlockedStatusCodes.Contains(code) || t.BlockedMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase))
                ? "✗ регион закрыт" : "✓ доступен";
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { access = "✗ " + Friendly(e); }

        return new ServiceState(t.Name, t.Host, resolved, proxied, access);
    }
}
