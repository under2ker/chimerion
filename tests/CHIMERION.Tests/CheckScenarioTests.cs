using System.Net;
using Dnex.Models;
using Dnex.Services;
using Probe = Dnex.Services.ServerChecker.Probe;

namespace Dnex.Tests;

/// <summary>
/// Проверка сервера без сети: ответы DNS, эталона и HTTPS задаются по сервису. Так видно, как из сбоев отдельных
/// шагов складывается итог: статус, плашки сервисов, стабильность и показатели.
/// </summary>
internal sealed class FakeChecker : ServerChecker
{
    public static readonly IPAddress Real = IPAddress.Parse("104.18.0.1");   // настоящий адрес сервиса (эталон)
    public static readonly IPAddress Proxy = IPAddress.Parse("185.0.0.7");   // адрес прокси Smart DNS

    /// <summary>Ответ DNS по хосту. Null — подменить на прокси. Исключение — сбой запроса.</summary>
    public Func<string, CancellationToken, Task<List<IPAddress>>> Resolve { get; set; } = (_, _) => Task.FromResult(new List<IPAddress> { Proxy });
    /// <summary>Ответ HTTPS по хосту API или сайта.</summary>
    public Func<string, Probe> Https { get; set; } = _ => Probe.Ok;
    public int ResolveCalls;

    public FakeChecker(AppSettings? st = null) : base(st ?? new AppSettings()) { }

    public override async Task<(List<IPAddress> Ips, long Ms)> ResolveViaAsync(DnsServer s, string host, CancellationToken ct)
    {
        Interlocked.Increment(ref ResolveCalls);
        return (await Resolve(host, ct), 40);
    }

    internal override Task<HashSet<IPAddress>> ResolveReferenceAsync(string host, CancellationToken ct) =>
        Task.FromResult(new HashSet<IPAddress> { Real });

    internal override Task<bool> IsOwnedByAsync(string ip, IReadOnlyCollection<string> orgs, CancellationToken ct) => Task.FromResult(false);

    internal override Task<Egress?> TraceAsync(IPAddress ip, string host, CancellationToken ct) => Task.FromResult<Egress?>(null);

    internal override Task<(Probe, string)> ProbeHttpsAsync(IPAddress ip, CheckTarget t, CancellationToken ct, bool isWeb = false)
    {
        var p = Https(t.Host);
        return Task.FromResult((p, p switch { Probe.Ok => "HTTP 401", Probe.Blocked => "HTTP 403, доступ закрыт", _ => "HTTP 502, сервис не отвечает нормально" }));
    }
}

[Collection("scenarios")]
public class CheckScenarioTests
{
    private static DnsServer NewServer() => new() { Name = "Проверяемый", Primary = "203.0.113.50" };

    [Fact]
    public async Task All_services_open_through_proxy()
    {
        var s = NewServer();
        await new FakeChecker().CheckAsync(s, CancellationToken.None);
        Assert.Equal(ServerStatus.Ok, s.Status);
        Assert.All(s.Chips, c => Assert.Equal("ok", c.State));
        Assert.Equal(FakeChecker.Proxy.ToString(), s.ProxyIp);
        Assert.Equal((1, 1), (s.ChecksOk, s.ChecksTotal));
    }

    [Fact]
    public async Task Dns_timeout_marks_server_failed_without_hanging()
    {
        var s = NewServer();
        var c = new FakeChecker { Resolve = (_, _) => throw new TimeoutException("сервер не ответил за 4 с") };
        await c.CheckAsync(s, CancellationToken.None);
        Assert.Equal(ServerStatus.Fail, s.Status);
        Assert.All(s.Chips, ch => Assert.Equal("fail", ch.State));
        Assert.Contains("не ответил", s.LastError);
        Assert.Null(s.LatencyMs);
        var m = ServerMetrics.Of(s);
        Assert.Equal(0, m.Availability); // в отличие от «отвечает, но закрыто»: сервер молчит
    }

