using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>«Скопировать отчет»: подробности, раскрываемые по «Подробнее», тоже без внешнего IP и имени пользователя.</summary>
public class DiagnosticsReportTests
{
    [Fact]
    public void MoreIsInReportAndSanitized()
    {
        var user = Environment.UserName;
        var items = new[]
        {
            new DiagItem(DiagLevel.Warn, "Адрес выхода 203.0.113.50", "Черные списки: нет. Оценка риска: 66 из 100",
                More: $"Ваш адрес 198.51.100.7, профиль C:\\Users\\{user}\\AppData"),
        };
        var text = Diagnostics.ToText(new DiagReport(items, "198.51.100.7", DateTime.Now), Array.Empty<string>());
        Assert.Contains("Оценка риска: 66 из 100", text);
        Assert.Contains("Ваш адрес", text);
        Assert.DoesNotContain("198.51.100.7", text);
        Assert.DoesNotContain("\\" + user + "\\", text);
    }
}
