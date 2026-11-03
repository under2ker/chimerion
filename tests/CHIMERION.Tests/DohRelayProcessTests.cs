using System.Diagnostics;
using Xunit;

namespace Dnex.Tests;

public class DohRelayProcessTests
{
    [Theory]
    // Старое задание без канала и задание с чужим каналом не должны ничего запускать.
    [InlineData("--doh-relay-helper")]
    [InlineData("--doh-relay-helper f15d22cb599d4cd1b7c0098fd088b6e1")]
    public async Task Helper_without_matching_configuration_exits_without_starting_the_application_or_touching_dns(string arguments)
    {
        var data = Path.Combine(Path.GetTempPath(), "doh-relay-process-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "CHIMERION.exe");
            var start = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["DNEX_DATA_DIR"] = data;
            start.Environment["DNEX_NO_AUTORESTORE"] = "1";
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(); }
            Assert.Equal(1, process.ExitCode);
            Assert.False(File.Exists(Path.Combine(data, "active.json")));
            Assert.False(File.Exists(Path.Combine(data, "dns-backup.json")));
            // Настроек нет: посредник не должен создавать пустой файл, по которому его запустят в следующий раз.
            Assert.False(File.Exists(Path.Combine(data, "doh-relay.json")));
        }
        finally { Directory.Delete(data, true); }
    }
}