    [Fact]
    public async Task One_service_failing_while_others_work_is_partial()
    {
        var s = NewServer();
        await new FakeChecker { Https = host => host.Contains("anthropic") || host.Contains("claude") ? Probe.Blocked : Probe.Ok }
            .CheckAsync(s, CancellationToken.None);
        Assert.Equal(ServerStatus.Partial, s.Status);
        Assert.Equal("region", s.Chips.Single(c => c.Name == "Claude").State);
        Assert.Equal("ok", s.Chips.Single(c => c.Name == "ChatGPT").State);
        Assert.Contains("Claude", s.LastError);
        Assert.Equal(1, s.ChecksOk); // частично рабочий сервер считается успешной проверкой
        Assert.Equal(0, s.ServiceOk.GetValueOrDefault("Claude"));
        Assert.Equal(1, s.ServiceOk.GetValueOrDefault("ChatGPT"));
        var m = ServerMetrics.Of(s);
        Assert.Equal((2, 3), (m.CompatibleNow, m.ServicesTotal));
    }

    [Fact]
    public async Task Proxy_for_one_service_only_is_partial_not_ok()
    {
        // Подменяет адрес только ChatGPT, для остальных отдает настоящий: сервисы без подмены не засчитаны
        var s = NewServer();
        await new FakeChecker
        {
            Resolve = (host, _) => Task.FromResult(new List<IPAddress> { host.Contains("openai") || host.Contains("chatgpt") ? FakeChecker.Proxy : FakeChecker.Real }),
        }.CheckAsync(s, CancellationToken.None);
        Assert.Equal(ServerStatus.Partial, s.Status);
        Assert.Equal("ok", s.Chips.Single(c => c.Name == "ChatGPT").State);
        Assert.Contains("без подмены", s.Summary);
        Assert.False(s.NoProxy);
    }

    [Fact]
    public async Task Server_that_never_proxies_is_plain_dns()
    {
        var s = NewServer();
        await new FakeChecker { Resolve = (_, _) => Task.FromResult(new List<IPAddress> { FakeChecker.Real }) }.CheckAsync(s, CancellationToken.None);
        Assert.Equal(ServerStatus.Fail, s.Status);
        Assert.True(s.NoProxy);
        Assert.True(s.IsJunk);
        Assert.Contains("обычный DNS", s.LastError);
    }

    [Fact]
    public async Task Dns_works_but_service_unreachable_is_failed_with_reason()
    {
        // Адрес прокси выдан, но соединение с ним не проходит: DNS «работает», а пользоваться нельзя
        var s = NewServer();
        await new FakeChecker { Https = _ => Probe.Error }.CheckAsync(s, CancellationToken.None);
        Assert.Equal(ServerStatus.Fail, s.Status);
        Assert.Contains("не отвечает нормально", s.LastError);
        var m = ServerMetrics.Of(s);
        Assert.Equal(100, m.Availability);    // отвечает на DNS
        Assert.Equal(0, m.CompatibleNow);     // но ни один сервис через него не работает
        Assert.False(s.NoProxy);              // и это не обычный DNS: подмена есть
    }

    [Fact]
    public async Task Empty_or_broken_answers_are_not_counted_as_working()
    {
        var s = NewServer();
        await new FakeChecker { Resolve = (_, _) => Task.FromResult(new List<IPAddress>()) }.CheckAsync(s, CancellationToken.None);
        Assert.Equal(ServerStatus.Fail, s.Status);
        Assert.Contains("пустой ответ", s.Summary);

        var s2 = NewServer();
        await new FakeChecker { Resolve = (_, _) => throw new FormatException("ответ DNS не разбирается") }.CheckAsync(s2, CancellationToken.None);
        Assert.Equal(ServerStatus.Fail, s2.Status);
        Assert.Contains("нет DNS", s2.Summary);
    }

