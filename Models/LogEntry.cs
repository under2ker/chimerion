using System.Windows.Media;

namespace Dnex.Models;

public enum LogLevel { Info, Ok, Warn, Error }

/// <summary>Строка журнала: время, текст и уровень. Уровень определяется по тексту сообщения.</summary>
public sealed class LogEntry
{
    private static readonly Brush AccentB = Freeze(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush OkB = Freeze(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush WarnB = Freeze(Color.FromRgb(0xF5, 0xB9, 0x41));
    private static readonly Brush BadB = Freeze(Color.FromRgb(0xF2, 0x62, 0x6E));
    private static readonly Brush TextB = Freeze(Color.FromRgb(0xE7, 0xE9, 0xEE));

    public LogEntry(DateTime time, string text)
    {
        Time = time;
        Text = text;
        Level = Classify(text);
    }

    public DateTime Time { get; }
    public string Text { get; }
    public LogLevel Level { get; }
    public string TimeText => Time.ToString("HH:mm:ss");
    public string DateText => Time.ToString("dd.MM HH:mm:ss");

    /// <summary>Цвет полоски слева.</summary>
    public Brush Bar => Level switch { LogLevel.Ok => OkB, LogLevel.Warn => WarnB, LogLevel.Error => BadB, _ => AccentB };

    /// <summary>Текст предупреждений и ошибок подсвечивается, остальное обычным цветом.</summary>
    public Brush TextBrush => Level switch { LogLevel.Error => BadB, LogLevel.Warn => WarnB, _ => TextB };

    public static LogLevel Classify(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("не удалось") || t.Contains("ошибка") || t.Contains("exception") || t.Contains("error")) return LogLevel.Error;
        if (t.Contains("does not indicate success") || t.Contains("нет ответа") || t.Contains("connection could not") ||
            t.Contains("внимание") || t.Contains("медленная") || t.Contains("остановлен") || t.Contains("сторож") ||
            t.Contains("не отвечает") || t.Contains("нет соединения"))
            return LogLevel.Warn;
        if (t.StartsWith("+") || t.Contains("готово") || t.Contains("подключено") || t.Contains("добавлен") ||
            t.Contains("сохранены") || t.Contains("итог: работают") || t.Contains("настройки сохранены"))
            return LogLevel.Ok;
        return LogLevel.Info;
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
