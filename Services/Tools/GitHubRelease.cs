using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dnex.Services.Tools;

/// <summary>Файл релиза: имя, ссылка и SHA-256 из GitHub (поле digest).</summary>
public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

/// <summary>Последний релиз репозитория: тег, версия из тега и файлы.</summary>
public sealed record Release(string Tag, Version Version, IReadOnlyList<ReleaseAsset> Assets, string PageUrl);

/// <summary>
/// Релизы сторонних инструментов на GitHub. Для каждого файла GitHub API отдает SHA-256: файл ставится,
/// только если скачанное совпало с ней. Без суммы (API недоступен или не отдал ее) установка не делается.
/// </summary>
public static class GitHubRelease
{
    private static readonly HttpClient Api = CreateApi();
    private static readonly HttpClient Download = CreateDownload();

    private static HttpClient CreateApi()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-tools/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static HttpClient CreateDownload()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-tools/1.0");
        return c;
    }

    /// <summary>Номер версии из тега: «v1.11.2» и «1.10.4» дают 1.11.2 и 1.10.4.</summary>
    public static Version? ParseTag(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] == '.')) end++;
        var num = t[..end].Trim('.');
        if (num.Length > 0 && !num.Contains('.')) num += ".0"; // тег из одного числа («v72»): Version требует хотя бы два
        return Version.TryParse(num, out var v) ? v : null;
    }

    public static async Task<Release> LatestAsync(string repo, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await Api.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { throw new ToolException("GitHub не отвечает"); }
        using (resp)
        {
            if ((int)resp.StatusCode is 403 or 429)
                throw new ToolException("GitHub временно ограничил запросы с вашего адреса. Попробуйте через час");
            if (!resp.IsSuccessStatusCode) throw new ToolException($"GitHub ответил {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var ver = ParseTag(tag) ?? throw new ToolException($"Не понял номер версии «{tag}»");
            var assets = new List<ReleaseAsset>();
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToUpperInvariant() : null;
                assets.Add(new ReleaseAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? "", sha));
            }
            var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{repo}/releases/latest";
            return new Release(tag, ver, assets, page);
        }
    }

    /// <summary>Скачивает файл во временную папку и сверяет SHA-256. Возвращает путь к проверенному файлу.</summary>
    public static async Task<string> DownloadVerifiedAsync(ReleaseAsset asset, IProgress<string> progress, CancellationToken ct)
    {
        if (asset.Sha256 is not { Length: 64 })
            throw new ToolException("GitHub не дал контрольную сумму файла, без нее установка не делается");
        var dir = Path.Combine(Path.GetTempPath(), "CHIMERION-tools");
        Directory.CreateDirectory(dir);
        // Имя приходит из ответа API: берем только имя файла, чтобы «..\» в нем не вывел запись за временную папку
        var name = Path.GetFileName(asset.Name);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ToolException($"Некорректное имя файла в релизе: {asset.Name}");
        var path = Path.Combine(dir, name);
        // Прямой адрес, потом зеркала (GitHubMirrors): файл принимается, только если совпала сумма из GitHub API
        Exception? lastError = null;
        bool mismatch = false;
        foreach (var url in Dnex.Services.GitHubMirrors.ReleaseUrls(asset.Url))
        {
            try { await DownloadAsync(url, path, progress, ct); }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not ToolException) { lastError = e; continue; }
            progress.Report("Проверяю контрольную сумму…");
            string actual;
            await using (var fs = File.OpenRead(path))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (actual == asset.Sha256) return path;
            mismatch = true;
        }
        try { File.Delete(path); } catch (Exception) { }
        throw mismatch
            ? new ToolException("Контрольная сумма не совпала, файл не установлен")
            : new ToolException("Не удалось скачать файл: " + lastError?.GetBaseException().Message);
    }

    private static async Task DownloadAsync(string url, string path, IProgress<string> progress, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Dnex.Services.GitHubMirrors.AttemptTimeout);
        using var resp = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        cts.CancelAfter(Timeout.InfiniteTimeSpan);
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        long done = 0, shown = 0;
        int n;
        while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
            done += n;
            if (done - shown > 256 * 1024 || done == total)
            {
                shown = done;
                progress.Report(total > 0 ? $"Скачиваю: {done * 100 / total.Value}%" : $"Скачиваю: {done / 1024} КБ");
            }
        }
    }
}

/// <summary>Понятная причина, почему инструмент не установился, не включился или не обновился.</summary>
public sealed class ToolException(string message) : Exception(message);