    [Fact]
    public async Task Cancel_during_check_does_not_leave_row_checking()
    {
        var s = NewServer();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var c = new FakeChecker
        {
            Resolve = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new List<IPAddress>(); },
        };
        var run = c.CheckAsync(s, cts.Token);
        await started.Task;
        Assert.Equal(ServerStatus.Checking, s.Status);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(ServerStatus.Unknown, s.Status);
        Assert.Equal(0, s.ChecksTotal); // отмененная проверка не портит стабильность
    }

    private sealed class SlowTraceChecker : ServerChecker
    {
        public readonly TaskCompletionSource TraceStarted = new();
        public SlowTraceChecker() : base(new AppSettings()) { }
        public override Task<(List<IPAddress> Ips, long Ms)> ResolveViaAsync(DnsServer s, string host, CancellationToken ct) =>
            Task.FromResult((new List<IPAddress> { FakeChecker.Proxy }, 30L));
        internal override Task<HashSet<IPAddress>> ResolveReferenceAsync(string host, CancellationToken ct) => Task.FromResult(new HashSet<IPAddress> { FakeChecker.Real });
        internal override Task<bool> IsOwnedByAsync(string ip, IReadOnlyCollection<string> orgs, CancellationToken ct) => Task.FromResult(false);
        internal override Task<(Probe, string)> ProbeHttpsAsync(IPAddress ip, CheckTarget t, CancellationToken ct, bool isWeb = false) => Task.FromResult((Probe.Ok, "HTTP 401"));
        internal override async Task<Egress?> TraceAsync(IPAddress ip, string host, CancellationToken ct)
        {
            TraceStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    [Fact]
    public async Task Cancel_while_tracing_egress_does_not_leave_row_checking()
    {
        // «Стоп» нажат, когда сервисы уже проверены и идет определение страны выхода
        var s = NewServer();
        var c = new SlowTraceChecker();
        using var cts = new CancellationTokenSource();
        var run = c.CheckAsync(s, cts.Token);
        await c.TraceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(ServerStatus.Unknown, s.Status);
    }

    [Fact]
    public async Task Cancel_of_batch_stops_remaining_servers()
    {
        var servers = Enumerable.Range(0, 30).Select(i => new DnsServer { Name = $"s{i}", Primary = $"203.0.113.{i + 1}" }).ToList();
        using var cts = new CancellationTokenSource();
        var c = new FakeChecker(new AppSettings { Parallelism = 2 })
        {
            Resolve = async (_, ct) => { await Task.Delay(50, ct); return new List<IPAddress> { FakeChecker.Proxy }; },
        };
        cts.CancelAfter(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.CheckManyAsync(servers, null, cts.Token));
        Assert.DoesNotContain(servers, x => x.Status == ServerStatus.Checking);
        Assert.Contains(servers, x => x.Status == ServerStatus.Unknown && x.ChecksTotal == 0);
    }

    [Fact]
    public async Task Flapping_server_gets_lower_confidence_than_steady_one()
    {
        var flappy = NewServer();
        var steady = new DnsServer { Name = "Ровный", Primary = "203.0.113.60" };
        for (int i = 0; i < 6; i++)
        {
            bool up = i % 2 == 0;
            await new FakeChecker { Https = _ => up ? Probe.Ok : Probe.Blocked }.CheckAsync(flappy, CancellationToken.None);
            await new FakeChecker { Https = _ => i < 3 ? Probe.Ok : Probe.Blocked }.CheckAsync(steady, CancellationToken.None);
        }
        // Одинаковая доля успехов (3 из 6), но один скачет через раз, другой сломался один раз и стабильно
        Assert.Equal(steady.ChecksOk, flappy.ChecksOk);
        Assert.True(ServerMetrics.ConfidenceOf(flappy) < ServerMetrics.ConfidenceOf(steady));
        Assert.Equal("низкая", ServerMetrics.Of(flappy).ConfidenceLevel);
    }

    [Fact]
    public async Task Single_service_check_does_not_touch_stability()
    {
        var s = NewServer();
        await new FakeChecker().CheckAsync(s, CancellationToken.None);
        await new FakeChecker(null) { Https = _ => Probe.Blocked }.CheckAsync(s, CancellationToken.None);
        var only = new ServerChecker.CheckOptions(Only: new[] { "Claude" });
        var c = new FakeCheckerWith(only);
        await c.CheckAsync(s, CancellationToken.None);
        Assert.Equal(2, s.ChecksTotal);
        Assert.Equal("ok", s.Chips.Single(ch => ch.Name == "Claude").State);
        Assert.Equal("region", s.Chips.Single(ch => ch.Name == "ChatGPT").State); // прежний итог сохранился
    }

    private sealed class FakeCheckerWith : ServerChecker
    {
        public FakeCheckerWith(CheckOptions o) : base(new AppSettings(), o) { }
        public override Task<(List<IPAddress> Ips, long Ms)> ResolveViaAsync(DnsServer s, string host, CancellationToken ct) =>
            Task.FromResult((new List<IPAddress> { FakeChecker.Proxy }, 30L));
        internal override Task<HashSet<IPAddress>> ResolveReferenceAsync(string host, CancellationToken ct) => Task.FromResult(new HashSet<IPAddress> { FakeChecker.Real });
        internal override Task<bool> IsOwnedByAsync(string ip, IReadOnlyCollection<string> orgs, CancellationToken ct) => Task.FromResult(false);
        internal override Task<Egress?> TraceAsync(IPAddress ip, string host, CancellationToken ct) => Task.FromResult<Egress?>(null);
        internal override Task<(Probe, string)> ProbeHttpsAsync(IPAddress ip, CheckTarget t, CancellationToken ct, bool isWeb = false) => Task.FromResult((Probe.Ok, "HTTP 401"));
    }
}

/// <summary>Решения сторожа и выбор сервера по показателям.</summary>
public class MonitorAndRankingTests
{
    private static readonly IPAddress Ours = IPAddress.Parse("203.0.113.10");
    private static DnsSwitcher.ActiveConnection Active(bool split = false, params string[] adapters) => new()
    {
        ServerId = Guid.NewGuid(), Name = "Тест", Addresses = { "203.0.113.10" }, Split = split,
        AdapterAddresses = adapters.ToList(),
    };

    [Fact]
    public async Task Watchdog_detects_dns_reset_on_one_of_two_adapters()
    {
        var m = new ConnectionMonitor();
        var adapters = new List<IReadOnlyCollection<IPAddress>>
        {
            new List<IPAddress> { Ours },
            new List<IPAddress> { IPAddress.Parse("192.0.2.1") },
        };
        var current = adapters.SelectMany(a => a).ToList();
        bool probed = false;
        var result = await m.TickAsync(Active(), true, current, true, () => { probed = true; return Task.FromResult(true); }, adapters: adapters);
        Assert.Equal(ConnectionMonitor.Verdict.Restore, result);
        Assert.False(probed);
        adapters[1] = new List<IPAddress> { Ours };
        Assert.Equal(ConnectionMonitor.Verdict.Ok, await new ConnectionMonitor().TickAsync(Active(), true, current, true, () => Task.FromResult(true), adapters: adapters));
        Assert.False(ConnectionMonitor.NeedsRestore(Active(split: true), current, true, out _, adapters));
    }

    [Fact]
    public async Task No_network_waits_and_is_not_a_failure()
    {
        var m = new ConnectionMonitor();
        var a = Active();
        bool asked = false;
        Assert.Equal(ConnectionMonitor.Verdict.WaitNetwork, await m.TickAsync(a, false, new List<IPAddress>(), true, () => { asked = true; return Task.FromResult(false); }));
        Assert.True(m.FirstNoNetwork);
        Assert.Equal(ConnectionMonitor.Verdict.WaitNetwork, await m.TickAsync(a, false, new List<IPAddress>(), true, () => Task.FromResult(false)));
        Assert.False(m.FirstNoNetwork); // в журнал один раз
        Assert.False(asked);
        Assert.Equal(0, m.Fails);
    }

    [Fact]
    public async Task Three_silent_ticks_switch_server_and_success_resets()
    {
        var m = new ConnectionMonitor();
        var a = Active();
        var cur = new List<IPAddress> { Ours };
        Assert.Equal(ConnectionMonitor.Verdict.Failing, await m.TickAsync(a, true, cur, true, () => Task.FromResult(false)));
        Assert.Equal(ConnectionMonitor.Verdict.Ok, await m.TickAsync(a, true, cur, true, () => Task.FromResult(true)));
        Assert.Equal(0, m.Fails);
        Assert.Equal(ConnectionMonitor.Verdict.Failing, await m.TickAsync(a, true, cur, true, () => Task.FromResult(false)));
        Assert.Equal(ConnectionMonitor.Verdict.Failing, await m.TickAsync(a, true, cur, true, () => Task.FromResult(false)));
        Assert.Equal(ConnectionMonitor.Verdict.SwitchServer, await m.TickAsync(a, true, cur, true, () => Task.FromResult(false)));
        Assert.Equal(0, m.Fails);
    }

    [Fact]
    public async Task Network_drop_between_failures_resets_counter()
    {
        var m = new ConnectionMonitor();
        var a = Active();
        var cur = new List<IPAddress> { Ours };
        await m.TickAsync(a, true, cur, true, () => Task.FromResult(false));
        await m.TickAsync(a, true, cur, true, () => Task.FromResult(false));
        await m.TickAsync(a, false, cur, true, () => Task.FromResult(false)); // сон ноутбука
        Assert.Equal(ConnectionMonitor.Verdict.Failing, await m.TickAsync(a, true, cur, true, () => Task.FromResult(false)));
    }

    [Fact]
    public async Task Reset_dns_is_restored_before_counting_failures()
    {
        var m = new ConnectionMonitor();
        bool asked = false;
        var v = await m.TickAsync(Active(), true, new List<IPAddress> { IPAddress.Parse("8.8.8.8") }, true, () => { asked = true; return Task.FromResult(true); });
        Assert.Equal(ConnectionMonitor.Verdict.Restore, v);
        Assert.False(asked);
    }

    [Fact]
    public void Split_mode_checks_rules_and_general_dns()
    {
        // «Два DNS» без общего DNS: адаптеры не менялись, проверяются только правила NRPT
        var noGeneral = Active(split: true);
        Assert.False(ConnectionMonitor.NeedsRestore(noGeneral, new List<IPAddress> { IPAddress.Parse("192.168.1.1") }, true, out _));
        Assert.True(ConnectionMonitor.NeedsRestore(noGeneral, new List<IPAddress>(), false, out var lost));
        Assert.True(lost);
        // С общим DNS: на адаптерах должен стоять он, а не Smart DNS
        var general = Active(true, "198.51.100.1");
        Assert.False(ConnectionMonitor.NeedsRestore(general, new List<IPAddress> { IPAddress.Parse("198.51.100.1") }, true, out _));
        Assert.True(ConnectionMonitor.NeedsRestore(general, new List<IPAddress> { Ours }, true, out _));
    }

    private static DnsServer Srv(string name, int ok, int total, long ping, DateTime? checkedAt = null, bool fav = false, bool flappy = false)
    {
        var s = new DnsServer { Name = name, Primary = "198.51.100." + (name.GetHashCode() & 0x7F), Status = ServerStatus.Ok, LatencyMs = ping, IsFavorite = fav };
        s.ChecksOk = ok; s.ChecksTotal = total; s.ServiceChecks = total; s.AllOk = ok;
        foreach (var n in new[] { "ChatGPT", "Claude", "Gemini" }) s.ServiceOk[n] = ok;
        s.Summary = "ChatGPT ✓ · Claude ✓ · Gemini ✓";
        var t0 = (checkedAt ?? DateTime.Now).AddHours(-total);
        for (int i = 0; i < total; i++) s.AddHistory(flappy ? i % 2 == 0 : i < ok, ping, t0.AddHours(i));
        s.LastChecked = checkedAt ?? DateTime.Now;
        return s;
    }

    [Fact]
    public void Best_pick_prefers_confidence_over_a_single_lucky_check()
    {
        var once = Srv("Один раз", 1, 1, 20);
        var proven = Srv("Проверенный", 19, 20, 60);
        // Одинаковая стабильность 100% против 95% решает в пользу первого, поэтому сравниваем при равной: 10 из 10 и 1 из 1
        var ten = Srv("Десять", 10, 10, 60);
        var picks = ServerRanking.PickBest(new[] { once, ten, proven }, new[] { "ChatGPT", "Claude", "Gemini" });
        Assert.All(picks, p => Assert.Same(ten, p.Best));
    }

    [Fact]
    public void Replacement_skips_current_junk_and_prefers_fresh_results()
    {
        var active = Active();
        var current = Srv("Текущий", 10, 10, 10); current.Id = active.ServerId;
        var junk = Srv("Мусор", 0, 3, 10); junk.FailStreak = 3;
        var stale = Srv("Давний", 10, 10, 10, DateTime.Now.AddDays(-3));
        var fresh = Srv("Свежий", 10, 10, 50);
        var list = ConnectionMonitor.Replacements(new[] { current, junk, stale, fresh }, active);
        Assert.Equal(new[] { fresh, stale }, list);
    }

    [Fact]
    public void Metrics_separate_what_one_score_hides()
    {
        var s = Srv("Сервер", 8, 10, 45, DateTime.Now.AddDays(-2));
        s.Summary = "ChatGPT ✓ · Claude ✗ регион · Gemini ✓";
        var m = ServerMetrics.Of(s);
        Assert.Equal(100, m.Availability);
        Assert.Equal((2, 3), (m.CompatibleNow, m.ServicesTotal));
        Assert.Equal(80, m.Stability);
        Assert.True(m.Stale);
        var text = m.Describe();
        Assert.Contains("работает 2 из 3 сервисов", text);
        Assert.Contains("результат мог устареть", text);
        Assert.Contains("Уверенность", text);
    }

    [Fact]
    public void Unchecked_server_has_no_confidence()
    {
        var m = ServerMetrics.Of(new DnsServer { Name = "Новый", Primary = "198.51.100.9" });
        Assert.Null(m.Availability);
        Assert.Equal(0, m.Confidence);
        Assert.True(m.Stale);
        Assert.Contains("не проверялся", m.Describe());
    }
}

/// <summary>Сторож и сторонний VPN или программа, которая меняет DNS следом за ним.</summary>
public class MonitorVpnAndConflictTests
{
    private static readonly IPAddress Ours = IPAddress.Parse("203.0.113.10");
    private static readonly List<IPAddress> Reset = new() { IPAddress.Parse("10.8.0.1") };
    private static DnsSwitcher.ActiveConnection Active() => new() { ServerId = Guid.NewGuid(), Name = "Тест", Addresses = { "203.0.113.10" } };

    [Fact]
    public async Task Vpn_that_changed_dns_is_not_fought()
    {
        var m = new ConnectionMonitor();
        var v = await m.TickAsync(Active(), true, Reset, true, () => Task.FromResult(true), vpnActive: true);
        Assert.Equal(ConnectionMonitor.Verdict.VpnHolds, v);
        Assert.True(m.FirstVpnHold);
        Assert.Equal(ConnectionMonitor.Verdict.VpnHolds, await m.TickAsync(Active(), true, Reset, true, () => Task.FromResult(true), true));
        Assert.False(m.FirstVpnHold); // в журнал один раз
    }

    [Fact]
    public async Task Vpn_blocking_dns_does_not_make_watchdog_switch_servers()
    {
        var m = new ConnectionMonitor();
        var cur = new List<IPAddress> { Ours };
        for (int i = 0; i < 5; i++)
            Assert.Equal(ConnectionMonitor.Verdict.VpnHolds, await m.TickAsync(Active(), true, cur, true, () => Task.FromResult(false), true));
        Assert.Equal(0, m.Fails);
    }

    [Fact]
    public async Task Vpn_that_does_not_interfere_is_ignored_and_turning_it_off_resumes_restore()
    {
        var m = new ConnectionMonitor();
        var a = Active();
        Assert.Equal(ConnectionMonitor.Verdict.Ok, await m.TickAsync(a, true, new List<IPAddress> { Ours }, true, () => Task.FromResult(true), true));
        Assert.Equal(ConnectionMonitor.Verdict.VpnHolds, await m.TickAsync(a, true, Reset, true, () => Task.FromResult(true), true));
        // VPN выключили, а DNS так и остался чужим: теперь сторож возвращает подключение
        Assert.Equal(ConnectionMonitor.Verdict.Restore, await m.TickAsync(a, true, Reset, true, () => Task.FromResult(true), false));
    }

    [Fact]
    public async Task Program_that_keeps_resetting_dns_stops_the_watchdog()
    {
        var t = new DateTime(2026, 10, 26, 12, 0, 0);
        var m = new ConnectionMonitor { Now = () => t };
        var a = Active();
        for (int i = 0; i < ConnectionMonitor.MaxRestores; i++)
        {
            Assert.Equal(ConnectionMonitor.Verdict.Restore, await m.TickAsync(a, true, Reset, true, () => Task.FromResult(true)));
            m.Reset(); // так делает окно после каждого возврата: счетчик возвратов при этом не теряется
            t = t.AddMinutes(2);
        }
        Assert.Equal(ConnectionMonitor.Verdict.Conflict, await m.TickAsync(a, true, Reset, true, () => Task.FromResult(true)));
    }

    [Fact]
    public async Task Rare_resets_are_restored_every_time()
    {
        var t = new DateTime(2026, 10, 26, 12, 0, 0);
        var m = new ConnectionMonitor { Now = () => t };
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(ConnectionMonitor.Verdict.Restore, await m.TickAsync(Active(), true, Reset, true, () => Task.FromResult(true)));
            t = t.AddMinutes(10); // раз в 10 минут: за 15 минут не больше двух
        }
    }

    [Fact]
    public async Task Manual_connect_forgets_old_restores()
    {
        var m = new ConnectionMonitor();
        for (int i = 0; i < ConnectionMonitor.MaxRestores; i++) await m.TickAsync(Active(), true, Reset, true, () => Task.FromResult(true));
        m.ForgetRestores();
        Assert.Equal(ConnectionMonitor.Verdict.Restore, await m.TickAsync(Active(), true, Reset, true, () => Task.FromResult(true)));
    }
}

