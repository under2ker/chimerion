using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Dnex.Services;

namespace Dnex.Tests;

[Collection("scenarios")]
public class ClaudeTimeZoneTests
{
    [Fact]
    public void PowerShell_passes_the_zone_and_restores_the_previous_value()
    {
        var command = ClaudeTimeZone.CodeCommand("Europe/Berlin");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"function claude {{ $env:TZ }}; {command}; $env:TZ");
        start.Environment["TZ"] = "Etc/UTC";
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(new[] { "Europe/Berlin", "Etc/UTC" },
            process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Europe/Atlantis")]
    [InlineData("Europe/Berlin'")]
    public void Invalid_zone_is_rejected_before_creating_a_command(string id) =>
        Assert.Throws<ArgumentException>(() => ClaudeTimeZone.CodeCommand(id));

    [Fact]
    public void Windows_receives_a_validated_zone_as_one_argument()
    {
        var start = ClaudeTimeZone.WindowsCommand("W. Europe Standard Time");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "tzutil.exe"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "/s", "W. Europe Standard Time" }, start.ArgumentList);
        Assert.Throws<TimeZoneNotFoundException>(() => ClaudeTimeZone.WindowsCommand("Invalid"));
    }

    [Fact]
    public async Task Test_copy_does_not_change_windows()
    {
        Assert.True(Storage.TestCopy);
        Assert.False(await ClaudeTimeZone.ApplyWindowsAsync("Invalid"));
    }

    [Fact]
    public void Browser_command_changes_only_the_time_zone()
    {
        using var command = JsonDocument.Parse(ClaudeTimeZone.BrowserCommand("Asia/Tokyo"));
        Assert.Equal("Emulation.setTimezoneOverride", command.RootElement.GetProperty("cmd").GetString());
        var args = command.RootElement.GetProperty("args");
        Assert.Single(args.EnumerateObject());
        Assert.Equal("Asia/Tokyo", args.GetProperty("timezoneId").GetString());
        Assert.Throws<ArgumentException>(() => ClaudeTimeZone.BrowserCommand("Invalid"));
    }
}
