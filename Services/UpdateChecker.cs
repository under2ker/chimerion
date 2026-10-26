using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>Новая версия из GitHub: номер, описание и страница релиза. Установку делает Updater.</summary>
/// <summary>Hotfix — версия пришла из канала быстрых обновлений (Cloudflare R2), а не из релиза GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, HotfixChannel.Manifest? Hotfix = null);

public enum UpdateStatus { UpToDate, Available, Failed }

/// <summary>Итог проверки: последняя версия, есть новая или проверка не удалась. Reason — почему не удалась.</summary>
public sealed record UpdateCheck(UpdateStatus Status, UpdateInfo? Info, Version? Latest, string? Reason = null);

/// <summary>
/// Проверка наличия новой версии. Только чтение данных о релизе из GitHub API по HTTPS.
/// Сама проверка ничего не скачивает: обновление ставится только по кнопке «Обновить» (см. Updater).
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "under2ker/chimerion";

    // internal и не readonly: сценарные тесты подставляют клиентов с поддельными ответами GitHub
    internal static HttpClient Http = CreateClient();
    // Для страниц сайта GitHub: без заголовков API и без перехода по перенаправлению (номер версии берется из него)
    internal static HttpClient Web = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    // jsDelivr: список версий репозитория, когда GitHub недоступен целиком
    internal static HttpClient Cdn = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Версия программы из три части (0.1.16), без служебного четвертого числа сборки.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-update-check/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Запрашивает последний релиз. Null, если новой версии нет или проверка не удалась (для фоновой проверки).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var r = await CheckDetailedAsync(ct);
        return r.Status == UpdateStatus.Available ? r.Info : null;
    }

    /// <summary>
    /// Полная проверка с итогом. Три пути по очереди:
    /// 1) GitHub API — описание и страница релиза. Без входа API дает 60 запросов в час на IP-адрес, и за общим
    ///    адресом провайдера лимит бывает исчерпан, хотя сам GitHub открывается;
    /// 2) лента релизов releases.atom — без лимита API, тоже с описанием;
    /// 3) перенаправление releases/latest на страницу последнего тега — только номер версии.
    /// Неудачей считается, только если не сработал ни один путь; Reason объясняет почему.
    /// </summary>
    public static async Task<UpdateCheck> CheckDetailedAsync(CancellationToken ct = default)
    {
        // GitHub (большие релизы) и R2 (быстрые исправления) спрашиваются одновременно, побеждает более новая версия
        var hotTask = HotfixChannel.CheckAsync(ct);
        var gh = await CheckGitHubAsync(ct);
        var hot = await hotTask;
        return Combine(gh, hot, CurrentVersion);
    }

    /// <summary>
    /// Итог по двум каналам. Быстрое исправление берется, если оно новее и программы, и последнего релиза GitHub.
    /// Если GitHub не ответил, а R2 ответил, итог все равно есть: обновление или «последняя версия».
    /// </summary>
    internal static UpdateCheck Combine(UpdateCheck gh, HotfixChannel.Manifest? hot, Version current)
    {
        if (hot?.ParsedVersion is not { } hv) return gh;
        var ghLatest = gh.Latest ?? new Version(0, 0, 0);
        if (hv > current && hv > ghLatest)
        {
            var notes = string.IsNullOrWhiteSpace(hot.Notes) ? "Срочное исправление." : hot.Notes.Trim();
            return new UpdateCheck(UpdateStatus.Available,
                new UpdateInfo(hv, notes, $"https://github.com/{Repo}/blob/main/CHANGELOG.md", hot), hv);
        }
        if (gh.Status == UpdateStatus.Failed)
            return new UpdateCheck(UpdateStatus.UpToDate, null, hv > current ? hv : current);
        return gh;
    }

    private static async Task<UpdateCheck> CheckGitHubAsync(CancellationToken ct)
    {
        string reason;
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                var info = Parse(json, CurrentVersion);
                if (info != null) return new UpdateCheck(UpdateStatus.Available, info, info.Version);
                return new UpdateCheck(UpdateStatus.UpToDate, null, TagVersion(json));
            }
            bool limit = (int)resp.StatusCode is 403 or 429
                && resp.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0";
            reason = limit ? "GitHub временно ограничил запросы с вашего адреса" : $"GitHub ответил кодом {(int)resp.StatusCode}";
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { reason = Friendly(e); }

        try
        {
            var atom = await Web.GetStringAsync($"https://github.com/{Repo}/releases.atom", ct);
            if (ParseAtom(atom, CurrentVersion) is { } r) return r;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* пробуем последний путь */ }

        try
        {
            using var resp = await Web.GetAsync($"https://github.com/{Repo}/releases/latest", ct);
            var tag = resp.Headers.Location?.ToString().TrimEnd('/').Split('/').LastOrDefault() ?? "";
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                var page = $"https://github.com/{Repo}/releases/tag/{tag}";
                return latest > CurrentVersion
                    ? new UpdateCheck(UpdateStatus.Available, new UpdateInfo(latest, "Описание версии на странице релиза.", page), latest)
                    : new UpdateCheck(UpdateStatus.UpToDate, null, latest);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { reason += "; сайт GitHub: " + Friendly(e); }

        // 4) GitHub недоступен целиком: номер последней версии из jsDelivr (он раздает теги репозитория). Только номер:
        //    сама установка все равно проверяет подпись автора, а скачать архив поможет зеркало (GitHubMirrors)
        try
        {
            var json = await Cdn.GetStringAsync($"https://data.jsdelivr.com/v1/packages/gh/{Repo}", ct);
            if (CdnLatest(json) is { } latest)
            {
                var page = $"https://github.com/{Repo}/releases/tag/v{latest.ToString(3)}";
                return latest > CurrentVersion
                    ? new UpdateCheck(UpdateStatus.Available, new UpdateInfo(latest, "Описание версии на странице релиза.", page), latest)
                    : new UpdateCheck(UpdateStatus.UpToDate, null, latest);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* и jsDelivr недоступен */ }

        return new UpdateCheck(UpdateStatus.Failed, null, null, reason);
    }

    /// <summary>Самая новая версия из ответа jsDelivr («versions»: [{ "version": "0.1.26" }, ...]). Null, если не разобрать.</summary>
    internal static Version? CdnLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("versions", out var vs) || vs.ValueKind != JsonValueKind.Array) return null;
            return vs.EnumerateArray()
                .Select(v => v.TryGetProperty("version", out var x) ? x.GetString() : null)
                .Select(t => Version.TryParse(t?.TrimStart('v', 'V'), out var ver) && ver.Build >= 0 ? ver : null)
                .OfType<Version>().DefaultIfEmpty().Max();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Понятная причина вместо текста исключения .NET.</summary>
    private static string Friendly(Exception e) => e.GetBaseException() switch
    {
        TaskCanceledException or TimeoutException => "GitHub не ответил за 15 секунд",
        System.Net.Sockets.SocketException => "нет соединения с GitHub",
        System.Security.Authentication.AuthenticationException => "ошибка защищенного соединения с GitHub",
        HttpRequestException h when h.StatusCode is { } c => $"GitHub ответил кодом {(int)c}",
        _ => "нет связи с GitHub",
    };

    /// <summary>
    /// Лента releases.atom: самая новая версия среди записей (номер из ссылки …/releases/tag/vX.Y.Z),
    /// описание из содержимого записи без HTML. Null, если номеров в ленте нет.
    /// </summary>
    public static UpdateCheck? ParseAtom(string xml, Version current)
    {
        System.Xml.Linq.XNamespace a = "http://www.w3.org/2005/Atom";
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var best = doc.Descendants(a + "entry")
            .Select(e =>
            {
                var href = e.Elements(a + "link").Select(l => (string?)l.Attribute("href")).FirstOrDefault(h => h != null) ?? "";
                var tag = href.TrimEnd('/').Split('/').LastOrDefault() ?? "";
                return (Ok: Version.TryParse(tag.TrimStart('v', 'V'), out var v), V: v, Href: href,
                        Notes: (string?)e.Element(a + "content") ?? "");
            })
            .Where(x => x.Ok && x.V != null)
            .OrderByDescending(x => x.V)
            .FirstOrDefault();
        if (best.V == null) return null;
        if (best.V <= current) return new UpdateCheck(UpdateStatus.UpToDate, null, best.V);
        var page = IsGitHubPage(best.Href) ? best.Href : $"https://github.com/{Repo}/releases/latest";
        var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
            best.Notes.Replace("</li>", "\n").Replace("</p>", "\n").Replace("<br>", "\n"), "<[^>]+>", ""));
        return new UpdateCheck(UpdateStatus.Available, new UpdateInfo(best.V, text.Trim(), page), best.V);
    }

    private static Version? TagVersion(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        return Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;
    }

    /// <summary>Разбирает ответ GitHub API. Возвращает данные только если релиз новее текущей версии.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        if (latest <= current) return null;

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";
        if (!IsGitHubPage(page)) page = $"https://github.com/{Repo}/releases/latest";
        return new UpdateInfo(latest, notes, page);
    }

    private static bool IsGitHubPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com";
}
