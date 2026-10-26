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
                // Списки лежат на GitHub; если он недоступен, тот же файл берется через jsDelivr
                var text = await GitHubMirrors.GetStringAsync(http, GitHubMirrors.RawUrls(l.Url), ct, t => Clean(t, l.IsIp).Count >= l.MinLines);
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

    /// <summary>
    /// Сеть годится для обхода: не слишком широкая и не служебная. Подмененный или испорченный список с сетью вида
    /// 0.0.0.0/1 отправил бы в обход половину интернета, а с 192.168.0.0/16 и сайты роутера и локальной сети.
    /// </summary>
    internal static bool Acceptable(IPNetwork n)
    {
        var a = n.BaseAddress;
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (n.PrefixLength < 8) return false;
            var b = a.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127));
        }
        if (n.PrefixLength < 16) return false;
        var v6 = a.GetAddressBytes();
        // глобальные адреса только из 2000::/3: без локальных fc00::/7, fe80::/10, мультикаста и служебных
        return (v6[0] & 0xE0) == 0x20;
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
                if (IPNetwork.TryParse(line, out var n)) v = Acceptable(n) ? n.ToString() : null;
                else if (IPAddress.TryParse(line, out var a)) v = Acceptable(new IPNetwork(a, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)) ? a.ToString() : null;
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
