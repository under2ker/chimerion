using System.Net;
using System.Text.Json;
using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Сценарные тесты меняют статические точки подмены программы, поэтому идут строго по одному.</summary>
[CollectionDefinition("scenarios", DisableParallelization = true)]
public class ScenarioCollection { }

/// <summary>
/// Модель сети Windows для скриптов DnsSwitcher. Настоящие скрипты подключения и отключения выполняются в PowerShell,
/// но команды, которые меняют сеть (Set-DnsClientServerAddress, NRPT, DoH, привязка IPv6, реестр), заменены
/// функциями с тем же именем: функции PowerShell важнее командлетов. Состояние адаптеров лежит в JSON-файле и
/// переживает запуск нескольких процессов PowerShell подряд (резервная копия, подключение, откат, отключение).
/// Настоящая сеть компьютера не меняется: кроме подмены, тесты запускаются без прав администратора.
/// </summary>
public sealed class NetSim : IDisposable
{
    public sealed class Adapter
    {
        public string name { get; set; } = "Sim-Ethernet";
        public int index { get; set; } = 12;
        public string guid { get; set; } = "{11111111-1111-1111-1111-111111111111}";
        public string status { get; set; } = "Up";
        public bool physical { get; set; } = true;
        public List<string> dhcp4 { get; set; } = new() { "192.168.1.1" };
        public List<string> dhcp6 { get; set; } = new();
        public List<string> dns4 { get; set; } = new() { "192.168.1.1" };
        public List<string> dns6 { get; set; } = new();
        public string static4 { get; set; } = "";
        public string static6 { get; set; } = "";
        public bool v6 { get; set; } = true;
        public bool failSet { get; set; }
        /// <summary>Драйвер молча не включает IPv6 обратно.</summary>
        public bool failV6On { get; set; }
    }

    public sealed class Rule
    {
        public string name { get; set; } = "";
        public string comment { get; set; } = "";
        public List<string> ns { get; set; } = new();
        public List<string> servers { get; set; } = new();
    }

    public sealed class Doh
    {
        public string addr { get; set; } = "";
        public string template { get; set; } = "";
    }

    public sealed class State
    {
        public List<Adapter> adapters { get; set; } = new();
        public List<Rule> nrpt { get; set; } = new();
        public List<Doh> doh { get; set; } = new();
    }

    private readonly string _file = Path.Combine(TestSetup.DataDir, $"netsim-{Guid.NewGuid():N}.json");
    private readonly Func<string, bool, Task<(int, string)>> _prevRunner = DnsSwitcher.Runner;
    private readonly Func<bool> _prevV6 = DnsSwitcher.Ipv6Internet;

    /// <summary>Скрипты, которые программа запускала, по порядку: резервная копия, подключение, отключение.</summary>
    public List<string> Scripts { get; } = new();

    /// <summary>Перехват перед выполнением скрипта: можно бросить исключение (процесс не запустился) или зависнуть.</summary>
    public Func<string, Task>? Before { get; set; }

    public NetSim(State state)
    {
        foreach (var f in new[] { "active.json", "dns-backup.json", "ipv6-disabled.txt" })
            File.Delete(Path.Combine(TestSetup.DataDir, f));
        Save(state);
        DnsSwitcher.Ipv6Internet = () => false;
        DnsSwitcher.Runner = async (script, _) =>
        {
            Scripts.Add(script);
            if (Before != null) await Before(script);
            return await RunReal(script);
        };
    }

    /// <summary>Скрипт вместе с подменами: для разбора сбоев вручную.</summary>
    public string WithMocks(string script) => Mocks + Environment.NewLine + script;

    /// <summary>Выполнить скрипт программы в модели сети.</summary>
    public Task<(int Code, string Output)> RunReal(string script) => DnsSwitcher.RunPsAsync(Mocks + "\n" + (NoDoh ? NoDohScript : "") + script);

