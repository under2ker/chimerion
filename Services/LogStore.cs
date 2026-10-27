using System.Globalization;
using System.IO;
using System.Text;

namespace Dnex.Services;

/// <summary>
/// Журнал на диске: папка logs, файл на каждый день (2026-10-27.log), строка «время TAB текст». Хранится 3 дня,
/// старые файлы удаляются при запуске и при смене дня. Пишется уже очищенный текст (без IP, места и провайдера),
/// поэтому копирование и отчет берут журнал за все 3 дня, а не только за текущий запуск.
/// </summary>
public static class LogStore
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(3);
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly object Gate = new();
    private static DateTime _prunedDay;

    public static string Dir => Storage.PathOf("logs");

    private static string FileOf(DateTime day) => Path.Combine(Dir, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    /// <summary>Дописать строки. Многострочный текст склеивается в одну строку, чтобы файл читался обратно построчно.</summary>
    public static void Append(IEnumerable<(DateTime Time, string Text)> lines, DateTime? now = null)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if ((now ?? DateTime.Now).Date != _prunedDay) Prune(now);
                foreach (var day in list.GroupBy(l => l.Time.Date))
                {
                    var sb = new StringBuilder();
                    foreach (var (t, text) in day)
                        sb.Append(t.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\t').Append(OneLine(text)).Append('\n');
                    File.AppendAllText(FileOf(day.Key), sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception) { /* журнал на диске не главное: нет места или нет прав, программа работает дальше */ }
        }
    }

    /// <summary>Все записи за последние 3 дня по порядку.</summary>
    public static List<(DateTime Time, string Text)> ReadAll(DateTime? now = null)
    {
        var from = (now ?? DateTime.Now) - Keep;
        var result = new List<(DateTime, string)>();
        lock (Gate)
        {
            try
            {
                if (!Directory.Exists(Dir)) return result;
                foreach (var file in Directory.GetFiles(Dir, "*.log").Order(StringComparer.Ordinal))
                {
                    if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                        || day.Date < from.Date) continue;
                    foreach (var line in File.ReadLines(file, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0 || !DateTime.TryParseExact(line[..tab], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                        if (t >= from) result.Add((t, line[(tab + 1)..]));
                    }
                }
            }
            catch (Exception) { }
        }
        return result;
    }

    /// <summary>Удалить файлы старше 3 дней.</summary>
    public static void Prune(DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        lock (Gate)
        {
            _prunedDay = today;
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (var file in Directory.GetFiles(Dir, "*.log"))
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                        && day < today - Keep)
                        File.Delete(file);
            }
            catch (Exception) { }
        }
    }

    private static string OneLine(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ⏎ ");
}
