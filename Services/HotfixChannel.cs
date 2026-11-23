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
///
/// Кроме R2 есть свои зеркала (MirrorUrls): свой сервер в России (ru.chimerion.dev) и GitVerse Pages, куда выкладываются те же файлы. В России Cloudflare
/// и GitHub бывают замедлены провайдером, а свой сервер внутри страны отвечает. Все источники спрашиваются сразу,
/// берется самая новая версия с верной подписью, а качается она сначала с того источника, что ответил быстрее.
/// </summary>
public static class HotfixChannel
{
    /// <summary>Публичный адрес бакета R2. Пустой — канал выключен (сборки без настроенного бакета).</summary>
    internal static string BaseUrl = "https://pub-6327ca42c2344c2ab42e1c16868d6c5a.r2.dev";

    /// <summary>
    /// Свои зеркала с теми же файлами, что в R2 (latest.json, архивы и подписи), плюс файлы релизов GitHub под теми
    /// же именами. Выкладывают их Служебное\Подпись\ru-mirror.ps1 (сервер Time-Host) и gitverse-mirror.ps1
    /// (ветка pages репозитория gitverse.ru/under2ker/chimerion), оба вызываются из hotfix.ps1 и release.ps1.
    /// </summary>
    internal static string[] MirrorUrls = { "https://ru.chimerion.dev", "https://under2ker.gitverse.site/chimerion" };

    /// <summary>Непустые адреса зеркал без косой черты в конце.</summary>
    internal static IEnumerable<string> Mirrors => MirrorUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.TrimEnd('/'));

    /// <summary>Все источники быстрого канала: R2 и зеркала.</summary>
    internal static IEnumerable<string> Sources => new[] { BaseUrl }.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.TrimEnd('/')).Concat(Mirrors).Distinct();

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

        /// <summary>Источники, где лежит именно эта версия, первым ответивший быстрее. Не из файла: заполняет CheckDetailedAsync.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public List<string> From { get; set; } = new();

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
    public static async Task<Manifest?> CheckAsync(CancellationToken ct) => (await CheckDetailedAsync(ct)).Manifest;

    /// <summary>
    /// То же с причиной: None (канал выключен или все хорошо), Unreachable (ни один источник не ответил),
    /// BadSignature (манифест получен, но подпись не сошлась, а верного нет ни у кого).
    /// </summary>
    public static async Task<(Manifest? Manifest, UpdateFailure Problem)> CheckDetailedAsync(CancellationToken ct)
    {
        var sources = Sources.ToList();
        if (sources.Count == 0) return (null, UpdateFailure.None);
        // Все источники сразу; порядок ответов запоминается, чтобы качать с самого быстрого
        var answered = new List<(string Base, Manifest? M, UpdateFailure P)>();
        var pending = sources.Select(b => CheckOneAsync(b, ct)).ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            answered.Add(await done);
        }
        ct.ThrowIfCancellationRequested();
        var best = answered.Where(a => a.M != null).Select(a => a.M!).OrderByDescending(m => m.ParsedVersion).FirstOrDefault();
        if (best == null)
            return (null, answered.Any(a => a.P == UpdateFailure.BadSignature) ? UpdateFailure.BadSignature
                        : answered.Any(a => a.P == UpdateFailure.None) ? UpdateFailure.None : UpdateFailure.Unreachable);
        best.From = answered.Where(a => a.M?.ParsedVersion == best.ParsedVersion).Select(a => a.Base).ToList();
        return (best, UpdateFailure.None);
    }

    /// <summary>Манифест одного источника: Unreachable (не ответил), BadSignature (подпись не сошлась) или манифест.</summary>
    private static async Task<(string Base, Manifest? M, UpdateFailure P)> CheckOneAsync(string baseUrl, CancellationToken ct)
    {
        string json, sig;
        try
        {
            json = await Http.GetStringAsync($"{baseUrl}/{ManifestFile}", ct);
            sig = await Http.GetStringAsync($"{baseUrl}/{ManifestFile}.sig", ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return (baseUrl, null, UpdateFailure.Unreachable); }
        if (!Updater.VerifySignature(System.Text.Encoding.UTF8.GetBytes(json), sig, Updater.TrustedKey))
            return (baseUrl, null, UpdateFailure.BadSignature);
        return (baseUrl, Parse(json, sig, Updater.TrustedKey), UpdateFailure.None);
    }

    /// <summary>Где брать архив: источники с этой версией (быстрый первым), затем остальные на случай, если тот оборвется.</summary>
    private static IEnumerable<string> BasesFor(Manifest m) => m.From.Concat(Sources).Distinct();

    public static string[] ZipUrls(Manifest m) => BasesFor(m).Select(b => $"{b}/{m.Zip}").ToArray();
    public static string[] SigUrls(Manifest m) => BasesFor(m).Select(b => $"{b}/{m.Zip}.sig").ToArray();
}