/// <summary>Фоновая проверка сервисов: когда сообщать о падении и восстановлении.</summary>
public class ServiceHealthTests
{
    private static ServiceState Svc(string name, string access, bool proxied = true, string ip = "83.217.211.149") => new(name, "api", ip, proxied, access);
    private static readonly ServiceHealth.Problem Ok = ServiceHealth.Problem.None;

    [Theory]
    [InlineData("✓ доступен", true, "83.217.211.149", ServiceHealth.Problem.None)]
    [InlineData("✗ нет ответа", true, "не резолвится", ServiceHealth.Problem.NoDns)]
    [InlineData("✗ доступ закрыт", false, "160.79.104.10", ServiceHealth.Problem.NotProxied)]
    [InlineData("✗ доступ закрыт", true, "83.217.211.149", ServiceHealth.Problem.Closed)]
    [InlineData("✗ сервис не отвечает", true, "83.217.211.149", ServiceHealth.Problem.NoResponse)]
    public void Failure_reason_is_recognized(string access, bool proxied, string ip, ServiceHealth.Problem expected) =>
        Assert.Equal(expected, ServiceHealth.Classify(Svc("ChatGPT", access, proxied, ip)));

    [Fact]
    public void One_failed_check_is_not_an_alert_two_are()
    {
        var h = new ServiceHealth();
        Assert.Empty(h.Update(new[] { ("ChatGPT", Ok), ("Claude", Ok) }));
        Assert.Empty(h.Update(new[] { ("ChatGPT", ServiceHealth.Problem.NotProxied), ("Claude", Ok) }));
        Assert.True(h.Suspicious); // повторная проверка пойдет через минуту
        var ch = Assert.Single(h.Update(new[] { ("ChatGPT", ServiceHealth.Problem.NotProxied), ("Claude", Ok) }));
        Assert.True(ch.Down);
        Assert.Equal("ChatGPT", ch.Service);
        Assert.False(h.Suspicious);
        // Пока сервис лежит, повторных уведомлений нет
        Assert.Empty(h.Update(new[] { ("ChatGPT", ServiceHealth.Problem.NotProxied), ("Claude", Ok) }));
    }

