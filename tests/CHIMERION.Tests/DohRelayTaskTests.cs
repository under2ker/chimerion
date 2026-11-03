using System.Xml.Linq;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayTaskTests
{
    private const string Exe = @"C:\Example & Test\CHIMERION.exe";
    private const string User = "S-1-5-21-100-200-300-1001";
    private const string Channel = "f15d22cb599d4cd1b7c0098fd088b6e1";

    [Fact]
    public void Task_starts_at_logon_under_the_current_user_without_elevation()
    {
        var xml = XDocument.Parse(DohRelayTask.Xml(Exe, User, Channel));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(Exe, xml.Descendants(ns + "Command").Single().Value);
        // Канал обязателен: задание без него не найдет настройки посредника.
        Assert.Equal(DohRelayTask.HelperArgument + " " + Channel, xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal(User, xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.NotNull(xml.Descendants(ns + "RestartOnFailure").SingleOrDefault());
    }

    [Fact]
    public void Invalid_user_executable_or_channel_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => DohRelayTask.Xml("relative.exe", User, Channel));
        Assert.Throws<ArgumentException>(() => DohRelayTask.Xml(@"C:\Example\helper.exe", "invalid", Channel));
        Assert.Throws<ArgumentException>(() => DohRelayTask.Xml(@"C:\Example\helper.exe", User, "not-a-channel"));
        // Канал в формате Guid с разделителями тоже не подходит: имя задания строится из 32 символов.
        Assert.Throws<ArgumentException>(() => DohRelayTask.Xml(@"C:\Example\helper.exe", User, "f15d22cb-599d-4cd1-b7c0-098fd088b6e1"));
    }
}