    // Команд DoH «нет»: Get-Command их не находит, а прямой вызов падает так же, как на такой Windows.
    // Удалять подмены нельзя: тогда Get-Command подгрузил бы настоящий модуль DnsClient
    private const string NoDohScript = """
        foreach ($c in 'Get-DnsClientDohServerAddress', 'Add-DnsClientDohServerAddress', 'Remove-DnsClientDohServerAddress') {
          Set-Item -Path "function:\$c" -Value ([scriptblock]::Create("throw [System.Management.Automation.CommandNotFoundException]::new('Имя ""$c"" не распознано как имя командлета')")) }
        function Get-Command { [CmdletBinding()] param([Parameter(Position = 0)][string]$Name)
          if ($Name -like '*DnsClientDoh*') { return }
          Microsoft.PowerShell.Core\Get-Command @PSBoundParameters }

        """;

    /// <summary>Windows без команд DoH (Windows 10 до сборки 19628): их в системе просто нет.</summary>
    public bool NoDoh { get; init; }

    public State Load() => JsonSerializer.Deserialize<State>(File.ReadAllText(_file))!;
    public void Save(State s) => File.WriteAllText(_file, JsonSerializer.Serialize(s));
    public void Edit(Action<State> change) { var s = Load(); change(s); Save(s); }
    public Adapter this[string name] => Load().adapters.Single(a => a.name == name);

    /// <summary>DNS всех адаптеров, которые сейчас «работают»: так их видит программа через NetworkInterface.</summary>
    public List<IPAddress> CurrentDns() => Load().adapters.Where(a => a.status == "Up")
        .SelectMany(a => a.dns4.Concat(a.dns6)).Select(IPAddress.Parse).ToList();

    public void Dispose()
    {
        DnsSwitcher.Runner = _prevRunner;
        DnsSwitcher.Ipv6Internet = _prevV6;
        File.Delete(_file);
        foreach (var f in new[] { "active.json", "dns-backup.json", "ipv6-disabled.txt" })
            File.Delete(Path.Combine(TestSetup.DataDir, f));
    }

