using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Dnex.Services;

internal static class TimeZoneSettings
{
    private static readonly SemaphoreSlim WindowsChange = new(1, 1);

    internal sealed record Zone(string WindowsId, string Name, string? IanaId);

    internal static IReadOnlyList<Zone> Zones(bool english) => TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => new Zone(zone.Id, DisplayName(zone, english),
            TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var id) ? id : null))
        .ToArray();

    internal static string DisplayName(TimeZoneInfo zone, bool english)
    {
        if (!english) return zone.DisplayName;
        var offset = zone.BaseUtcOffset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return $"(UTC{sign}{absolute.Hours:00}:{absolute.Minutes:00}) {zone.Id}";
    }

    /// <summary>Часовой пояс действует в процессе Claude Code и его дочерних процессах до выхода.</summary>
    internal static string ClaudeCodeCommand(string ianaId)
    {
        if (!KnownIana(ianaId)) throw new ArgumentException("Неизвестный часовой пояс", nameof(ianaId));
        return "& { $previousZone = [Environment]::GetEnvironmentVariable('TZ', 'Process'); " +
            $"try {{ $env:TZ = '{ianaId}'; claude }} " +
            "finally { [Environment]::SetEnvironmentVariable('TZ', $previousZone, 'Process') } }";
    }

    internal static string BrowserCommand(string ianaId)
    {
        if (!KnownIana(ianaId)) throw new ArgumentException("Неизвестный часовой пояс", nameof(ianaId));
        return JsonSerializer.Serialize(new { cmd = "Emulation.setTimezoneOverride", args = new { timezoneId = ianaId } });
    }

    private static bool KnownIana(string id) => id.Length > 0 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+') &&
        TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _);

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
