using System.Reflection;
using Dnex.Services.Tools;
using Xunit;

namespace Dnex.Tests;

/// <summary>Сбой записи списков zapret больше не проглатывается молча (аудит T-07).</summary>
[Collection("scenarios")]
public class ZapretListWarningTests
{
    [Fact]
    public void Same_failure_is_reported_once()
    {
        var seen = new List<string>();
        var prev = ZapretTool.ListWarning;
        ZapretTool.ListWarning = seen.Add;
        try
        {
            var msg = "проверка T-07 " + Guid.NewGuid();
            ZapretTool.ReportListFailure(msg);
            ZapretTool.ReportListFailure(msg);
            Assert.Single(seen, msg);
        }
        finally { ZapretTool.ListWarning = prev; }
    }

    [Fact]
    public void Locked_ipset_file_is_reported_and_left_as_is()
    {
        var lists = Path.Combine(ZapretTool.Dir, "lists");
        Directory.CreateDirectory(lists);
        var target = Path.Combine(lists, "ipset-all.txt");
        File.WriteAllText(target, "198.51.100.0/24\r\n");
        var seen = new List<string>();
        var prev = ZapretTool.ListWarning;
        ZapretTool.ListWarning = seen.Add;
        try
        {
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var apply = typeof(ZapretTool).GetMethod("ApplyIpSet", BindingFlags.NonPublic | BindingFlags.Instance)!;
                apply.Invoke(new ZapretTool(), null); // исключения наружу нет, как и раньше
            }
            Assert.Contains(seen, m => m.Contains("ipset-all.txt") && m.Contains("не обновлен"));
            Assert.Equal("198.51.100.0/24\r\n", File.ReadAllText(target));
        }
        finally { ZapretTool.ListWarning = prev; File.Delete(target); }
    }
}
