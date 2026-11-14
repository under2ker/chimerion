namespace Dnex.Services;

/// <summary>Одна строка палитры: что показать и что выполнить. Note: пояснение под названием («сейчас выключено»).</summary>
public sealed record PaletteCommand(string Title, string Group, string Icon, string Keywords, Func<Task> Run, string? Note = null);

/// <summary>
/// Поиск для палитры Ctrl+K: слова запроса ищутся в названии и в ключевых словах, начало слова весит больше середины.
/// Запрос, набранный в английской раскладке («ldf lyc» вместо «два днс»), находит то же самое. Без интерфейса, чтобы
/// проверяться тестами.
/// </summary>
public static class PaletteSearch
{
    private const string En = "qwertyuiop[]asdfghjkl;'zxcvbnm,./`";
    private const string Ru = "йцукенгшщзхъфывапролджэячсмитьбю.ё";

    /// <summary>Строчные буквы, «е» вместо «ё», без кавычек и знаков, слова через один пробел.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.ToLowerInvariant().Replace('ё', 'е').Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Те же клавиши в другой раскладке: английские буквы становятся русскими и наоборот.</summary>
    public static string SwapLayout(string s)
    {
        var chars = s.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            int e = En.IndexOf(chars[i]), r = Ru.IndexOf(chars[i]);
            if (e >= 0) chars[i] = Ru[e]; else if (r >= 0) chars[i] = En[r];
        }
        return new string(chars);
    }

    /// <summary>Баллы строки за один запрос: -1 если какого-то слова запроса нет ни в названии, ни в ключевых словах.</summary>
    internal static int Score(string query, PaletteCommand c)
    {
        var tokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 0;
        var title = Normalize(c.Title);
        var titleWords = title.Split(' ');
        var keys = Normalize(c.Keywords + " " + c.Group);
        var keyWords = keys.Split(' ');
        int total = 0;
        foreach (var t in tokens)
        {
            int best =
                title.StartsWith(t, StringComparison.Ordinal) ? 100 :
                titleWords.Any(w => w.StartsWith(t, StringComparison.Ordinal)) ? 80 :
                title.Contains(t, StringComparison.Ordinal) ? 50 :
                keyWords.Any(w => w.StartsWith(t, StringComparison.Ordinal)) ? 40 :
                keys.Contains(t, StringComparison.Ordinal) ? 25 : -1;
            if (best < 0) return -1;
            total += best;
        }
        return total;
    }

    /// <summary>Лучшие строки по запросу. Пустой запрос: первые строки списка в заданном порядке.</summary>
    public static IReadOnlyList<PaletteCommand> Rank(string query, IReadOnlyList<PaletteCommand> all, int max = 40)
    {
        if (Normalize(query).Length == 0) return all.Take(max).ToList();
        var found = RankOne(query, all, max);
        if (found.Count == 0) found = RankOne(SwapLayout(query), all, max); // раскладка не та
        return found;
    }

    private static List<PaletteCommand> RankOne(string query, IReadOnlyList<PaletteCommand> all, int max) =>
        all.Select((c, i) => (c, i, s: Score(query, c))).Where(x => x.s >= 0)
           .OrderByDescending(x => x.s).ThenBy(x => x.i).Take(max).Select(x => x.c).ToList();
}