    private string Mocks => """
        Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility
        # Каждая команда модулей сети (DnsClient, NetAdapter, NetTCPIP) перекрывается заглушкой, которая падает.
        # Функции важнее командлетов, поэтому команда без подмены не выполнится, даже если тесты идут с правами
        # администратора. Просто не загружать модули мало: Get-Command подгружает их и при выключенной автозагрузке
        $PSModuleAutoLoadingPreference = 'None'
        foreach ($m in 'DnsClient', 'NetAdapter', 'NetTCPIP') {
          Import-Module $m -ErrorAction Stop
          foreach ($c in (Get-Module $m).ExportedCommands.Keys) {
            Set-Item -Path "function:\$c" -Value ([scriptblock]::Create("throw 'модель сети: команда $c не подменена'")) } }
        $SimFile = '__FILE__'
        function SimLoad { Get-Content -LiteralPath $SimFile -Raw -Encoding UTF8 | ConvertFrom-Json }
        function SimSave($s) { $s | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $SimFile -Encoding UTF8 }
        function SimByIndex($s, $i) { $s.adapters | Where-Object { $_.index -eq $i } | Select-Object -First 1 }
        function SimByName($s, $n) { $s.adapters | Where-Object { $_.name -eq $n } | Select-Object -First 1 }
        function Get-NetAdapter { [CmdletBinding()] param([switch]$Physical, [switch]$IncludeHidden, [string]$Name)
          foreach ($a in (SimLoad).adapters) {
            if ($Name -and $a.name -ne $Name) { continue }
            if ($Physical -and -not $a.physical) { continue }
            [pscustomobject]@{ Name = $a.name; ifIndex = $a.index; InterfaceGuid = $a.guid; Status = $a.status } } }
        function Get-ItemProperty { [CmdletBinding()] param([Parameter(Position = 0)][string]$Path, [string]$Name)
          foreach ($a in (SimLoad).adapters) {
            if ($Path -notlike "*$($a.guid)*") { continue }
            if ($Path -like '*Tcpip6*') { if ($a.static6) { return [pscustomobject]@{ NameServer = $a.static6 } } }
            elseif ($a.static4) { return [pscustomobject]@{ NameServer = $a.static4 } } } }
        function Get-DnsClientServerAddress { [CmdletBinding()] param([int]$InterfaceIndex, [string]$AddressFamily)
          $a = SimByIndex (SimLoad) $InterfaceIndex
          if (-not $a) { return }
          if (-not $AddressFamily -or $AddressFamily -eq 'IPv4') { [pscustomobject]@{ ServerAddresses = @($a.dns4) } }
          if (-not $AddressFamily -or $AddressFamily -eq 'IPv6') { [pscustomobject]@{ ServerAddresses = @($a.dns6) } } }
        function Set-DnsClientServerAddress { [CmdletBinding()] param([int]$InterfaceIndex, [switch]$ResetServerAddresses, [string[]]$ServerAddresses)
          $s = SimLoad; $a = SimByIndex $s $InterfaceIndex
          if ($a.failSet) { throw "сбой установки DNS на адаптере $($a.name)" }
          if ($ResetServerAddresses) { $a.dns4 = @($a.dhcp4); $a.dns6 = @($a.dhcp6); $a.static4 = ''; $a.static6 = '' }
          else {
            $v4 = @($ServerAddresses | Where-Object { $_ -notmatch ':' }); $v6 = @($ServerAddresses | Where-Object { $_ -match ':' })
            if ($v4.Count -gt 0) { $a.dns4 = $v4; $a.static4 = $v4 -join ',' }
            if ($v6.Count -gt 0) { $a.dns6 = $v6; $a.static6 = $v6 -join ',' } }
          SimSave $s }
        function Get-NetAdapterBinding { [CmdletBinding()] param([string]$Name, [string]$ComponentID)
          $a = SimByName (SimLoad) $Name; if ($a) { [pscustomobject]@{ Enabled = [bool]$a.v6 } } }
        function Disable-NetAdapterBinding { [CmdletBinding(SupportsShouldProcess)] param([string]$Name, [string]$ComponentID)
          $s = SimLoad; (SimByName $s $Name).v6 = $false; SimSave $s }
        function Enable-NetAdapterBinding { [CmdletBinding(SupportsShouldProcess)] param([string]$Name, [string]$ComponentID)
          $s = SimLoad; $a = SimByName $s $Name; if (-not $a.failV6On) { $a.v6 = $true }; SimSave $s }
        function Get-DnsClientDohServerAddress { [CmdletBinding()] param([string]$ServerAddress)
          (SimLoad).doh | Where-Object { -not $ServerAddress -or $_.addr -eq $ServerAddress } | ForEach-Object { [pscustomobject]@{ ServerAddress = $_.addr; DohTemplate = $_.template } } }
        function Add-DnsClientDohServerAddress { [CmdletBinding()] param([string]$ServerAddress, [string]$DohTemplate, $AllowFallbackToUdp, $AutoUpgrade)
          $s = SimLoad; $s.doh = @($s.doh) + @([pscustomobject]@{ addr = $ServerAddress; template = $DohTemplate }); SimSave $s; 'ok' }
        function Remove-DnsClientDohServerAddress { [CmdletBinding()] param([string]$ServerAddress)
          $s = SimLoad; $s.doh = @($s.doh | Where-Object { $_.addr -ne $ServerAddress }); SimSave $s }
        function Get-DnsClientNrptRule { [CmdletBinding()] param()
          (SimLoad).nrpt | ForEach-Object { [pscustomobject]@{ Name = $_.name; Comment = $_.comment; Namespace = $_.ns } } }
        function Add-DnsClientNrptRule { [CmdletBinding()] param([string[]]$Namespace, [string[]]$NameServers, [string]$Comment)
          $s = SimLoad; $s.nrpt = @($s.nrpt) + @([pscustomobject]@{ name = '{' + [guid]::NewGuid() + '}'; comment = $Comment; ns = @($Namespace); servers = @($NameServers) }); SimSave $s }
        function Remove-DnsClientNrptRule { [CmdletBinding(SupportsShouldProcess)] param([string]$Name, [switch]$Force)
          $s = SimLoad; $s.nrpt = @($s.nrpt | Where-Object { $_.name -ne $Name }); SimSave $s }
        function Remove-Item { [CmdletBinding()] param([Parameter(Position = 0)][string[]]$Path, [switch]$Recurse) }
        function Clear-DnsClientCache { }
        function ipconfig { }
        foreach ($c in 'Set-DnsClientServerAddress', 'Add-DnsClientNrptRule', 'Remove-DnsClientNrptRule', 'Disable-NetAdapterBinding',
                       'Enable-NetAdapterBinding', 'Add-DnsClientDohServerAddress', 'Remove-DnsClientDohServerAddress', 'Remove-Item') {
          if ((Get-Command $c).CommandType -ne 'Function') { throw "команда $c не подменена" } }
        """.Replace("__FILE__", _file.Replace("'", "''"));
}

