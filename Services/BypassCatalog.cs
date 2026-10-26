using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>
/// Готовые наборы сайтов для обхода zapret (catalog/bypass.json): Meta, X, YouTube, Discord, Telegram и другие,
/// со всеми их доменами и CDN. Собираются автором из v2fly/domain-list-community (лицензия MIT) и подписываются
/// ключом обновлений. Свежие наборы берутся с GitHub (не чаще раза в сутки), без сети последние скачанные,
/// а если их нет, встроенная в программу копия. Без верной подписи наборы не принимаются.
/// </summary>
public static class BypassCatalog
{
    public const string Url = "https://raw.githubusercontent.com/under2ker/chimerion/main/catalog/bypass.json";
    private const string CacheFile = "bypass.json";
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(24);

    public sealed class Group
    {
        public string Id { get; set; } = "";
        /// <summary>Раздел: «Программирование», «Графика и дизайн», «ИИ и LLM» и другие.</summary>
        public string Category { get; set; } = "";
        /// <summary>Чем лучше открывать: «zapret» (сайт режет провайдер) или «smartdns» (сервис сам закрылся для России).</summary>
        public string Recommend { get; set; } = "";
        public string Title { get; set; } = "";
        public string Note { get; set; } = "";
        public List<string> Domains { get; set; } = new();
    }

    /// <summary>Наборы прежних версий, объединенные в новые: выбор пользователя переносится сам.</summary>
    public static readonly Dictionary<string, string> Renamed = new()
    {
        ["proton"] = "knowledge", ["speedtest"] = "knowledge", ["archive"] = "knowledge",
    };

    /// <summary>Выбранные наборы с учетом переименований.</summary>
    public static HashSet<string> Normalize(IEnumerable<string> ids) =>
        ids.Select(i => Renamed.TryGetValue(i, out var n) ? n : i).ToHashSet();

    /// <summary>Домены выбранных наборов (без повторов).</summary>
    public static IEnumerable<string> DomainsOf(Data? catalog, IEnumerable<string> ids)
    {
        if (catalog == null) return Array.Empty<string>();
        var set = Normalize(ids);
        return catalog.Groups.Where(g => set.Contains(g.Id)).SelectMany(g => g.Domains).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class Data
    {
        public string Updated { get; set; } = "";
        public string Source { get; set; } = "";
        public List<Group> Groups { get; set; } = new();
    }

    private sealed class Cache
    {
        public DateTime Fetched { get; set; }
        public string Json { get; set; } = "";
        public string Sig { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static Data? Parse(string json, string sig, string publicKey = Updater.PublicKey)
    {
        if (!Updater.VerifySignature(System.Text.Encoding.UTF8.GetBytes(json), sig, publicKey)) return null;
        try { return JsonSerializer.Deserialize<Data>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Без сети: последние скачанные наборы или встроенная копия.</summary>
    public static Data? LoadCached()
    {
        try
        {
            var path = Storage.PathOf(CacheFile);
            if (File.Exists(path) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(path), Storage.Options) is { } c && Parse(c.Json, c.Sig) is { } d)
                return d;
        }
        catch (Exception) { }
        return Embedded();
    }

    /// <summary>Свежие наборы с GitHub, если пора обновить; иначе последние скачанные или встроенные.</summary>
    public static async Task<Data?> LoadAsync(CancellationToken ct = default)
    {
        Cache? cache = null;
        try
        {
            var path = Storage.PathOf(CacheFile);
            if (File.Exists(path)) cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(path), Storage.Options);
        }
        catch (Exception) { }
        if (cache == null || DateTime.Now - cache.Fetched > Refresh)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-bypass/1.0");
                // GitHub недоступен: тот же подписанный файл через jsDelivr (см. GitHubMirrors)
                if (await GitHubMirrors.SignedRawAsync(http, Url, (j, s) => Parse(j, s) != null, ct) is var (json, sig))
                {
                    cache = new Cache { Fetched = DateTime.Now, Json = json, Sig = sig };
                    try { Storage.Save(CacheFile, cache); } catch (Exception) { }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* без сети или файла еще нет на GitHub */ }
        }
        return cache != null && Parse(cache.Json, cache.Sig) is { } d ? d : Embedded();
    }

    /// <summary>Копия наборов, встроенная в программу при сборке (catalog/bypass.json и подпись).</summary>
    private static Data? Embedded()
    {
        try
        {
            var asm = typeof(BypassCatalog).Assembly;
            using var js = asm.GetManifestResourceStream("bypass.json");
            using var ss = asm.GetManifestResourceStream("bypass.json.sig");
            if (js == null || ss == null) return null;
            // Подпись над точными байтами файла: читаем как есть, без перекодирования
            using var ms = new MemoryStream();
            js.CopyTo(ms);
            var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            return Parse(json, new StreamReader(ss).ReadToEnd());
        }
        catch (Exception) { return null; }
    }
}
