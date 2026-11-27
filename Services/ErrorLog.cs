using System.IO;
using System.Text;

namespace Dnex.Services;

/// <summary>
/// error.log: необработанные ошибки программы. Раньше файл рос без предела, а отчет читает его целиком: при
/// повторяющейся ошибке он разрастался и тормозил подготовку отчета (аудит P-11). Теперь, когда файл больше
/// MaxBytes, остаются только последние KeepBytes, начиная с целой записи. Свежие записи не теряются.
/// </summary>
internal static class ErrorLog
{
    internal const long MaxBytes = 1024 * 1024;
    internal const int KeepBytes = 256 * 1024;
    private static readonly object Gate = new();

    internal static string FilePath => Storage.PathOf("error.log");

    /// <summary>Дописать запись (текст уже без личных данных). Ошибки записи глотаются: журнал не должен ронять программу.</summary>
    internal static void Append(string entry) => Append(FilePath, entry);

    internal static void Append(string path, string entry)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(path, entry);
                var info = new FileInfo(path);
                if (info.Length > MaxBytes) Trim(path);
            }
        }
        catch (Exception) { }
    }

    /// <summary>Оставить хвост файла: последние KeepBytes, с начала первой целой записи (строка с датой).</summary>
    private static void Trim(string path)
    {
        byte[] tail;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var take = (int)Math.Min(KeepBytes, fs.Length);
            fs.Seek(-take, SeekOrigin.End);
            tail = new byte[take];
            fs.ReadExactly(tail);
        }
        var text = Encoding.UTF8.GetString(tail);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z? ");
        var kept = m.Success ? text[m.Index..] : text[(text.IndexOf('\n') + 1)..];
        File.WriteAllText(path, $"{DateTime.Now:u} Журнал ошибок укорочен: оставлены последние записи\n" + kept);
    }
}
