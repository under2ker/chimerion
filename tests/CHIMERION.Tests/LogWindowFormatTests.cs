using Dnex.Models;
using Xunit;

namespace Dnex.Tests;

/// <summary>Копирование из окна «Журнал»: время в скобках, с датой для журнала за 3 дня, строки по порядку.</summary>
public class LogWindowFormatTests
{
    private static readonly LogEntry[] Lines =
    {
        new(new DateTime(2026, 10, 29, 9, 5, 7), "Подключено: Тестовый Smart DNS"),
        new(new DateTime(2026, 10, 29, 9, 6, 0), "Не удалось проверить сервер"),
    };

    [Fact]
    public void CopiesTimeAndText() =>
        Assert.Equal($"[09:05:07] Подключено: Тестовый Smart DNS{Environment.NewLine}[09:06:00] Не удалось проверить сервер", LogWindow.Format(Lines, false));

    [Fact]
    public void ThreeDayCopyHasDate() =>
        Assert.StartsWith("[29.10 09:05:07] ", LogWindow.Format(Lines, true));

    [Fact]
    public void NothingToCopy() => Assert.Equal("", LogWindow.Format(Array.Empty<LogEntry>(), false));
}
