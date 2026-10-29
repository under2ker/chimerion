using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Стартовый список серверов из репозитория CHIMERION (catalog/servers.json). Автор собирает его из серверов, которые
/// у него стабильно работают, и подписывает тем же ключом, что и обновления. Новичку не нужно ждать поиск:
/// серверы появляются сразу и проверяются как обычно. Список без верной подписи не принимается.
/// Скачивается не чаще раза в 12 часов, последний принятый список хранится в catalog.json для запуска без сети.
/// </summary>
public static class RemoteCatalog
{
    public const string Url = "https://raw.githubusercontent.com/under2ker/chimerion/main/catalog/servers.json";
    private const string CacheFile = "catalog.json";
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(12);

    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public string? Primary { get; set; }
        public string? Secondary { get; set; }
        public string? Doh { get; set; }
        public string? Site { get; set; }
    }

    public sealed class Catalog
    {
        public string Updated { get; set; } = "";
        public string? Expires { get; set; }
        public List<Entry> Servers { get; set; } = new();
    }

    private sealed class Cache
    {
        public DateTime Fetched { get; set; }
        public string Json { get; set; } = "";
        public string Sig { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true,
    };

    public static string Serialize(Catalog c) => JsonSerializer.Serialize(c, Json);

    /// <summary>Разбор списка с проверкой подписи. Null, если подпись не сошлась или JSON не читается.</summary>
    public static Catalog? Parse(string json, string sig, string publicKey = Updater.PublicKey)
    {
        if (!Updater.VerifySignature(System.Text.Encoding.UTF8.GetBytes(json), sig, publicKey)) return null;
        try
        {
            var catalog = JsonSerializer.Deserialize<Catalog>(json, Json);
            return catalog != null && IsCurrent(catalog, DateTime.UtcNow) ? catalog : null;
        }
        catch (JsonException) { return null; }
    }

    // Для существующих подписанных каталогов без срока действует ограничение от подписанной даты обновления.
    internal static bool IsCurrent(Catalog catalog, DateTime now)
    {
        if (!DateOnly.TryParseExact(catalog.Updated, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var updated)) return false;
        var today = DateOnly.FromDateTime(now.ToUniversalTime());
        if (updated > today.AddDays(1)) return false;
        var limit = updated.AddDays(180);
        if (catalog.Expires != null)
        {
            if (!DateOnly.TryParseExact(catalog.Expires, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var expires) || expires < updated) return false;
            if (expires < limit) limit = expires;
        }
        return today <= limit;
    }

    /// <summary>
    /// Список для слияния: свежий с GitHub, если пора обновить и он скачался с верной подписью, иначе последний
    /// принятый. Null, если нет ни того ни другого.
    /// </summary>
    public static async Task<Catalog?> LoadAsync(CancellationToken ct = default)
    {
        Cache? cache = null;
        try
        {
            var path = Storage.PathOf(CacheFile);
            if (System.IO.File.Exists(path)) cache = JsonSerializer.Deserialize<Cache>(System.IO.File.ReadAllText(path), Storage.Options);
        }
        catch (Exception) { }
        if (cache == null || DateTime.Now - cache.Fetched > Refresh)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-catalog/1.0");
                // GitHub недоступен: тот же подписанный файл через jsDelivr (см. GitHubMirrors)
                if (await GitHubMirrors.SignedRawAsync(http, Url, (j, s) => Parse(j, s) != null, ct) is var (json, sig))
                {
                    cache = new Cache { Fetched = DateTime.Now, Json = json, Sig = sig };
                    try { Storage.Save(CacheFile, cache); } catch (Exception) { }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* без сети или GitHub недоступен: берем прошлый список */ }
        }
        return cache == null ? null : Parse(cache.Json, cache.Sig);
    }

    /// <summary>Серверы списка в виде строк таблицы. Запись с IP и DoH дает две строки: они проверяются отдельно.</summary>
    public static IEnumerable<DnsServer> Servers(Catalog c)
    {
        foreach (var e in c.Servers)
        {
            if (!string.IsNullOrEmpty(e.Primary))
                yield return new DnsServer { Name = e.Name, Primary = e.Primary, Secondary = e.Secondary, Source = "Список CHIMERION", SourceUrl = e.Site };
            if (!string.IsNullOrEmpty(e.Doh) && SourceParser.IsValidDoh(e.Doh, out _))
                yield return new DnsServer { Name = string.IsNullOrEmpty(e.Primary) ? e.Name : e.Name + " (DoH)", DohUrl = e.Doh, Source = "Список CHIMERION", SourceUrl = e.Site };
        }
    }
}
