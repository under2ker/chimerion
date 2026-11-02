using System.Xml.Linq;
using Dnex.Services.Tools;
using Xunit;

namespace Dnex.Tests;

/// <summary>
/// Задания Планировщика для обхода zapret: права спрашиваются один раз, дальше обход включается без вопроса Windows.
/// Задание с наивысшими правами, поэтому важно, кто может его менять и что именно оно запускает.
/// </summary>
public class ZapretTaskTests
{
    private const string Sid = "S-1-5-21-1000-2000-3000-1001";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Doc(string exe = @"C:\Program Files\CHIMERION\CHIMERION.exe", string verb = "start") =>
        XDocument.Parse(ZapretTask.Xml(exe, verb, Sid));

    [Fact]
    public void Task_runs_this_exe_in_helper_mode_with_highest_rights()
    {
        var d = Doc();
        Assert.Equal("\"C:\\Program Files\\CHIMERION\\CHIMERION.exe\"", d.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("--zapret-helper start", d.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal("HighestAvailable", d.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", d.Descendants(Ns + "LogonType").Single().Value);
        Assert.Equal(Sid, d.Descendants(Ns + "UserId").Single().Value);
    }

    [Fact]
    public void Stop_task_has_its_own_verb()
    {
        Assert.Equal("--zapret-helper stop", Doc(verb: "stop").Descendants(Ns + "Arguments").Single().Value);
    }

    [Fact]
    public void Only_admins_can_change_the_task_user_can_only_read_and_run()
    {
        var sddl = Doc().Descendants(Ns + "SecurityDescriptor").Single().Value;
        Assert.Contains("(A;;FA;;;BA)", sddl);
        Assert.Contains("(A;;FA;;;SY)", sddl);
        Assert.Contains($"(A;;GRGX;;;{Sid})", sddl);
        Assert.DoesNotContain($"(A;;FA;;;{Sid})", sddl);
    }

    [Fact]
    public void Task_has_no_schedule_it_runs_only_on_demand()
    {
        var d = Doc();
        Assert.Empty(d.Descendants(Ns + "Triggers"));
        Assert.Equal("true", d.Descendants(Ns + "AllowStartOnDemand").Single().Value);
    }

    [Fact]
    public void Path_with_ampersand_stays_valid_xml()
    {
        var d = Doc(@"D:\Tools & Apps\CHIMERION.exe");
        Assert.Equal("\"D:\\Tools & Apps\\CHIMERION.exe\"", d.Descendants(Ns + "Command").Single().Value);
    }

    [Fact]
    public void Path_compare_ignores_garbled_cyrillic_from_console_output()
    {
        // schtasks печатает в кодировке консоли: «Рабочий стол» может прийти как «????? ????» или нечитаемыми знаками
        Assert.Equal(ZapretTask.Ascii(@"C:\Users\Саша\Рабочий стол\CHIMERION.exe"), ZapretTask.Ascii(@"C:\Users\????\??????? ????\CHIMERION.exe"));
        Assert.NotEqual(ZapretTask.Ascii(@"C:\A\CHIMERION.exe"), ZapretTask.Ascii(@"C:\B\CHIMERION.exe"));
    }
}

public class ZapretTaskSecurityTests
{
    [Fact]
    public void Helper_copy_lives_in_program_files_where_users_cannot_write()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.StartsWith(pf, Dnex.Services.Tools.ZapretTask.HelperDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Dnex.Services.Tools.ZapretTask.HelperDir, Dnex.Services.Tools.ZapretTask.HelperExe, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Dnex.Services.Tools.ZapretTask.HelperDir, Dnex.Services.Tools.ZapretTask.HelperBin, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Task_xml_runs_the_protected_copy()
    {
        var xml = Dnex.Services.Tools.ZapretTask.Xml(Dnex.Services.Tools.ZapretTask.HelperExe, "start", "S-1-5-21-1");
        Assert.Contains(Dnex.Services.Tools.ZapretTask.HelperExe, xml);
        Assert.Contains("--zapret-helper start", xml);
    }

    [Fact]
    public void Test_process_is_not_the_protected_copy() => Assert.False(Dnex.Services.Tools.ZapretTask.IsHelperCopy);

    [Fact]
    public void Helper_refuses_to_start_winws_outside_the_protected_copy() =>
        Assert.NotEqual(0, Dnex.Services.Tools.ZapretTool.RunHelper("start"));
}
