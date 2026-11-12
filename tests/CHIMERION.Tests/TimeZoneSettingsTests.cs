using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Dnex.Services;

namespace Dnex.Tests;

[Collection("scenarios")]
public class TimeZoneSettingsTests
{
    [Fact]
    public void PowerShell_passes_the_zone_and_clock_instructions_and_restores_the_previous_value()
    {
        var command = TimeZoneSettings.ClaudeCodeCommand("Asia/Tokyo", "Tokyo Standard Time");
        var output = RunPowerShell($"function claude {{ [pscustomobject]@{{ zone = $env:TZ; arguments = @($args) }} | ConvertTo-Json -Compress }}; {command}; $env:TZ");
        Assert.Equal(2, output.Length);
        using var invocation = JsonDocument.Parse(output[0]);
        Assert.Equal("Asia/Tokyo", invocation.RootElement.GetProperty("zone").GetString());
        var arguments = invocation.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(2, arguments.Length);
        Assert.Equal("--append-system-prompt", arguments[0]);
        var instructions = Assert.IsType<string>(arguments[1]);
        Assert.Contains("selected time zone for this session is Asia/Tokyo", instructions);
        Assert.Contains("timeZone:`Asia/Tokyo`", instructions);
        Assert.Equal("Etc/UTC", output[1]);

        var clock = instructions.Split("PowerShell expression `", 2)[1].Split('`')[0];
        var timestamp = DateTimeOffset.ParseExact(Assert.Single(RunPowerShell(clock)), "yyyy-MM-dd HH:mm:ss zzz",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromHours(9), timestamp.Offset);
        Assert.InRange((DateTimeOffset.UtcNow - timestamp).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("Etc/UTC")]
    [InlineData(null)]
    public void PowerShell_restores_the_previous_zone_if_launch_fails(string? previousZone)
    {
        var command = TimeZoneSettings.ClaudeCodeCommand("Antarctica/Troll");
        var output = RunPowerShell($"function claude {{ throw 'mock launch failure' }}; try {{ {command} }} catch {{ }}; " +
            "[pscustomobject]@{ zone = [Environment]::GetEnvironmentVariable('TZ', 'Process') } | ConvertTo-Json -Compress",
            previousZone);
        using var environment = JsonDocument.Parse(Assert.Single(output));
        Assert.Equal(previousZone, environment.RootElement.GetProperty("zone").GetString());
    }

    private static string[] RunPowerShell(string script, string? previousZone = "Etc/UTC")
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        if (previousZone == null) start.Environment.Remove("TZ");
        else start.Environment["TZ"] = previousZone;
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
        return process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

    [Theory]
    [InlineData("Invalid")]
    [InlineData("UTC+03")]
    public void Browser_command_rejects_an_unknown_zone(string id) =>
        Assert.Throws<ArgumentException>(() => TimeZoneSettings.BrowserCommand(id));
}