[Collection("scenarios")]
public class DnsScenarioTests
{
    private static readonly DnsServer Server = new() { Name = "Тестовый Smart DNS", Primary = "203.0.113.10", Secondary = "203.0.113.11" };
    private static readonly string[] Ours = { "203.0.113.10", "203.0.113.11" };

    private static NetSim.State OneEthernet(Action<NetSim.Adapter>? tune = null)
    {
        var a = new NetSim.Adapter();
        tune?.Invoke(a);
        return new NetSim.State { adapters = { a } };
    }

    private static (List<string> Log, Action<string> Write) NewLog()
    {
        var log = new List<string>();
        return (log, l => { lock (log) log.Add(l); });
    }

    [Fact]
    public async Task Connect_and_disconnect_return_dhcp()
    {
        using var sim = new NetSim(OneEthernet());
        var (log, write) = NewLog();

        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        Assert.Equal(Ours, sim["Sim-Ethernet"].dns4);
        Assert.NotNull(DnsSwitcher.GetActive());

        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        Assert.Equal("", sim["Sim-Ethernet"].static4);
        Assert.Null(DnsSwitcher.GetActive());
        Assert.False(File.Exists(Path.Combine(TestSetup.DataDir, "dns-backup.json")));
    }

    [Fact]
    public async Task Windows_without_doh_commands_can_connect_and_disconnect()
    {
        // Журнал пользователя: на его Windows нет Remove-DnsClientDohServerAddress, и отключение падало
        // «Имя ... не распознано как имя командлета», запись о подключении оставалась, сторож подключал снова
        using var sim = new NetSim(OneEthernet(a => { a.static4 = "127.0.0.1"; a.dns4 = new() { "127.0.0.1" }; })) { NoDoh = true };
        var dohServer = new DnsServer { Name = "Zapret DNS", Primary = "83.217.211.149", DohUrl = "https://dns.zapret.moe/dns-query" };
        var (log, write) = NewLog();
        await DnsSwitcher.ApplyAsync(dohServer, new AppSettings(), write);
        Assert.Equal(new[] { "83.217.211.149" }, sim["Sim-Ethernet"].dns4);
        Assert.Contains(log, l => l.Contains("не поддерживает DoH"));

        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "127.0.0.1" }, sim["Sim-Ethernet"].dns4);
        Assert.Null(DnsSwitcher.GetActive());
        Assert.DoesNotContain(log, l => l.Contains("не распознано"));
    }

    [Fact]
    public async Task Emergency_reset_works_without_doh_commands()
    {
        using var sim = new NetSim(OneEthernet()) { NoDoh = true };
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        var appData = Path.Combine(Path.GetTempPath(), $"chimerion-reset-{Guid.NewGuid():N}");
        var dnex = Directory.CreateDirectory(Path.Combine(appData, "DNEX")).FullName;
        foreach (var f in new[] { "active.json", "dns-backup.json" }) File.Copy(Path.Combine(TestSetup.DataDir, f), Path.Combine(dnex, f));
        try
        {
            var (code, output) = await sim.RunReal($"$env:APPDATA = '{appData.Replace("'", "''")}'\n" + File.ReadAllText(RepoFile("reset-dns.ps1")));
            Assert.True(code == 0, $"код {code}: {output}");
            Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        }
        finally { Directory.Delete(appData, true); }
    }

    [Fact]
    public async Task Disconnect_returns_original_static_dns()
    {
        using var sim = new NetSim(OneEthernet(a => { a.static4 = "77.88.8.8"; a.dns4 = new() { "77.88.8.8" }; }));
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "77.88.8.8" }, sim["Sim-Ethernet"].dns4);
        Assert.Equal("77.88.8.8", sim["Sim-Ethernet"].static4);
    }

    [Fact]
    public async Task Failed_dns_change_rolls_back_every_adapter()
    {
        // Первый адаптер DNS принял, на втором Windows отказала: первый тоже должен вернуться к исходным
        using var sim = new NetSim(new NetSim.State
        {
            adapters =
            {
                new NetSim.Adapter(),
                new NetSim.Adapter { name = "Sim-WiFi", index = 15, guid = "{22222222-2222-2222-2222-222222222222}", failSet = true },
            },
        });
        sim.Before = script =>
        {
            // Откат должен пройти, хотя адаптер все еще сбоит: отключение сбрасывает DNS тем же командлетом
            if (script.Contains("-ResetServerAddresses")) sim.Edit(s => s.adapters[1].failSet = false);
            return Task.CompletedTask;
        };
        var (log, write) = NewLog();

        await Assert.ThrowsAsync<InvalidOperationException>(() => DnsSwitcher.ApplyAsync(Server, new AppSettings(), write));

        Assert.Contains(log, l => l.Contains("возвращаю исходные настройки DNS"));
        Assert.All(sim.Load().adapters, a => Assert.Equal(new[] { "192.168.1.1" }, a.dns4));
        Assert.Null(DnsSwitcher.GetActive());
    }

    [Fact]
    public async Task PowerShell_that_did_not_start_rolls_back()
    {
        // Так выглядел сбой из журнала: процесс PowerShell не запустился (слишком длинная командная строка)
        using var sim = new NetSim(OneEthernet());
        sim.Before = script => script.Contains("Clear-DnsClientCache") && script.Contains("$ifs =")
            ? throw new System.ComponentModel.Win32Exception(206, "Имя файла или его расширение имеет слишком большую длину")
            : Task.CompletedTask;
        var (log, write) = NewLog();

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => DnsSwitcher.ApplyAsync(Server, new AppSettings(), write));

        Assert.Contains(log, l => l.Contains("Отключено: исходные настройки DNS восстановлены"));
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        Assert.Null(DnsSwitcher.GetActive());
    }

    [Fact]
    public async Task App_killed_during_change_can_still_disconnect()
    {
        // Программу закрыли, пока скрипт менял DNS: вывод скрипта так и не прочитан. При следующем запуске
        // запись о подключении уже на диске, и отключение возвращает исходные DNS
        using var sim = new NetSim(OneEthernet(a => a.dns6 = new() { "fd00::1" }));
        var hang = new TaskCompletionSource();
        var applied = new TaskCompletionSource();
        sim.Before = async script =>
        {
            if (!script.Contains("$ifs =")) return;
            await sim.RunReal(script);
            applied.SetResult();
            await hang.Task; // процесс «убит»: ответа программа не дождется
        };
        var (_, write) = NewLog();
        _ = DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(Ours, sim["Sim-Ethernet"].dns4);
        Assert.False(sim["Sim-Ethernet"].v6); // IPv6 выключен, но программа об этом не узнала
        Assert.Equal(Ours, DnsSwitcher.GetActive()!.Addresses);
        Assert.Empty(DnsSwitcher.GetActive()!.Ipv6Disabled);

        sim.Before = null;
        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        Assert.True(sim["Sim-Ethernet"].v6); // вернулся по файлу ipv6-disabled.txt
        Assert.Null(DnsSwitcher.GetActive());
    }

    [Fact]
    public async Task Ipv6_turned_off_for_connection_is_turned_back_on()
    {
        using var sim = new NetSim(OneEthernet(a => a.dns6 = new() { "fd00::1" }));
        var (log, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        Assert.False(sim["Sim-Ethernet"].v6);
        Assert.Contains("{11111111-1111-1111-1111-111111111111}", DnsSwitcher.GetActive()!.Ipv6Disabled);

        await DnsSwitcher.RestoreAsync(write);
        Assert.True(sim["Sim-Ethernet"].v6);
        Assert.Contains(log, l => l.Contains("IPv6 включен обратно"));
    }

    [Fact]
    public async Task Ipv6_that_would_not_turn_back_on_is_reported_and_retried()
    {
        using var sim = new NetSim(OneEthernet(a => a.dns6 = new() { "fd00::1" }));
        var (log, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        Assert.False(sim["Sim-Ethernet"].v6);

        sim.Edit(s => s.adapters[0].failV6On = true);
        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4); // DNS вернулся
        Assert.Equal(new[] { "Sim-Ethernet" }, DnsSwitcher.Ipv6NotRestored);
        Assert.Contains(log, l => l.Contains("IPv6 не включился обратно"));
        Assert.True(File.Exists(Path.Combine(TestSetup.DataDir, "ipv6-disabled.txt"))); // попытка не забыта

        // Драйвер починили: следующее отключение включает IPv6 по файлу, хотя записи о подключении уже нет
        sim.Edit(s => s.adapters[0].failV6On = false);
        await DnsSwitcher.RestoreAsync(write);
        Assert.True(sim["Sim-Ethernet"].v6);
        Assert.Empty(DnsSwitcher.Ipv6NotRestored);
        Assert.False(File.Exists(Path.Combine(TestSetup.DataDir, "ipv6-disabled.txt")));
    }

    [Fact]
    public async Task Ipv6_disabled_by_user_stays_disabled()
    {
        // IPv6 выключил сам пользователь: программа его не включала и не должна включать
        using var sim = new NetSim(OneEthernet(a => { a.v6 = false; a.dns6 = new() { "fd00::1" }; }));
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        Assert.Empty(DnsSwitcher.GetActive()!.Ipv6Disabled);
        await DnsSwitcher.RestoreAsync(write);
        Assert.False(sim["Sim-Ethernet"].v6);
    }

    [Fact]
    public async Task Split_dns_rules_are_removed_and_foreign_rules_survive()
    {
        var state = OneEthernet();
        state.nrpt.Add(new NetSim.Rule { name = "{corp}", comment = "Corp", ns = { ".corp.example" }, servers = { "10.0.0.1" } });
        using var sim = new NetSim(state);
        var st = new AppSettings();
        st.SplitDns.Enabled = true;
        st.SplitDns.GeneralName = "Общий";
        st.SplitDns.GeneralAddresses = new() { "198.51.100.1" };
        var (_, write) = NewLog();

        await DnsSwitcher.ApplyAsync(Server, st, write);
        var rules = sim.Load().nrpt;
        var ours = Assert.Single(rules, r => r.comment == SplitDns.RuleComment);
        Assert.Contains("chatgpt.com", ours.ns);
        Assert.Equal(Ours, ours.servers);
        Assert.Equal(new[] { "198.51.100.1" }, sim["Sim-Ethernet"].dns4); // на адаптере общий DNS, а не Smart DNS

        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal("Corp", Assert.Single(sim.Load().nrpt).comment);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
    }

    [Fact]
    public async Task Leftover_rules_are_removed_even_without_connection_record()
    {
        var state = OneEthernet();
        state.nrpt.Add(new NetSim.Rule { name = "{old}", comment = SplitDns.RuleComment, ns = { "claude.ai" }, servers = { "203.0.113.10" } });
        using var sim = new NetSim(state);
        var (_, write) = NewLog();
        await DnsSwitcher.RestoreAsync(write); // active.json нет: например, его удалили вручную
        Assert.Empty(sim.Load().nrpt);
    }

    [Fact]
    public async Task Adapter_switched_during_connection_is_cleaned_on_disconnect()
    {
        // Подключились по кабелю, потом кабель выдернули и включили Wi-Fi. Сторож вернул подключение уже на Wi-Fi.
        // Отключение возвращает кабель по резервной копии (по GUID, хотя он сейчас не активен), а Wi-Fi, которого
        // в копии нет, сбрасывает на автоматические DNS, потому что на нем стоят адреса CHIMERION
        var wifi = new NetSim.Adapter { name = "Sim-WiFi", index = 15, guid = "{22222222-2222-2222-2222-222222222222}", status = "Disconnected", dhcp4 = new() { "10.0.0.1" }, dns4 = new() { "10.0.0.1" } };
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter(), wifi } });
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);

        sim.Edit(s => { s.adapters[0].status = "Disconnected"; s.adapters[1].status = "Up"; });
        var active = DnsSwitcher.GetActive()!;
        Assert.True(ConnectionMonitor.NeedsRestore(active, sim.CurrentDns(), rulesPresent: true, out _));
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        Assert.Equal(Ours, sim["Sim-WiFi"].dns4);

        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        Assert.Equal(new[] { "10.0.0.1" }, sim["Sim-WiFi"].dns4);
    }

    [Fact]
    public async Task Dns_reset_by_another_program_is_detected_and_restored()
    {
        using var sim = new NetSim(OneEthernet());
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        var active = DnsSwitcher.GetActive()!;
        Assert.False(ConnectionMonitor.NeedsRestore(active, sim.CurrentDns(), true, out _));

        // Другая программа (VPN-клиент, «оптимизатор») поставила свой DNS
        sim.Edit(s => s.adapters[0].dns4 = new() { "8.8.8.8" });
        Assert.True(ConnectionMonitor.NeedsRestore(active, sim.CurrentDns(), true, out _));
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        Assert.Equal(Ours, sim["Sim-Ethernet"].dns4);

        // При отключении возвращается то, что было до подключения, а не DNS чужой программы
        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
    }

    [Fact]
    public async Task After_reboot_connection_record_survives_and_reset_is_noticed()
    {
        using var sim = new NetSim(OneEthernet());
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);

        // «Перезагрузка»: в памяти ничего не осталось, есть только файлы. Обновление Windows сбросило DNS на DHCP
        var record = JsonSerializer.Deserialize<DnsSwitcher.ActiveConnection>(
            File.ReadAllText(Path.Combine(TestSetup.DataDir, "active.json")), Storage.Options)!;
        Assert.Equal(Server.Id, record.ServerId);
        Assert.False(ConnectionMonitor.NeedsRestore(record, sim.CurrentDns(), true, out _));
        sim.Edit(s => s.adapters[0].dns4 = new() { "192.168.1.1" });
        Assert.True(ConnectionMonitor.NeedsRestore(record, sim.CurrentDns(), true, out _));

        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        Assert.Equal(Ours, sim["Sim-Ethernet"].dns4);
        await DnsSwitcher.RestoreAsync(write);
    }

    [Fact]
    public async Task Disconnect_that_leaves_our_dns_keeps_the_record_for_retry()
    {
        using var sim = new NetSim(OneEthernet());
        var (log, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);

        sim.Edit(s => s.adapters[0].failSet = true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DnsSwitcher.RestoreAsync(write));
        Assert.Contains(log, l => l.Contains("Остались адреса CHIMERION"));
        Assert.NotNull(DnsSwitcher.GetActive()); // запись не потеряна: отключиться можно снова

        sim.Edit(s => s.adapters[0].failSet = false);
        await DnsSwitcher.RestoreAsync(write);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        Assert.Null(DnsSwitcher.GetActive());
    }

    private static string RepoFile(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, name)) && File.Exists(Path.Combine(d.FullName, "CHIMERION.csproj"))) return Path.Combine(d.FullName, name);
        throw new FileNotFoundException(name);
    }

    [Fact]
    public async Task Emergency_reset_script_restores_dns_ipv6_and_rules_after_crash()
    {
        // Программа упала после подключения в «Двух DNS» с выключенным IPv6, и до записи об IPv6 в active.json
        // дело не дошло. reset-dns.cmd из архива должен вернуть все: DNS, IPv6 и правила NRPT
        using var sim = new NetSim(OneEthernet(a => a.dns6 = new() { "fd00::1" }));
        var st = new AppSettings();
        st.SplitDns.Enabled = true;
        st.SplitDns.GeneralAddresses = new() { "198.51.100.1" };
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, st, write);
        Assert.False(sim["Sim-Ethernet"].v6);
        Assert.NotEmpty(sim.Load().nrpt);

        var appData = Path.Combine(Path.GetTempPath(), $"chimerion-reset-{Guid.NewGuid():N}");
        var dnex = Directory.CreateDirectory(Path.Combine(appData, "DNEX")).FullName;
        foreach (var f in new[] { "active.json", "dns-backup.json", "ipv6-disabled.txt" })
            File.Copy(Path.Combine(TestSetup.DataDir, f), Path.Combine(dnex, f));
        var active = File.ReadAllText(Path.Combine(dnex, "active.json"));
        File.WriteAllText(Path.Combine(dnex, "active.json"), System.Text.RegularExpressions.Regex.Replace(active, @"""ipv6Disabled"": \[[^\]]*\]", @"""ipv6Disabled"": []"));
        try
        {
            var script = $"$env:APPDATA = '{appData.Replace("'", "''")}'\n" + File.ReadAllText(RepoFile("reset-dns.ps1"));
            var (code, output) = await sim.RunReal(script);
            Assert.True(code == 0, $"код {code}: {output}");
            Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
            Assert.True(sim["Sim-Ethernet"].v6, output);
            Assert.DoesNotContain(sim.Load().nrpt, r => r.comment == SplitDns.RuleComment);
            Assert.Contains("Готово", output);
        }
        finally { Directory.Delete(appData, true); }
    }

    [Fact]
    public async Task Declined_admin_prompt_keeps_previous_connection()
    {
        using var sim = new NetSim(OneEthernet());
        var (_, write) = NewLog();
        await DnsSwitcher.ApplyAsync(Server, new AppSettings(), write);
        var other = new DnsServer { Name = "Другой", Primary = "198.51.100.20" };

        sim.Before = script => script.Contains("$ifs =") ? throw new OperationCanceledException("Запрос прав администратора отклонен") : Task.CompletedTask;
        await Assert.ThrowsAsync<OperationCanceledException>(() => DnsSwitcher.ApplyAsync(other, new AppSettings(), write));

        Assert.Equal(Server.Id, DnsSwitcher.GetActive()!.ServerId);
        Assert.Equal(Ours, sim["Sim-Ethernet"].dns4);
        Assert.DoesNotContain(sim.Scripts, s => s.Contains("-ResetServerAddresses")); // отката не было: DNS не менялся
        sim.Before = null;
        await DnsSwitcher.RestoreAsync(write);
    }
}
