using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Dnex.Services;

/// <summary>Откуда ИИ-сервис видит соединение: адрес и страна выхода в интернет.</summary>
public sealed record Egress(string Ip, string Country);

/// <summary>
/// Настоящий выход в интернет, а не адрес прокси из DNS. Некоторые Smart DNS (например, keyoneday) отвечают адресом
/// своего сервера в России, а дальше ведут трафик через другой узел (Cloudflare WARP во Франции). ChatGPT видит
/// именно этот узел. Служебная страница Cloudflare /cdn-cgi/trace на chatgpt.com возвращает адрес и страну,
/// с которых пришел запрос, — то же, что видит сам ChatGPT.
/// </summary>
public static class EgressTrace
{
    public const string Host = "chatgpt.com";

    /// <summary>
    /// Сайт за Cloudflare, на котором спрашивать trace для сервиса. ChatGPT и Claude сами за Cloudflare. У Gemini (Google)
    /// такой страницы нет: выход меряем запросом к chatgpt.com через прокси, который сервер выдал для Gemini, —
    /// так видно, откуда выходит именно эта машина. Если прокси пускает только адреса Google, выход не определится.
    /// </summary>
    public static string TraceHostFor(string service) => service switch
    {
        "Claude" => "claude.ai",
        _ => Host,
    };

    /// <summary>Адрес сайта сервиса, чей прокси меряем: сайт из WebCheck (chatgpt.com, claude.ai, gemini.google.com) или API.</summary>
    public static string SiteFor(Dnex.Models.CheckTarget t)
    {
        var w = t.WebCheck?.Trim() ?? "";
        var host = w.Split('/')[0];
        return Uri.CheckHostName(host) == UriHostNameType.Dns ? host : t.Host;
    }

    /// <summary>
    /// Запрос через указанный адрес (via — адрес прокси, который выдал сервер) или через DNS системы (via = null).
    /// Null, если страница не ответила или ответ не похож на trace Cloudflare.
    /// </summary>
    public static async Task<Egress?> TraceAsync(IPAddress? via, TimeSpan timeout, CancellationToken ct, string host = Host)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = timeout };
        if (via != null)
            handler.ConnectCallback = async (_, c) =>
            {
                var sock = new Socket(via.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await sock.ConnectAsync(new IPEndPoint(via, 443), c); return new NetworkStream(sock, ownsSocket: true); }
                catch { sock.Dispose(); throw; }
            };
        using var http = new HttpClient(handler) { Timeout = timeout + timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DNEX/0.1");
        try
        {
            var text = await http.GetStringAsync($"https://{host}/cdn-cgi/trace", ct);
            return Parse(text);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Строки вида «ip=104.28.196.54» и «loc=FR». Null, если адреса или страны нет.</summary>
    public static Egress? Parse(string text)
    {
        string? ip = null, loc = null;
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("ip=")) ip = l[3..];
            else if (l.StartsWith("loc=")) loc = l[4..];
        }
        if (ip == null || !IPAddress.TryParse(ip, out _) || loc is not { Length: 2 } || loc == "XX") return null;
        return new Egress(ip, loc.ToUpperInvariant());
    }
}
