using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Dnex.Services;

namespace Dnex.Tests;

[Collection("scenarios")]
public class TimeZoneSettingsTests
{
    [Fact]
    public void PowerShell_passes_the_zone_and_restores_the_previous_value()
    {
        var command = TimeZoneSettings.ClaudeCodeCommand("Europe/Berlin");
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
        Assert.Throws<ArgumentException>(() => TimeZoneSettings.ClaudeCodeCommand(id));

    [Fact]
    public void Windows_receives_a_validated_zone_as_one_argument()
    {
        var start = TimeZoneSettings.WindowsCommand("W. Europe Standard Time");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "tzutil.exe"), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "/s", "W. Europe Standard Time" }, start.ArgumentList);
        Assert.Throws<TimeZoneNotFoundException>(() => TimeZoneSettings.WindowsCommand("Invalid"));
    }

    [Fact]
    public async Task Test_copy_does_not_change_windows()
    {
        Assert.True(Storage.TestCopy);
        Assert.False(await TimeZoneSettings.ApplyWindowsAsync("Invalid"));
    }

    [Theory]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/New_York")]
    [InlineData("Europe/Berlin")]
    public void Browser_command_changes_only_the_time_zone(string zone)
    {
        using var command = JsonDocument.Parse(TimeZoneSettings.BrowserCommand(zone));
        Assert.Equal(2, command.RootElement.EnumerateObject().Count());
        Assert.Equal("Emulation.setTimezoneOverride", command.RootElement.GetProperty("cmd").GetString());
        var args = command.RootElement.GetProperty("args");
        Assert.Single(args.EnumerateObject());
        Assert.Equal(zone, args.GetProperty("timezoneId").GetString());
    }

    [Fact]
    public void Browser_command_rejects_an_unknown_zone() =>
        Assert.Throws<ArgumentException>(() => TimeZoneSettings.BrowserCommand("Invalid"));
}
