using System.IO;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

[Collection("scenarios")]
public class LogStoreTests : IDisposable
{
    public LogStoreTests()
    {
        if (Directory.Exists(LogStore.Dir)) Directory.Delete(LogStore.Dir, true);
    }

    public void Dispose()
    {
        if (Directory.Exists(LogStore.Dir)) Directory.Delete(LogStore.Dir, true);
        PrivacyGuard.ForgetPersonal();
    }

    [Fact]
    public void Entries_Of_Last_Three_Days_Are_Read_Back_From_All_Runs()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0);
        LogStore.Append(new[] { (now.AddDays(-4), "старая"), (now.AddDays(-2.5), "позавчера") }, now);
        LogStore.Append(new[] { (now.AddHours(-1), "строка\nвторая") }, now);
        var all = LogStore.ReadAll(now);
        Assert.Equal(new[] { "позавчера", "строка ⏎ вторая" }, all.Select(x => x.Text));
        Assert.Equal(now.AddHours(-1), all[^1].Time);
    }

    [Fact]
    public void Files_Older_Than_Three_Days_Are_Deleted()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0);
        LogStore.Append(new[] { (now.AddDays(-5), "a"), (now.AddDays(-1), "b") }, now.AddDays(-5));
        LogStore.Prune(now);
        var files = Directory.GetFiles(LogStore.Dir).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "2026-10-26.log" }, files);
    }

    [Fact]
    public void Log_Is_Scrubbed_Of_Personal_Data_Even_When_Privacy_Mode_Is_Off()
    {
        PrivacyGuard.ForgetPersonal();
        PrivacyGuard.RememberPersonal("88.86.206.62", "RU, Sverdlovsk Oblast, Pervouralsk", "AS28890 INSYS ISP");
        var r = PrivacyGuard.Scrub(@"IP 88.86.206.62, Pervouralsk, INSYS ISP, DNS 83.217.211.149, шлюз 192.168.1.1, C:\Users\" + Environment.UserName + @"\x");
        Assert.DoesNotContain("88.86.206.62", r);
        Assert.DoesNotContain("Pervouralsk", r);
        Assert.DoesNotContain("INSYS", r);
        Assert.DoesNotContain("192.168.1.1", r);
        Assert.DoesNotContain("83.217.211.149", r);
        if (Environment.UserName.Length >= 2) Assert.DoesNotContain(@"\" + Environment.UserName + @"\", r);
    }
}
