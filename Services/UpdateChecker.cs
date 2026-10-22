using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>Новая версия из GitHub: номер, описание и страница релиза. Ничего не скачивается.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl);

public enum UpdateStatus { UpToDate, Available, Failed }

/// <summary>Итог проверки: последняя версия, есть новая или проверка не удалась.</summary>
public sealed record UpdateCheck(UpdateStatus Status, UpdateInfo? Info, Version? Latest);

/// <summary>
/// Проверка наличия новой версии. Только чтение данных о релизе из GitHub API по HTTPS.
/// Файлы программы не загружаются и не заменяются: пользователь скачивает версию сам.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "under2ker/dnex-dns-hub";

    private static readonly HttpClient Http = CreateClient();

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
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DNEX-update-check/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Запрашивает последний релиз. Null, если новой версии нет или проверка не удалась (для фоновой проверки).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var r = await CheckDetailedAsync(ct);
        return r.Status == UpdateStatus.Available ? r.Info : null;
    }

    /// <summary>Полная проверка с итогом: для кнопки «Проверить обновление» нужно знать, что именно ответил GitHub.</summary>
    public static async Task<UpdateCheck> CheckDetailedAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return new UpdateCheck(UpdateStatus.Failed, null, null);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var info = Parse(json, CurrentVersion);
            if (info != null) return new UpdateCheck(UpdateStatus.Available, info, info.Version);
            return new UpdateCheck(UpdateStatus.UpToDate, null, TagVersion(json));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheck(UpdateStatus.Failed, null, null); // нет сети или GitHub недоступен
        }
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
