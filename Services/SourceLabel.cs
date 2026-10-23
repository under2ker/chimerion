using System.Text.RegularExpressions;

namespace Dnex.Services;

/// <summary>
/// Откуда взят сервер, в читаемом виде: сайт и путь на нем «хлебными крошками» вместо голой ссылки.
/// «https://github.com/MAECTRO/magicdns/commits/main/» → github.com › MAECTRO › magicdns › commits › main.
/// Понятные места называются словами: Telegram-канал, файл на GitHub, тема и сообщение на форуме.
/// Длинный путь сокращается: сайт, начало пути, «…», конец.
/// </summary>
public static class SourceLabel
{
    /// <summary>Части подписи: первая — сайт, дальше путь. Пусто, если ссылка не http или https.</summary>
    public static List<string> Parts(string? url, int max = 70)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            return new();
        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();
        var query = ParseQuery(u.Query);

        var parts = host.ToLowerInvariant() switch
        {
            // t.me/s/канал — лента Telegram-канала
            "t.me" when segs.Count >= 2 && segs[0] == "s" => new List<string> { "Telegram", "@" + segs[1] },
            "t.me" when segs.Count >= 1 => new List<string> { "Telegram", "@" + segs[0] },
            // raw.githubusercontent.com/автор/репозиторий/ветка/путь/файл — файл в репозитории
            "raw.githubusercontent.com" when segs.Count >= 4 => new List<string> { "github.com", segs[0], segs[1], segs[^1] },
            _ => Generic(host, segs, query),
        };
        return Shorten(parts, max);
    }

    /// <summary>Подпись одной строкой: «github.com › MAECTRO › magicdns». Если ссылки нет — запасной текст.</summary>
    public static string Pretty(string? url, string fallback, int max = 70)
    {
        var parts = Parts(url, max);
        return parts.Count == 0 ? fallback : string.Join(" › ", parts);
    }

    /// <summary>
    /// Короткая подпись для узкой колонки: сайт и самая говорящая часть пути (у форума тема, иначе последняя часть):
    /// «github.com › main», «4pda.to › тема 1117490», «Telegram › @dns_club». Полный путь — в полосе над таблицей.
    /// </summary>
    public static string Compact(string? url, string fallback)
    {
        var parts = Parts(url, int.MaxValue);
        if (parts.Count == 0) return fallback;
        if (parts.Count == 1) return parts[0];
        var key = parts[1].StartsWith("тема ") ? parts[1] : parts[^1];
        return $"{parts[0]} › {key}";
    }

    private static List<string> Generic(string host, List<string> segs, Dictionary<string, string> q)
    {
        var parts = new List<string> { host };
        // Форумы на IPB (4pda и подобные): index.php?showtopic=…&view=findpost&p=…
        if (q.TryGetValue("showtopic", out var topic))
        {
            parts.Add($"тема {topic}");
            if (q.TryGetValue("p", out var post) && q.TryGetValue("view", out var v) && v == "findpost") parts.Add($"сообщение {post}");
            return parts;
        }
        if (q.TryGetValue("showforum", out var forum)) { parts.Add($"раздел {forum}"); return parts; }

        // Страница скрипта с номером (page.php?id=16095) — номер вместо имени файла
        foreach (var s in segs)
        {
            if (Regex.IsMatch(s, @"^(index|page|view|show)\.(php|aspx?|html?)$", RegexOptions.IgnoreCase)) continue;
            parts.Add(s.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? s[..^5] : s);
        }
        foreach (var key in new[] { "id", "p", "t", "topic", "post" })
            if (q.TryGetValue(key, out var id)) { parts.Add($"№{id}"); break; }
        return parts;
    }

    /// <summary>
    /// Сокращение: каждая часть не длиннее 28 знаков; если вся подпись длиннее max,
    /// остаются сайт, первая часть пути, «…» и столько последних частей, сколько помещается.
    /// </summary>
    private static List<string> Shorten(List<string> parts, int max)
    {
        parts = parts.Select(p => p.Length <= 28 ? p : p[..27] + "…").ToList();
        int Len(IEnumerable<string> ps) => ps.Sum(p => p.Length) + Math.Max(0, ps.Count() - 1) * 3;
        if (parts.Count <= 3 || Len(parts) <= max) return parts;

        var head = parts.Take(2).ToList();
        var tail = new List<string>();
        for (int i = parts.Count - 1; i >= 2; i--)
        {
            var test = head.Append("…").Concat(new[] { parts[i] }).Concat(tail).ToList();
            if (tail.Count > 0 && Len(test) > max) break;
            tail.Insert(0, parts[i]);
        }
        return tail.Count + 2 >= parts.Count ? parts : head.Append("…").Concat(tail).ToList();
    }

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); } catch (Exception) { return s; }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[1].Length > 0) d[kv[0]] = Unescape(kv[1]);
        }
        return d;
    }
}
