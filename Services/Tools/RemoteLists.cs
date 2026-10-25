using System.IO;
using System.Net;
using System.Net.Http;

namespace Dnex.Services.Tools;

/// <summary>Большой внешний список: домены для обхода или адреса для IPSet.</summary>
public sealed record RemoteList(string Id, string Title, string Note, string Url, bool IsIp, int MinLines, TimeSpan Refresh);

/// <summary>
/// Большие списки заблокированного от сторонних проектов с лицензией MIT. Скачиваются напрямую у авторов
/// (они обновляют их сами каждые несколько часов), проверяются на формат и правдоподобный размер и хранятся
/// в папке данных CHIMERION. Подписи у них нет: подмена списка влияет только на то, к каким сайтам или адресам
/// zapret применяет обход, поэтому вред от нее ограничен. Без сети используется последняя скачанная копия.
/// </summary>
public static class RemoteLists
{
    public static readonly RemoteList[] All =
    {
        new("refilter-domains", "Все заблокированное в России (Re:filter)",
            "Отфильтрованный реестр РКН и популярные заблокированные сайты, около 80 тысяч доменов. Широко: если что-то стало работать хуже, выключите",
            "https://raw.githubusercontent.com/1andrevich/Re-filter-lists/main/domains_all.lst", false, 20000, TimeSpan.FromHours(24)),
        new("refilter-ips", "Re:filter",
            "Суммированный список заблокированных адресов, около 27 тысяч",
            "https://raw.githubusercontent.com/1andrevich/Re-filter-lists/main/ipsum.lst", true, 5000, TimeSpan.FromHours(24)),
        new("eduard-ips", "Блокировки, CDN и облака (eduard256)",
            "Около 50 тысяч адресов из нескольких источников, обновляется каждые 6 часов",
            "https://raw.githubusercontent.com/eduard256/russia-blocked-ips/main/ip.txt", true, 10000, TimeSpan.FromHours(6)),
        new("fancy-cdn", "CDN и хостинги (RussiaFancyLists)",
            "Около 12 тысяч сетей CDN и хостингов, которые провайдеры замедляют (Cloudflare и другие)",
            "https://raw.githubusercontent.com/Noktomezo/RussiaFancyLists/main/lists/blacklist/ipsets/cdn.lst", true, 3000, TimeSpan.FromHours(24)),
    };

    public static RemoteList? Get(string id) => All.FirstOrDefault(l => l.Id == id);

    private static string PathOf(RemoteList l) => Path.Combine(Storage.Dir, "lists", l.Id + ".lst");

    /// <summary>Последняя скачанная копия (уже очищенная). Пусто, если списка еще нет.</summary>
    public static IReadOnlyList<string> Cached(RemoteList l)
    {
        try { return File.Exists(PathOf(l)) ? File.ReadAllLines(PathOf(l)) : Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public static string? CachedPath(RemoteList l) => File.Exists(PathOf(l)) ? PathOf(l) : null;

    /// <summary>
    /// Свежая копия, если пора обновить. При ошибке сети или подозрительном ответе остается прежняя.
    /// Возвращает число строк в копии после попытки.
    /// </summary>
    public static async Task<int> EnsureAsync(RemoteList l, CancellationToken ct = default)
    {
        var path = PathOf(l);
        var fresh = File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < l.Refresh;
        if (!fresh)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-lists/1.0");
                var text = await http.GetStringAsync(l.Url, ct);
                var clean = Clean(text, l.IsIp);
                // Защита от порчи: пустой или подозрительно короткий ответ не заменяет рабочую копию
                if (clean.Count >= l.MinLines)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllLinesAsync(path, clean, ct);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        return Cached(l).Count;
    }

    /// <summary>Только корректные строки: домены (без схемы и путей) или подсети IPv4/IPv6. Комментарии отбрасываются.</summary>
    public static List<string> Clean(string text, bool ip)
    {
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            string? v = null;
            if (ip)
            {
                if (IPNetwork.TryParse(line, out var n)) v = n.ToString();
                else if (IPAddress.TryParse(line, out var a)) v = a.ToString();
                // 0.0.0.0/8 и подобные служебные сети zapret не нужны
                if (v != null && (v.StartsWith("0.") || v.StartsWith("127.") || v == "::/0" || v == "0.0.0.0/0")) v = null;
            }
            else
            {
                var d = line.TrimStart('*').Trim('.').ToLowerInvariant();
                if (Uri.CheckHostName(d) == UriHostNameType.Dns && d.Contains('.')) v = d;
            }
            if (v != null && seen.Add(v)) res.Add(v);
        }
        return res;
    }
}