    [Fact]
    public void Flapping_once_does_not_alert()
    {
        var h = new ServiceHealth();
        h.Update(new[] { ("Gemini", ServiceHealth.Problem.Closed) });
        Assert.Empty(h.Update(new[] { ("Gemini", Ok) }));
        Assert.Empty(h.Update(new[] { ("Gemini", ServiceHealth.Problem.Closed) }));
    }

    [Fact]
    public void Service_closed_from_the_start_is_not_reported_as_fallen()
    {
        // Журнал пользователя: Gemini на Zapret DNS был закрыт сразу после подключения, а через 6 минут пришло
        // «Gemini перестал открываться». Он не переставал: сообщаем только о том, что открывалось в этом подключении
        var h = new ServiceHealth();
        for (int i = 0; i < 5; i++)
            Assert.Empty(h.Update(new[] { ("ChatGPT", Ok), ("Gemini", ServiceHealth.Problem.Closed) }));
        Assert.False(h.Suspicious); // и проверка не ускоряется до раза в минуту из-за него
    }

    [Fact]
    public void Recovery_is_reported_once()
    {
        var h = new ServiceHealth();
        h.Update(new[] { ("Claude", Ok) });
        h.Update(new[] { ("Claude", ServiceHealth.Problem.NoResponse) });
        h.Update(new[] { ("Claude", ServiceHealth.Problem.NoResponse) });
        var up = Assert.Single(h.Update(new[] { ("Claude", Ok) }));
        Assert.False(up.Down);
        Assert.Empty(h.Update(new[] { ("Claude", Ok) }));
    }

