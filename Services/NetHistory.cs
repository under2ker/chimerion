using System.IO;
using System.Text;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>Одна запись истории: когда, что программа изменила в сети и почему.</summary>
public sealed record NetChange(DateTime At, string What, string Why, string Details);

/// <summary>
/// История сетевых изменений: что CHIMERION меняла в системе (DNS адаптеров, правила «Двух DNS», IPv6, запасной сервер),
/// когда и почему (ваше действие, сторож, возврат после запуска Windows, режим). Нужна, чтобы по одному окну было понятно,
/// откуда взялись настройки DNS на компьютере. Хранится в папке данных (net-history.jsonl), последние записи.
/// Личных данных в записях нет: они проходят тот же фильтр, что журнал.
/// </summary>
public static class NetHistory
{
    private const string FileName = "net-history.jsonl";
    private const int Keep = 400, TrimAt = 600;
    private static readonly object Gate = new();
    private static readonly AsyncLocal<string?> CauseLocal = new();

    public const string UserCause = "ваше действие";

    /// <summary>Причина текущей операции. Без явной отметки это действие пользователя (кнопка, трей, горячая клавиша).</summary>
    public static string Cause => CauseLocal.Value ?? UserCause;

    /// <summary>Отмечает причину для всего, что произойдет внутри (в том числе после await): using var _ = NetHistory.Because("сторож").</summary>
    public static IDisposable Because(string cause)
    {
        var previous = CauseLocal.Value;
        CauseLocal.Value = cause;
        return new Scope(() => CauseLocal.Value = previous);
    }

    private sealed class Scope(Action undo) : IDisposable { public void Dispose() => undo(); }

    private static string PathOfFile => Storage.PathOf(FileName);

    /// <summary>Добавляет запись. Ошибка записи истории никогда не мешает самому действию.</summary>
    public static void Add(string what, string details = "", string? why = null)
    {
        try
        {
            var entry = new NetChange(DateTime.Now, PrivacyGuard.Scrub(what), why ?? Cause, PrivacyGuard.Scrub(details));
            var line = JsonSerializer.Serialize(entry) + "\n";
            lock (Gate)
            {
                Directory.CreateDirectory(Storage.Dir);
                File.AppendAllText(PathOfFile, line, new UTF8Encoding(false));
                Trim();
            }
        }
        catch (Exception) { /* история не важнее подключения */ }
    }

    private static void Trim()
    {
        var path = PathOfFile;
        var lines = File.ReadAllLines(path);
        if (lines.Length <= TrimAt) return;
        File.WriteAllLines(path, lines.Skip(lines.Length - Keep), new UTF8Encoding(false));
    }

    /// <summary>Записи от старых к новым.</summary>
    public static List<NetChange> Load()
    {
        var list = new List<NetChange>();
        try
        {
            lock (Gate)
            {
                var path = PathOfFile;
                if (!File.Exists(path)) return list;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { if (JsonSerializer.Deserialize<NetChange>(line) is { } c) list.Add(c); }
                    catch (JsonException) { /* битая строка не ломает остальные */ }
                }
            }
        }
        catch (Exception) { }
        return list;
    }

    public static void Clear()
    {
        try { lock (Gate) { if (File.Exists(PathOfFile)) File.Delete(PathOfFile); } } catch (Exception) { }
    }

    /// <summary>Запись одной строкой для окна и отчета.</summary>
    public static string Format(NetChange c) =>
        $"{c.At:dd.MM HH:mm:ss}  {c.What}  ({c.Why}){(c.Details.Length > 0 ? ": " + c.Details : "")}";

    public static string AsText(int last = 200) =>
        string.Join("\n", Load().TakeLast(last).Select(Format));
}
