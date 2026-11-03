using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

[Collection("scenarios")]
public class DohRelaySchedulerTests
{
    private const string Exe = @"C:\Example\CHIMERION.exe";
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string Channel = "f15d22cb599d4cd1b7c0098fd088b6e1";

    [Fact]
    public async Task Existing_task_is_not_overwritten()
    {
        var calls = new List<string>();
        var scheduler = new DohRelayScheduler((args, _) => { calls.Add(args[0]); return Task.FromResult(0); });
        Assert.False(await scheduler.CreateAndRunAsync(Exe, Sid, Channel, CancellationToken.None));
        Assert.Equal(new[] { "/Query" }, calls);
    }

    [Fact]
    public async Task Failed_start_removes_created_task_and_temporary_xml()
    {
        var calls = new List<string>(); string? xmlPath = null;
        var scheduler = new DohRelayScheduler((args, _) =>
        {
            calls.Add(args[0]);
            if (args[0] == "/Query") return Task.FromResult(1);
            if (args[0] == "/Create") { xmlPath = args[^1]; Assert.True(File.Exists(xmlPath)); return Task.FromResult(0); }
            return Task.FromResult(args[0] == "/Run" ? 1 : 0);
        });
        Assert.False(await scheduler.CreateAndRunAsync(Exe, Sid, Channel, CancellationToken.None));
        Assert.Equal(new[] { "/Query", "/Create", "/Run", "/Delete" }, calls);
        Assert.NotNull(xmlPath);
        Assert.False(File.Exists(xmlPath));
    }

    [Fact]
    public async Task Cleanup_failure_is_not_hidden()
    {
        var scheduler = new DohRelayScheduler((args, _) => Task.FromResult(args[0] == "/Create" ? 0 : 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.CreateAndRunAsync(Exe, Sid, Channel, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_only_the_named_task()
    {
        var calls = new List<string[]>();
        var scheduler = new DohRelayScheduler((args, _) => { calls.Add(args); return Task.FromResult(0); });
        Assert.True(await scheduler.DeleteAsync(Channel, CancellationToken.None));
        Assert.Equal(new[] { "/Delete", "/TN", "CHIMERION DoH " + Channel, "/F" }, Assert.Single(calls));
    }

    [Fact]
    public async Task Run_existing_starts_the_named_task_and_reports_the_result()
    {
        var calls = new List<string[]>();
        var scheduler = new DohRelayScheduler((args, _) => { calls.Add(args); return Task.FromResult(0); });
        Assert.True(await scheduler.RunExistingAsync(Channel, CancellationToken.None));
        Assert.Equal(new[] { "/Run", "/TN", "CHIMERION DoH " + Channel }, Assert.Single(calls));

        var failing = new DohRelayScheduler((_, _) => Task.FromResult(1));
        Assert.False(await failing.RunExistingAsync(Channel, CancellationToken.None));
        Assert.False(await failing.DeleteAsync(Channel, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_channel_is_rejected_before_the_scheduler()
    {
        var calls = 0;
        var scheduler = new DohRelayScheduler((_, _) => { calls++; return Task.FromResult(0); });
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.DeleteAsync("bad", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.RunExistingAsync("bad", CancellationToken.None));
        Assert.Equal(0, calls);
    }
}
