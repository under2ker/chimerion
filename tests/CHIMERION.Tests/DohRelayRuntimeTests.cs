using System.Net;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Проверки слоя управления посредником. Статические точки подмены восстанавливаются после каждого теста.</summary>
[Collection("scenarios")]
public class DohRelayRuntimeTests
{
    private static readonly Uri Endpoint = new("https://dns.example.test/dns-query/example");
    private static readonly IPAddress[] Upstream = { IPAddress.Parse("203.0.113.10") };
    private const string Channel = "f15d22cb599d4cd1b7c0098fd088b6e1";

    private static DohRelayConfig SaveConfig()
    {
        var config = DohRelayConfig.Create(Endpoint, IPAddress.Parse(DohRelayRuntime.CandidateAddresses[0]), Upstream);
        config.Save();
        return config;
    }

    private static void WriteErrorFile() => Storage.SaveRequired("doh-relay-error.json", new { Kind = "test" });

    private static void RemoveFiles()
    {
        foreach (var name in new[] { DohRelayConfig.FileName, "doh-relay-error.json" })
        {
            try { File.Delete(Storage.PathOf(name)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static DohRelaySession Session(string channel) =>
        new(channel, DohRelayRuntime.CandidateAddresses[0], DohRelayRuntime.Port);

    [Fact]
    public async Task Start_saves_configuration_and_returns_a_session_for_the_new_channel()
    {
        var oldStart = DohRelayRuntime.StartHelper;
        try
        {
            RemoveFiles();
            DohRelayConfig? started = null;
            DohRelayRuntime.StartHelper = (config, _) => { started = config; return Task.FromResult(true); };
            var session = await DohRelayRuntime.StartAsync(Endpoint, Upstream, _ => { }, CancellationToken.None);
            Assert.NotNull(started);
            Assert.True(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
            var saved = DohRelayConfig.Load()!;
            Assert.Equal(saved.Channel, session.Channel);
            Assert.Equal(Endpoint, saved.Decode().Endpoint);
            Assert.Equal(DohRelayRuntime.CandidateAddresses[0], session.ListenAddress);
            Assert.Equal(53, session.Port);
        }
        finally
        {
            DohRelayRuntime.StartHelper = oldStart;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Failed_start_removes_configuration_and_task_without_exposing_the_relay_address()
    {
        var oldStart = DohRelayRuntime.StartHelper;
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        var log = new List<string>();
        var stopped = new List<string>();
        try
        {
            RemoveFiles();
            DohRelayRuntime.StartHelper = (_, _) => Task.FromResult(false);
            DohRelayRuntime.StopCommand = (_, _) => Task.FromResult(false);
            DohRelayRuntime.StopHelper = (channel, _) => { stopped.Add(channel); return Task.FromResult(true); };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DohRelayRuntime.StartAsync(Endpoint, Upstream, log.Add, CancellationToken.None));
            Assert.Contains("не запустился", error.Message);
            Assert.DoesNotContain("dns.example.test", error.Message);
            Assert.All(log, line => Assert.DoesNotContain("dns.example.test", line));
            Assert.False(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
            // Каждый занятый адрес убирает свое задание: после отказа не должно остаться ни одного.
            Assert.Equal(DohRelayRuntime.CandidateAddresses.Length, stopped.Count);
        }
        finally
        {
            DohRelayRuntime.StartHelper = oldStart;
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Stop_sends_stop_command_removes_task_and_service_files()
    {
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        try
        {
            RemoveFiles();
            var config = SaveConfig();
            WriteErrorFile();
            var calls = new List<string>();
            DohRelayRuntime.StopCommand = (channel, _) => { calls.Add("command:" + channel); return Task.FromResult(true); };
            DohRelayRuntime.StopHelper = (channel, _) => { calls.Add("task:" + channel); return Task.FromResult(true); };
            await DohRelayRuntime.StopAsync(Session(config.Channel), _ => { });
            Assert.Equal(new[] { "command:" + config.Channel, "task:" + config.Channel }, calls);
            Assert.False(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
            Assert.False(File.Exists(Storage.PathOf("doh-relay-error.json")));
        }
        finally
        {
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Stop_does_not_throw_when_helper_and_task_are_already_gone()
    {
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        var log = new List<string>();
        try
        {
            RemoveFiles();
            var config = SaveConfig();
            WriteErrorFile();
            DohRelayRuntime.StopCommand = (_, _) => throw new IOException("процесс уже закрыт");
            DohRelayRuntime.StopHelper = (_, _) => throw new InvalidOperationException("задание уже удалено");
            await DohRelayRuntime.StopAsync(Session(config.Channel), log.Add);
            Assert.NotEmpty(log);
            Assert.False(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
            Assert.False(File.Exists(Storage.PathOf("doh-relay-error.json")));
        }
        finally
        {
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Ensure_running_keeps_a_live_helper_and_does_not_touch_the_task()
    {
        var oldPing = DohRelayRuntime.PingHelper;
        var oldScheduler = DohRelayRuntime.Scheduler;
        var runs = new List<string>();
        try
        {
            DohRelayRuntime.PingHelper = (_, _) => Task.FromResult(true);
            DohRelayRuntime.Scheduler = () => new DohRelayScheduler((args, _) => { runs.Add(args[0]); return Task.FromResult(0); });
            Assert.True(await DohRelayRuntime.EnsureRunningAsync(Session(Channel), _ => { }, CancellationToken.None));
            Assert.Empty(runs);
        }
        finally
        {
            DohRelayRuntime.PingHelper = oldPing;
            DohRelayRuntime.Scheduler = oldScheduler;
        }
    }

    [Fact]
    public async Task Ensure_running_returns_the_existing_task_and_waits_for_readiness()
    {
        var oldPing = DohRelayRuntime.PingHelper;
        var oldScheduler = DohRelayRuntime.Scheduler;
        var oldTimeout = DohRelayRuntime.StartTimeout;
        var pings = 0;
        var runs = new List<string>();
        try
        {
            // Первый опрос не отвечает, после возврата задания посредник готов.
            DohRelayRuntime.PingHelper = (_, _) => Task.FromResult(++pings > 1);
            DohRelayRuntime.Scheduler = () => new DohRelayScheduler((args, _) => { runs.Add(args[0]); return Task.FromResult(0); });
            DohRelayRuntime.StartTimeout = TimeSpan.FromSeconds(2);
            Assert.True(await DohRelayRuntime.EnsureRunningAsync(Session(Channel), _ => { }, CancellationToken.None));
            Assert.Equal(new[] { "/Run" }, runs);
        }
        finally
        {
            DohRelayRuntime.PingHelper = oldPing;
            DohRelayRuntime.Scheduler = oldScheduler;
            DohRelayRuntime.StartTimeout = oldTimeout;
        }
    }

    [Fact]
    public async Task Ensure_running_reports_when_the_task_cannot_be_returned()
    {
        var oldPing = DohRelayRuntime.PingHelper;
        var oldScheduler = DohRelayRuntime.Scheduler;
        var log = new List<string>();
        try
        {
            DohRelayRuntime.PingHelper = (_, _) => Task.FromResult(false);
            DohRelayRuntime.Scheduler = () => new DohRelayScheduler((args, _) => Task.FromResult(args[0] == "/Run" ? 1 : 0));
            Assert.False(await DohRelayRuntime.EnsureRunningAsync(Session(Channel), log.Add, CancellationToken.None));
            Assert.NotEmpty(log);
        }
        finally
        {
            DohRelayRuntime.PingHelper = oldPing;
            DohRelayRuntime.Scheduler = oldScheduler;
        }
    }

    [Fact]
    public async Task Cleanup_stops_a_previous_helper_that_is_not_the_active_session()
    {
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        var calls = new List<string>();
        var log = new List<string>();
        try
        {
            RemoveFiles();
            var config = SaveConfig();
            WriteErrorFile();
            DohRelayRuntime.StopCommand = (channel, _) => { calls.Add("command:" + channel); return Task.FromResult(true); };
            DohRelayRuntime.StopHelper = (channel, _) => { calls.Add("task:" + channel); return Task.FromResult(true); };
            await DohRelayRuntime.CleanupStaleAsync(null, log.Add);
            Assert.Equal(new[] { "command:" + config.Channel, "task:" + config.Channel }, calls);
            Assert.Contains(log, line => line.Contains("Убран прежний локальный посредник DoH"));
            Assert.False(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
            Assert.False(File.Exists(Storage.PathOf("doh-relay-error.json")));
        }
        finally
        {
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Cleanup_keeps_the_helper_of_the_active_session()
    {
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        var calls = new List<string>();
        try
        {
            RemoveFiles();
            var config = SaveConfig();
            DohRelayRuntime.StopCommand = (channel, _) => { calls.Add(channel); return Task.FromResult(true); };
            DohRelayRuntime.StopHelper = (channel, _) => { calls.Add(channel); return Task.FromResult(true); };
            await DohRelayRuntime.CleanupStaleAsync(Session(config.Channel), _ => { });
            Assert.Empty(calls);
            Assert.True(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
        }
        finally
        {
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Cleanup_without_configuration_only_removes_the_error_file()
    {
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        var calls = new List<string>();
        try
        {
            RemoveFiles();
            WriteErrorFile();
            DohRelayRuntime.StopCommand = (channel, _) => { calls.Add(channel); return Task.FromResult(true); };
            DohRelayRuntime.StopHelper = (channel, _) => { calls.Add(channel); return Task.FromResult(true); };
            await DohRelayRuntime.CleanupStaleAsync(null, _ => { });
            Assert.Empty(calls);
            Assert.False(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
            Assert.False(File.Exists(Storage.PathOf("doh-relay-error.json")));
        }
        finally
        {
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Start_rejects_non_https_endpoint_and_empty_upstream()
    {
        RemoveFiles();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => DohRelayRuntime.StartAsync(new Uri("http://dns.example.test/dns-query"), Upstream, _ => { }, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(
                () => DohRelayRuntime.StartAsync(Endpoint, Array.Empty<IPAddress>(), _ => { }, CancellationToken.None));
        }
        finally { RemoveFiles(); }
    }

    [Fact]
    public async Task Start_tries_the_next_local_address_when_the_first_one_is_taken()
    {
        var oldStart = DohRelayRuntime.StartHelper;
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        var attempts = new List<string>();
        try
        {
            RemoveFiles();
            // Первый локальный адрес занят прежним посредником: готовность приходит со второго.
            DohRelayRuntime.StartHelper = (config, _) =>
            {
                attempts.Add(config.ListenAddress);
                return Task.FromResult(attempts.Count > 1);
            };
            DohRelayRuntime.StopCommand = (_, _) => Task.FromResult(false);
            DohRelayRuntime.StopHelper = (_, _) => Task.FromResult(false);
            var session = await DohRelayRuntime.StartAsync(Endpoint, Upstream, _ => { }, CancellationToken.None);
            Assert.Equal(2, attempts.Count);
            Assert.Equal(DohRelayRuntime.CandidateAddresses[1], session.ListenAddress);
            Assert.Equal(session.Channel, DohRelayConfig.Load()!.Channel);
        }
        finally
        {
            DohRelayRuntime.StartHelper = oldStart;
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Stop_keeps_configuration_that_belongs_to_another_channel()
    {
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        try
        {
            RemoveFiles();
            var config = SaveConfig();
            DohRelayRuntime.StopCommand = (_, _) => Task.FromResult(false);
            DohRelayRuntime.StopHelper = (_, _) => Task.FromResult(true);
            // Останавливаем прежний канал, а в файле уже лежат настройки нового: их удалять нельзя.
            await DohRelayRuntime.StopAsync(Session(Channel), _ => { });
            Assert.True(File.Exists(Storage.PathOf(DohRelayConfig.FileName)));
            Assert.Equal(config.Channel, DohRelayConfig.Load()!.Channel);
        }
        finally
        {
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Helper_error_makes_the_next_local_address_try_without_waiting_for_the_timeout()
    {
        var oldScheduler = DohRelayRuntime.Scheduler;
        var oldPing = DohRelayRuntime.PingHelper;
        var oldExe = DohRelayRuntime.ExecutablePath;
        var oldSid = DohRelayRuntime.CurrentUserSid;
        var oldTimeout = DohRelayRuntime.StartTimeout;
        var creates = 0;
        try
        {
            RemoveFiles();
            DohRelayRuntime.ExecutablePath = () => @"C:\Example\CHIMERION.exe";
            DohRelayRuntime.CurrentUserSid = () => "S-1-5-21-100-200-300-1001";
            DohRelayRuntime.StartTimeout = TimeSpan.FromSeconds(10);
            DohRelayRuntime.PingHelper = (_, _) => Task.FromResult(false);
            // Посредник не может занять адрес и пишет файл ошибки: ждать весь таймаут незачем.
            DohRelayRuntime.Scheduler = () => new DohRelayScheduler((args, _) =>
            {
                if (args[0] == "/Query") return Task.FromResult(1);
                if (args[0] == "/Create")
                {
                    creates++;
                    Storage.SaveRequired("doh-relay-error.json", new { Kind = "SocketException" });
                    return Task.FromResult(0);
                }
                return Task.FromResult(0);
            });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DohRelayRuntime.StartAsync(Endpoint, Upstream, _ => { }, CancellationToken.None));
            watch.Stop();
            Assert.Equal(DohRelayRuntime.CandidateAddresses.Length, creates);
            // Без раннего отказа по файлу ошибки перебор занял бы десятки секунд.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"перебор занял {watch.Elapsed.TotalSeconds:0.0} с");
        }
        finally
        {
            DohRelayRuntime.Scheduler = oldScheduler;
            DohRelayRuntime.PingHelper = oldPing;
            DohRelayRuntime.ExecutablePath = oldExe;
            DohRelayRuntime.CurrentUserSid = oldSid;
            DohRelayRuntime.StartTimeout = oldTimeout;
            RemoveFiles();
        }
    }
}
