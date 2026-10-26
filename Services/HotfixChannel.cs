using System.Net.Http;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>
/// Быстрые обновления через Cloudflare R2. На GitHub выходят большие релизы со всеми исправлениями, а срочное
/// исправление одной функции выкладывается в R2 сразу, без ожидания следующего релиза. В бакете лежат:
/// latest.json (номер версии, описание, имя архива, SHA-256), latest.json.sig и сам архив с подписью.
///
/// Доверие то же, что у релизов GitHub: манифест и архив подписаны ключом автора, без верной подписи манифест
/// не принимается, а архив не ставится. Подмена файлов в бакете или по дороге ничего не даст.
/// </summary>
public static class HotfixChannel
{
    /// <summary>Публичный адрес бакета R2. Пустой — канал выключен (сборки без настроенного бакета).</summary>
    internal static string BaseUrl = "https://pub-6327ca42c2344c2ab42e1c16868d6c5a.r2.dev";

    public const string ManifestFile = "latest.json";

    internal static HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-hotfix/1.0");
        c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return c;
    }

    /// <summary>Манифест быстрого обновления.</summary>
    public sealed class Manifest
    {
        public string Version { get; set; } = "";
        public string Notes { get; set; } = "";
        /// <summary>Имя архива в бакете (CHIMERION-x.y.z-win-x64.zip).</summary>
        public string Zip { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Date { get; set; } = "";

        public Version? ParsedVersion => System.Version.TryParse(Version, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Разбор манифеста с проверкой подписи. Null, если подпись не сошлась или манифест неполный.</summary>
    public static Manifest? Parse(string json, string sig, string publicKey)
    {
        if (!Updater.VerifySignature(System.Text.Encoding.UTF8.GetBytes(json), sig, publicKey)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(json, Json);
            if (m?.ParsedVersion == null) return null;
            // Имя архива только своего вида, без путей: из него собирается адрес скачивания
            if (!System.Text.RegularExpressions.Regex.IsMatch(m.Zip, @"^CHIMERION-\d+\.\d+\.\d+-win-x64\.zip$")) return null;
            if (m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit)) return null;
            m.Sha256 = m.Sha256.ToUpperInvariant();
            return m;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Последнее быстрое обновление. Null, если канал выключен, бакет недоступен или подпись не сошлась.</summary>
    public static async Task<Manifest?> CheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(BaseUrl)) return null;
        try
        {
            var json = await Http.GetStringAsync($"{BaseUrl}/{ManifestFile}", ct);
            var sig = await Http.GetStringAsync($"{BaseUrl}/{ManifestFile}.sig", ct);
            return Parse(json, sig, Updater.TrustedKey);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    public static string ZipUrl(Manifest m) => $"{BaseUrl}/{m.Zip}";
    public static string SigUrl(Manifest m) => $"{BaseUrl}/{m.Zip}.sig";
}
