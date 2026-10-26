using System.Net.Http;

namespace Dnex.Services;

/// <summary>
/// Запасные пути к GitHub, когда он недоступен или замедлен провайдером. Через зеркала идут только файлы, которые
/// программа проверяет сама: архив обновления — подписью автора, списки серверов и наборы сайтов — той же подписью,
/// файлы сторонних программ — суммой SHA-256 из GitHub API. Подменить их через зеркало нельзя: проверка не пройдет,
/// и программа возьмет следующий путь. Первым всегда идет прямой адрес.
/// </summary>
public static class GitHubMirrors
{
    /// <summary>Прокси для файлов релизов github.com: адрес релиза дописывается после префикса.</summary>
    internal static string[] ReleaseProxies = { "https://ghfast.top/", "https://gh-proxy.com/", "https://ghproxy.net/" };

    /// <summary>Сколько ждать ответа одного пути, прежде чем пробовать следующий.</summary>
    internal static TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Файл релиза: прямой адрес github.com и он же через прокси.</summary>
    public static IEnumerable<string> ReleaseUrls(string url)
    {
        yield return url;
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var p in ReleaseProxies) yield return p + url;
    }

    /// <summary>
    /// Файл репозитория: raw.githubusercontent.com и тот же файл через jsDelivr (CDN, который раздает репозитории
    /// GitHub). «https://raw.githubusercontent.com/автор/репозиторий/ветка/путь» → «https://cdn.jsdelivr.net/gh/автор/репозиторий@ветка/путь».
    /// </summary>
    public static IEnumerable<string> RawUrls(string url)
    {
        yield return url;
        const string raw = "https://raw.githubusercontent.com/";
        if (!url.StartsWith(raw, StringComparison.OrdinalIgnoreCase)) yield break;
        var parts = url[raw.Length..].Split('/', 4);
        if (parts.Length < 4) yield break;
        var tail = $"{parts[0]}/{parts[1]}@{parts[2]}/{parts[3]}";
        yield return "https://cdn.jsdelivr.net/gh/" + tail;
        yield return "https://fastly.jsdelivr.net/gh/" + tail;
    }

    /// <summary>
    /// Подписанный файл репозитория и его подпись (.sig) с одного и того же пути: сначала raw.githubusercontent.com,
    /// потом jsDelivr. Принимается первая пара, которая прошла проверку valid. Null, если ни один путь не подошел.
    /// jsDelivr кэширует ветку до 12 часов, поэтому список оттуда может быть чуть старее, но подпись у него та же.
    /// </summary>
    public static async Task<(string Body, string Sig)?> SignedRawAsync(HttpClient http, string url, Func<string, string, bool> valid, CancellationToken ct)
    {
        var bodies = RawUrls(url).ToList();
        var sigs = RawUrls(url + ".sig").ToList();
        for (int i = 0; i < bodies.Count; i++)
        {
            try
            {
                var body = await http.GetStringAsync(bodies[i], ct);
                var sig = await http.GetStringAsync(sigs[i], ct);
                if (valid(body, sig)) return (body, sig);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    /// <summary>
    /// Текст по первому ответившему пути. accept — проверка содержимого: прокси иногда отвечает 200 со своей
    /// страницей ошибки, такой ответ пропускается. Если не ответил ни один путь, бросается последняя ошибка.
    /// </summary>
    public static async Task<string> GetStringAsync(HttpClient http, IEnumerable<string> urls, CancellationToken ct, Func<string, bool>? accept = null)
    {
        Exception? last = null;
        foreach (var u in urls)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AttemptTimeout);
            try
            {
                var text = await http.GetStringAsync(u, cts.Token);
                if (accept == null || accept(text)) return text;
                last = new HttpRequestException("ответ не похож на нужный файл");
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { last = e; }
        }
        throw last ?? new HttpRequestException("нет адресов");
    }
}
