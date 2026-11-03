using System.Diagnostics;
using Xunit;

namespace Dnex.Tests;

public class DohRelayProcessTests
{
    [Fact]
    public async Task Helper_without_configuration_exits_without_starting_the_application_or_touching_dns()
    {
        var data = Path.Combine(Path.GetTempPath(), "doh-relay-process-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "CHIMERION.exe");
            var start = new ProcessStartInfo(exe, "--doh-relay-helper") { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["DNEX_DATA_DIR"] = data;
            start.Environment["DNEX_NO_AUTORESTORE"] = "1";
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(); }
            Assert.Equal(1, process.ExitCode);
            Assert.False(File.Exists(Path.Combine(data, "active.json")));
            Assert.False(File.Exists(Path.Combine(data, "dns-backup.json")));
        }
        finally { Directory.Delete(data, true); }
    }
}
