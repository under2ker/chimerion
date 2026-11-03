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

    private static void WriteErrorFile(string channel) => Storage.SaveRequired(DohRelayConfig.ErrorFileNameFor(channel), new { Kind = "test" });

    private static bool ConfigExists(string channel) => File.Exists(Storage.PathOf(DohRelayConfig.FileNameFor(channel)));
    private static bool ErrorExists(string channel) => File.Exists(Storage.PathOf(DohRelayConfig.ErrorFileNameFor(channel)));

    /// <summary>Все служебные файлы посредников: и по каналам, и общий файл прежних сборок.</summary>
    private static string[] RelayFiles() => Directory.Exists(Storage.Dir)
        ? Directory.GetFiles(Storage.Dir, "doh-relay*.json") : Array.Empty<string>();

    private static void RemoveFiles()
    {
        foreach (var path in RelayFiles())
        {
            try { File.Delete(path); }
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
            Assert.True(ConfigExists(session.Channel));
            var saved = DohRelayConfig.Load(session.Channel)!;
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
            Assert.Empty(RelayFiles());
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
            WriteErrorFile(config.Channel);
            var calls = new List<string>();
            DohRelayRuntime.StopCommand = (channel, _) => { calls.Add("command:" + channel); return Task.FromResult(true); };
            DohRelayRuntime.StopHelper = (channel, _) => { calls.Add("task:" + channel); return Task.FromResult(true); };
            await DohRelayRuntime.StopAsync(Session(config.Channel), _ => { });
            Assert.Equal(new[] { "command:" + config.Channel, "task:" + config.Channel }, calls);
            Assert.False(ConfigExists(config.Channel));
            Assert.False(ErrorExists(config.Channel));
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
            WriteErrorFile(config.Channel);
            DohRelayRuntime.StopCommand = (_, _) => throw new IOException("процесс уже закрыт");
            DohRelayRuntime.StopHelper = (_, _) => throw new InvalidOperationException("задание уже удалено");
            await DohRelayRuntime.StopAsync(Session(config.Channel), log.Add);
            Assert.NotEmpty(log);
            Assert.False(ConfigExists(config.Channel));
            Assert.False(ErrorExists(config.Channel));
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
            WriteErrorFile(config.Channel);
            DohRelayRuntime.StopCommand = (channel, _) => { calls.Add("command:" + channel); return Task.FromResult(true); };
            DohRelayRuntime.StopHelper = (channel, _) => { calls.Add("task:" + channel); return Task.FromResult(true); };
            await DohRelayRuntime.CleanupStaleAsync(null, log.Add);
            Assert.Equal(new[] { "command:" + config.Channel, "task:" + config.Channel }, calls);
            Assert.Contains(log, line => line.Contains("Убран прежний локальный посредник DoH"));
            Assert.False(ConfigExists(config.Channel));
            Assert.False(ErrorExists(config.Channel));
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
            Assert.True(ConfigExists(config.Channel));
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
            // Файл ошибки без настроек (посредник уже убран) и общий файл ошибки прежних сборок.
            WriteErrorFile(Channel);
            Storage.SaveRequired(DohRelayConfig.LegacyErrorFileName, new { Kind = "test" });
            DohRelayRuntime.StopCommand = (channel, _) => { calls.Add(channel); return Task.FromResult(true); };
            DohRelayRuntime.StopHelper = (channel, _) => { calls.Add(channel); return Task.FromResult(true); };
            await DohRelayRuntime.CleanupStaleAsync(null, _ => { });
            Assert.Empty(calls);
            Assert.Empty(RelayFiles());
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
            Assert.Equal(session.Channel, DohRelayConfig.Load(session.Channel)!.Channel);
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
            Assert.True(ConfigExists(config.Channel));
            Assert.Equal(config.Channel, DohRelayConfig.Load(config.Channel)!.Channel);
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
                    // Имя задания: «CHIMERION DoH <канал>»; посредник пишет ошибку в файл своего канала.
                    WriteErrorFile(args[2]["CHIMERION DoH ".Length..]);
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

    [Fact]
    public async Task Failed_switch_keeps_the_configuration_of_the_running_relay()
    {
        var oldStart = DohRelayRuntime.StartHelper;
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        try
        {
            RemoveFiles();
            // Работает прежний посредник; новый на всех адресах не запускается (как при отказе смены сервера).
            var running = SaveConfig();
            DohRelayRuntime.StartHelper = (_, _) => Task.FromResult(false);
            DohRelayRuntime.StopCommand = (_, _) => Task.FromResult(false);
            DohRelayRuntime.StopHelper = (_, _) => Task.FromResult(true);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DohRelayRuntime.StartAsync(Endpoint, Upstream, _ => { }, CancellationToken.None));
            // После перезагрузки задание прежнего посредника должно найти свои настройки.
            var kept = DohRelayConfig.Load(running.Channel);
            Assert.NotNull(kept);
            Assert.Equal(running.ListenAddress, kept!.ListenAddress);
            Assert.Single(RelayFiles());
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
    public async Task Error_of_another_relay_does_not_abort_a_new_start()
    {
        var oldPing = DohRelayRuntime.PingHelper;
        var oldScheduler = DohRelayRuntime.Scheduler;
        var oldExe = DohRelayRuntime.ExecutablePath;
        var oldSid = DohRelayRuntime.CurrentUserSid;
        var oldTimeout = DohRelayRuntime.StartTimeout;
        var pings = 0;
        try
        {
            RemoveFiles();
            DohRelayRuntime.ExecutablePath = () => @"C:\Example\CHIMERION.exe";
            DohRelayRuntime.CurrentUserSid = () => "S-1-5-21-100-200-300-1001";
            DohRelayRuntime.StartTimeout = TimeSpan.FromSeconds(5);
            // Прежний посредник (другой канал) записал ошибку, а новый отвечает со второго опроса.
            WriteErrorFile(Channel);
            Storage.SaveRequired(DohRelayConfig.LegacyErrorFileName, new { Kind = "test" });
            DohRelayRuntime.PingHelper = (_, _) => Task.FromResult(++pings > 1);
            DohRelayRuntime.Scheduler = () => new DohRelayScheduler((args, _) => Task.FromResult(args[0] == "/Query" ? 1 : 0));
            var session = await DohRelayRuntime.StartAsync(Endpoint, Upstream, _ => { }, CancellationToken.None);
            Assert.Equal(DohRelayRuntime.CandidateAddresses[0], session.ListenAddress);
        }
        finally
        {
            DohRelayRuntime.PingHelper = oldPing;
            DohRelayRuntime.Scheduler = oldScheduler;
            DohRelayRuntime.ExecutablePath = oldExe;
            DohRelayRuntime.CurrentUserSid = oldSid;
            DohRelayRuntime.StartTimeout = oldTimeout;
            RemoveFiles();
        }
    }

    [Fact]
    public async Task Cleanup_stops_every_stale_relay_and_the_legacy_one_but_keeps_the_active()
    {
        var oldStop = DohRelayRuntime.StopHelper;
        var oldStopCommand = DohRelayRuntime.StopCommand;
        var stopped = new List<string>();
        try
        {
            RemoveFiles();
            var active = SaveConfig();
            var stale = SaveConfig();
            // Общий файл прежних сборок с третьим каналом.
            var legacy = DohRelayConfig.Create(Endpoint, IPAddress.Parse(DohRelayRuntime.CandidateAddresses[1]), Upstream);
            Storage.SaveRequired(DohRelayConfig.LegacyFileName, legacy);
            DohRelayRuntime.StopCommand = (_, _) => Task.FromResult(true);
            DohRelayRuntime.StopHelper = (channel, _) => { stopped.Add(channel); return Task.FromResult(true); };
            await DohRelayRuntime.CleanupStaleAsync(Session(active.Channel), _ => { });
            Assert.Equal(new[] { legacy.Channel, stale.Channel }.OrderBy(c => c), stopped.OrderBy(c => c));
            Assert.True(ConfigExists(active.Channel));
            Assert.False(ConfigExists(stale.Channel));
            Assert.False(File.Exists(Storage.PathOf(DohRelayConfig.LegacyFileName)));
        }
        finally
        {
            DohRelayRuntime.StopHelper = oldStop;
            DohRelayRuntime.StopCommand = oldStopCommand;
            RemoveFiles();
        }
    }
}
