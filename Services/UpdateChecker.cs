using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>Новая версия из GitHub: номер, описание и страница релиза. Ничего не скачивается.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl);

/// <summary>
/// Проверка наличия новой версии. Только чтение данных о релизе из GitHub API по HTTPS.
/// Файлы программы не загружаются и не заменяются: пользователь скачивает версию сам.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "under2ker/dnex-dns-hub";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DNEX-update-check/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Запрашивает последний релиз. Null, если сеть недоступна, релиз не найден или версия не новее.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            return Parse(json, CurrentVersion);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // нет сети или GitHub недоступен: молча пропускаем, это не ошибка программы
        }
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
