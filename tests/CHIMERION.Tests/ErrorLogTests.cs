using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>error.log с пределом размера (аудит P-11).</summary>
public class ErrorLogTests
{
    [Fact]
    public void Big_log_is_trimmed_to_whole_recent_entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chimerion-errorlog-{Guid.NewGuid():N}.log");
        try
        {
            var block = new string('x', 900);
            for (int i = 0; i < 1500; i++)
                ErrorLog.Append(path, $"2026-11-27 12:{i / 60 % 60:00}:{i % 60:00}Z Ошибка {i}\n   at {block}\n");
            var len = new FileInfo(path).Length;
            Assert.True(len <= ErrorLog.MaxBytes, $"размер {len}");
            var text = File.ReadAllText(path);
            Assert.Contains("Ошибка 1499", text);                     // свежая запись на месте
            Assert.DoesNotContain("Ошибка 0\n", text);                // самые старые убраны
            Assert.Contains("Журнал ошибок укорочен", text);
            var recent = Diagnostics.RecentErrors(path, new DateTime(2026, 11, 27, 13, 0, 0));
            Assert.Contains(recent, l => l.Contains("Ошибка 1499"));   // отчет читает укороченный файл как раньше
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Small_log_is_untouched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chimerion-errorlog-{Guid.NewGuid():N}.log");
        try
        {
            ErrorLog.Append(path, "2026-11-27 12:00:00Z первая\n");
            ErrorLog.Append(path, "2026-11-27 12:00:01Z вторая\n");
            Assert.Equal("2026-11-27 12:00:00Z первая\n2026-11-27 12:00:01Z вторая\n", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
