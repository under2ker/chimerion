using System.Xml.Linq;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayTaskTests
{
    [Fact]
    public void Task_starts_at_logon_under_the_current_user_without_elevation()
    {
        const string exe = @"C:\Example & Test\CHIMERION.exe";
        const string user = "S-1-5-21-100-200-300-1001";
        var xml = XDocument.Parse(DohRelayTask.Xml(exe, user));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(exe, xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal(DohRelayTask.HelperArgument, xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal(user, xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.NotNull(xml.Descendants(ns + "RestartOnFailure").SingleOrDefault());
    }

    [Fact]
    public void Invalid_user_or_executable_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => DohRelayTask.Xml("relative.exe", "S-1-5-21-100-200-300-1001"));
        Assert.Throws<ArgumentException>(() => DohRelayTask.Xml(@"C:\Example\helper.exe", "invalid"));
    }
}
