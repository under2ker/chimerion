using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NodaTime.TimeZones;

namespace Dnex.Services;

internal static class TimeZoneSettings
{
    private static readonly SemaphoreSlim WindowsChange = new(1, 1);

    /// <summary>Передает пояс процессу и дает сеансу способ получить местное время из часов UTC.</summary>
    internal static string ClaudeCodeCommand(string ianaId, string? windowsId = null)
    {
        if (!KnownIana(ianaId)) throw new ArgumentException("Неизвестный часовой пояс", nameof(ianaId));

        // Одинарные кавычки оболочки и обратные кавычки JavaScript работают и в PowerShell 5.1, и в Bash.
        var nodeClock = $"node -p 'new Date().toLocaleString(`sv-SE`,{{timeZone:`{ianaId}`,timeZoneName:`longOffset`}})'";
        var prompt = $"The selected time zone for this session is {ianaId}. " +
            "Report this ID as the selected zone when asked about the user's time zone or the session time zone. " +
            "Use this zone for the user's local date and time. To answer a question about the current time or date, " +
            $"run this read-only shell command and use its output: {nodeClock}. ";
        if (windowsId != null)
        {
            var systemZone = PowerShellLiteral(TimeZoneInfo.FindSystemTimeZoneById(windowsId).Id);
            var windowsClock = $"[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, {systemZone})" +
                ".ToString('yyyy-MM-dd HH:mm:ss zzz', [Globalization.CultureInfo]::InvariantCulture)";
            prompt += $"If Node.js is unavailable, evaluate this PowerShell expression `{windowsClock}`. ";
        }
        prompt += "Plain Get-Date and tzutil /g report the Windows settings. If you cannot read the clock in the selected zone, " +
            "give the selected zone ID and say that you could not verify its current time.";

        return "& { $previousZone = [Environment]::GetEnvironmentVariable('TZ', 'Process'); " +
            $"try {{ $env:TZ = {PowerShellLiteral(ianaId)}; claude --append-system-prompt {PowerShellLiteral(prompt)} }} " +
            "finally { [Environment]::SetEnvironmentVariable('TZ', $previousZone, 'Process') } }";
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string BrowserCommand(string ianaId)
    {
        if (!KnownIana(ianaId)) throw new ArgumentException("Неизвестный часовой пояс", nameof(ianaId));
        return JsonSerializer.Serialize(new { cmd = "Emulation.setTimezoneOverride", args = new { timezoneId = ianaId } });
    }

    private static bool KnownIana(string id) => id.Length > 0 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+') &&
        (TzdbDateTimeZoneSource.Default.CanonicalIdMap.ContainsKey(id) || TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _));

    internal static ProcessStartInfo WindowsCommand(string windowsId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tzutil.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add(zone.Id);
        return start;
    }

    /// <summary>Проверяем итог по Windows, чтобы не показать успех после отказа прав или смены пояса политикой.</summary>
    internal static async Task<bool> ApplyWindowsAsync(string windowsId)
    {
        if (Storage.TestCopy || !await WindowsChange.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            using var process = Process.Start(WindowsCommand(windowsId))
                ?? throw new InvalidOperationException("Не удалось запустить настройку Windows");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw new TimeoutException("Windows не ответила вовремя");
            }
            TimeZoneInfo.ClearCachedData();
            return process.ExitCode == 0 && TimeZoneInfo.Local.Id.Equals(windowsId, StringComparison.OrdinalIgnoreCase);
        }
        finally { WindowsChange.Release(); }
    }
}