    [Fact]
    public void Silent_server_is_reported_after_two_checks_and_its_return_too()
    {
        var h = new ServiceHealth();
        Assert.Null(h.UpdateServer(false));
        Assert.True(h.Suspicious);
        Assert.True(h.UpdateServer(false));
        Assert.True(h.ServerDown);
        Assert.Null(h.UpdateServer(false));
        Assert.False(h.UpdateServer(true));
        Assert.Null(h.UpdateServer(true));
    }

    [Fact]
    public void New_connection_starts_clean()
    {
        var h = new ServiceHealth();
        h.Update(new[] { ("ChatGPT", ServiceHealth.Problem.NoDns) });
        h.Reset();
        Assert.Empty(h.Update(new[] { ("ChatGPT", ServiceHealth.Problem.NoDns) }));
    }

    [Fact]
    public void Notification_text_groups_services_by_reason()
    {
        var down = new[]
        {
            new ServiceHealth.Change("ChatGPT", true, ServiceHealth.Problem.NotProxied),
            new ServiceHealth.Change("Claude", true, ServiceHealth.Problem.NotProxied),
            new ServiceHealth.Change("Gemini", true, ServiceHealth.Problem.Closed),
        };
        var t = ServiceHealth.DownText("kayonedns", down);
        Assert.Equal("kayonedns: ChatGPT и Claude перестали открываться (сервер перестал подменять адреса); Gemini перестал открываться (доступ закрыт, прокси сервера попал под блокировку)", t);
        Assert.Equal("kayonedns: ChatGPT снова открывается", ServiceHealth.UpText("kayonedns", new[] { new ServiceHealth.Change("ChatGPT", false, ServiceHealth.Problem.None) }));
    }
}
