using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>Пользователь отказал в правах администратора (UAC). Наследует отмену: старый код, ловящий отмену, работает как раньше.</summary>
public sealed class UacDeclinedException() : OperationCanceledException("Запрос прав администратора отклонен");

/// <summary>Смена DNS на активных адаптерах Windows через PowerShell (NetTCPIP / DnsClient).</summary>
public static class DnsSwitcher
{
    private const string BackupFile = "dns-backup.json";
    private const string ActiveFile = "active.json";
    /// <summary>
    /// GUID адаптеров, где CHIMERION выключила IPv6. Скрипт подключения дописывает строку сразу после выключения,
    /// еще до того, как программа прочтет его вывод: если ее закроют посередине, отключение все равно включит IPv6
    /// обратно. В active.json те же GUID попадают только после завершения скрипта.
    /// </summary>
    private const string Ipv6OffFile = "ipv6-disabled.txt";

    /// <summary>
    /// Адаптеры, на которых последнее отключение не смогло включить IPv6 обратно. DNS при этом восстановлен, но без
    /// IPv6 часть сайтов может работать хуже: окно подсказывает, как включить его вручную.
    /// </summary>
    public static List<string> Ipv6NotRestored { get; } = new();

    private static List<string> Ipv6OffFromFile()
    {
        try
        {
            var path = Storage.PathOf(Ipv6OffFile);
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim().ToUpperInvariant()).Where(l => System.Guid.TryParse(l.Trim('{', '}'), out _)).Distinct().ToList()
                : new();
        }
        catch (IOException) { return new(); }
    }

    /// <summary>Сведения о текущем подключении. Файл переживает перезапуск и сбой программы,
    /// поэтому отключиться можно всегда, даже если CHIMERION закрылась аварийно.</summary>
    public sealed class ActiveConnection
    {
        public Guid ServerId { get; set; }
        public string Name { get; set; } = "";
        public List<string> Addresses { get; set; } = new();
        public string? DohTemplate { get; set; }
        /// <summary>Адреса, для которых CHIMERION сама зарегистрировала DoH, — только их и удаляем при отключении.</summary>
        public List<string> DohAddedFor { get; set; } = new();
        /// <summary>Адаптеры (GUID), на которых CHIMERION на время подключения выключила IPv6: у сервера нет IPv6,
        /// а DNS роутера по IPv6 вел бы запросы мимо сервера. При отключении IPv6 включается обратно.</summary>
        public List<string> Ipv6Disabled { get; set; } = new();
        public DateTime Since { get; set; } = DateTime.Now;
        /// <summary>Режим «Два DNS»: Addresses — Smart DNS для ИИ (через NRPT), AdapterAddresses — общий DNS на адаптерах.</summary>
        public bool Split { get; set; }
        public List<string> AdapterAddresses { get; set; } = new();
        public string? GeneralName { get; set; }
        /// <summary>
        /// Запасной сервер: его адреса стоят в Addresses после адресов основного (без DoH). Windows сама переходит на
        /// него, если основной не ответил. Пусто — запасного нет.
        /// </summary>
        public Guid? BackupId { get; set; }
        public string? BackupName { get; set; }
        public List<string> BackupAddresses { get; set; } = new();
        /// <summary>Что должно стоять на адаптерах: общий DNS в режиме «Два DNS» (может быть пусто: адаптер не меняли), иначе сервер.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public List<string> OnAdapters => Split ? AdapterAddresses : Addresses;
    }

    /// <summary>
    /// Исполнитель скриптов PowerShell (скрипт, нужны ли права администратора). В программе это RunPsAsync;
    /// сценарные тесты подставляют свой и проверяют, что и в каком порядке программа делает с DNS при сбоях,
    /// не трогая сеть компьютера.
    /// </summary>
    internal static Func<string, bool, Task<(int Code, string Output)>> Runner = RunPsAsync;

    /// <summary>Есть ли IPv6-интернет. Подменяется в тестах, в программе HasGlobalIpv6.</summary>
    internal static Func<bool> Ipv6Internet = HasGlobalIpv6;

    private static ActiveConnection? _activeCache;
    private static DateTime _activeStamp;

    /// <summary>Текущее подключение. Файл перечитывается, только если изменилось время его записи:
    /// метод вызывается из UI-потока много раз на каждое действие.</summary>
    public static ActiveConnection? GetActive()
    {
        var path = Storage.PathOf(ActiveFile);
        if (!File.Exists(path)) { _activeCache = null; _activeStamp = default; return null; }
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_activeCache == null || stamp != _activeStamp)
        {
            _activeCache = Storage.LoadOrCreate<ActiveConnection?>(ActiveFile, () => null);
            _activeStamp = stamp;
        }
        return _activeCache is { Addresses.Count: > 0 } ? _activeCache : null;
    }

    private sealed class AdapterBackup
    {
        public int InterfaceIndex { get; set; }
        /// <summary>Постоянный идентификатор адаптера: номер интерфейса может смениться, GUID — нет.</summary>
        public string? Guid { get; set; }
        public string? Name { get; set; }
        /// <summary>Статически заданные DNS (пусто — адреса от DHCP).</summary>
        public string? Static { get; set; }
        /// <summary>Статически заданные DNS для IPv6 (пусто — от роутера). В копиях старых версий нет.</summary>
        public string? Static6 { get; set; }
    }

    /// <summary>DNS системы: читаем сетевые интерфейсы напрямую. Раньше запускался PowerShell, это секунда на каждую проверку.</summary>
    public static Task<string> GetCurrentAsync() => Task.Run(() =>
    {
        var addrs = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                     && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Distinct()
            .ToList();
        return addrs.Count == 0 ? "не определен" : string.Join(", ", addrs);
    });

    /// <summary>Все DNS-адреса работающих адаптеров, IPv4 и IPv6. Служебные fec0::/10, которые Windows
    /// подставляет без настроенного IPv6 DNS, не считаются. Нужны сторожу для проверки сброса DNS.</summary>
    public static Task<List<IPAddress>> GetCurrentAddressesAsync() => Task.Run(() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                     && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => !a.IsIPv6SiteLocal)
            .Distinct()
            .ToList());

    /// <param name="backup">Запасной сервер (ConnectionMonitor.Backup): его IPv4-адрес встает после адресов основного,
    /// на адаптеры и в правила NRPT, без DoH. null — без запасного.</param>
    public static async Task ApplyAsync(DnsServer s, AppSettings settings, Action<string> log, DnsServer? backup = null)
    {
        var addrs = new List<string>();
        if (IPAddress.TryParse(s.Primary, out var p)) addrs.Add(p.ToString());
        if (IPAddress.TryParse(s.Secondary, out var sec)) addrs.Add(sec.ToString());
        foreach (var v in s.Ipv6)
            if (IPAddress.TryParse(v, out var a6) && a6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !addrs.Contains(a6.ToString()))
                addrs.Add(a6.ToString());

        string? doh = null;
        if (!string.IsNullOrWhiteSpace(s.DohUrl))
        {
            if (!Uri.TryCreate(s.DohUrl.Split('{')[0], UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Некорректный DoH-адрес");
            doh = u.GetLeftPart(UriPartial.Path).Replace("'", "''");

            if (addrs.Count == 0)
            {
                // Для DoH Windows нужен IP сервера: узнаем его через эталонный резолвер.
                var (ips, _) = await DnsWire.ResolveDohAsync(settings.ReferenceDoh, u.Host, TimeSpan.FromSeconds(8), CancellationToken.None);
                addrs.AddRange(ips.Take(2).Select(i => i.ToString()));
                log($"IP DoH-сервера {u.Host}: {string.Join(", ", addrs)}");
            }

            // IPv6 DoH-сервера: с ним DNS ставится и для IPv6, и IPv6 на адаптере выключать не нужно
            if (!addrs.Any(IsV6))
            {
                try
                {
                    var (ips6, _) = await DnsWire.ResolveDohAsync(settings.ReferenceDoh, u.Host, TimeSpan.FromSeconds(6), CancellationToken.None, qtype: 28);
                    var v6 = ips6.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).Take(2).Select(i => i.ToString()).ToList();
                    if (v6.Count > 0) { addrs.AddRange(v6); log($"IPv6 DoH-сервера {u.Host}: {string.Join(", ", v6)}"); }
                }
                catch (Exception) { /* IPv6 нет или эталон не ответил: тогда IPv6 на адаптере выключается ниже */ }
            }
        }
        // IPv6-адреса сервера ставим, только если у компьютера есть IPv6-интернет: иначе Windows ждала бы ответа
        // от недоступного адреса. Без него DNS ставится для IPv4, а IPv6 на адаптере выключается на время подключения.
        if (addrs.Any(IsV6) && !Ipv6Internet())
        {
            addrs.RemoveAll(IsV6);
            if (addrs.Count == 0) throw new InvalidOperationException("Сервер работает только по IPv6, а IPv6-интернета на компьютере нет");
            log("IPv6-интернета нет: DNS ставится только для IPv4");
        }
        if (addrs.Count == 0) throw new InvalidOperationException("У сервера нет IP-адреса");

        // Запасной сервер: только обычный IPv4 и только если он не совпадает с адресами основного. DoH для него не
        // регистрируется (его шаблон неизвестен или другой), поэтому список адресов для DoH — только основной
        var primaryAddrs = addrs.ToList();
        var backupAddrs = new List<string>();
        if (backup != null && backup.Id != s.Id && IPAddress.TryParse(backup.Primary, out var bip)
            && bip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !addrs.Contains(bip.ToString()))
            backupAddrs.Add(bip.ToString());
        addrs.AddRange(backupAddrs);

        // Режим «Два DNS»: на адаптеры идет общий DNS (или ничего, если его не меняем), сервер получает только домены ИИ
        var split = settings.SplitDns is { Enabled: true } sd ? sd : null;
        var adapterAddrs = addrs;
        string? adapterDoh = doh;
        if (split != null)
        {
            adapterAddrs = split.GeneralAddresses.Where(a => IPAddress.TryParse(a, out _)).Select(a => IPAddress.Parse(a).ToString()).Distinct().ToList();
            if (adapterAddrs.Any(IsV6) && !Ipv6Internet()) adapterAddrs.RemoveAll(IsV6);
            adapterDoh = Uri.TryCreate(split.GeneralDoh?.Split('{')[0], UriKind.Absolute, out var gu) && gu.Scheme == Uri.UriSchemeHttps
                ? gu.GetLeftPart(UriPartial.Path).Replace("'", "''") : null;
        }
        bool touchAdapters = adapterAddrs.Count > 0;

        await EnsureBackupAsync(log);

        var prev = GetActive();
        bool hasV6 = adapterAddrs.Any(IsV6);
        var list = string.Join(",", adapterAddrs.Select(a => $"'{a}'"));
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        // Только физические адаптеры: VPN-туннели и виртуальные сети (Hyper-V, WSL) не трогаем — иначе сломаем их DNS.
        sb.AppendLine("$ifs = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }");
        // Переход с сервера без IPv6 на сервер с IPv6: включаем IPv6 обратно до смены DNS, иначе его DNS не поставить
        // Выключенный раньше IPv6: из записи о подключении и из файла (если прошлый раз программу закрыли посередине)
        var prevV6 = (prev?.Ipv6Disabled ?? new()).Select(g => g.ToUpperInvariant()).Concat(Ipv6OffFromFile()).Distinct().ToList();
        if (hasV6 && prevV6.Count > 0)
        {
            sb.AppendLine($"foreach ($g in @({string.Join(",", prevV6.Select(g => $"'{g.Replace("'", "")}'"))})) {{");
            sb.AppendLine("""
                  try {
                    Get-NetAdapter -IncludeHidden | Where-Object { ([string]$_.InterfaceGuid).ToUpper() -eq $g } | ForEach-Object {
                      Enable-NetAdapterBinding -Name $_.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop
                      Write-Output ('V6ON ' + $g); Write-Output ('IPv6 включен обратно: ' + $_.Name) }
                  } catch { Write-Output ('WARN IPv6: ' + $_.Exception.Message) }
                }
                """);
        }
        // DoH: для адресов сервера (и в режиме «Два DNS» для адресов общего DNS). Windows знает эти адреса как DoH,
        // и запросы по правилам NRPT к ним тоже идут через DoH
        var dohPairs = new List<(string Ip, string Template)>();
        if (doh != null) dohPairs.AddRange(primaryAddrs.Select(a => (a, doh)));
        if (split != null && adapterDoh != null) dohPairs.AddRange(adapterAddrs.Where(a => !addrs.Contains(a)).Select(a => (a, adapterDoh)));
        if (dohPairs.Count > 0)
        {
            sb.AppendLine("if (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {");
            foreach (var (a, t) in dohPairs)
            {
                // Существующую системную запись DoH не трогаем; свою помечаем строкой ADDED, чтобы при отключении удалить только ее.
                sb.AppendLine($"  if (-not (Get-DnsClientDohServerAddress -ServerAddress '{a}' -ErrorAction SilentlyContinue)) {{");
                sb.AppendLine($"    Add-DnsClientDohServerAddress -ServerAddress '{a}' -DohTemplate '{t}' -AllowFallbackToUdp $false -AutoUpgrade $true | Out-Null; Write-Output 'ADDED {a}' }}");
            }
            sb.AppendLine("  Write-Output 'DoH зарегистрирован'");
            sb.AppendLine("} else { Write-Output 'Эта версия Windows не поддерживает DoH, используется обычный DNS' }");
        }
        // Правила NRPT: прежние правила CHIMERION снимаем всегда, в режиме «Два DNS» ставим заново для доменов ИИ
        sb.AppendLine(SplitDns.RemoveRulesScript);
        if (split != null)
        {
            var ns = SplitDns.Namespaces(settings);
            if (SplitDns.PolicyWarning(SplitDns.PolicyRuleCount()) is { } gpo) log("ВНИМАНИЕ: " + gpo);
            sb.AppendLine($"Add-DnsClientNrptRule -Namespace @({string.Join(",", ns.Select(n => $"'{n.Replace("'", "")}'"))}) -NameServers @({string.Join(",", addrs.Select(a => $"'{a}'"))}) -Comment '{SplitDns.RuleComment}'");
            sb.AppendLine($"Write-Output 'Домены ИИ ({ns.Count} правил) направлены в {s.Name.Replace("'", "")}'");
        }
        if (touchAdapters)
            sb.AppendLine($"foreach ($i in $ifs) {{ Set-DnsClientServerAddress -InterfaceIndex $i.ifIndex -ServerAddresses @({list}); Write-Output ('Адаптер: ' + $i.Name) }}");
        else
            sb.AppendLine("Write-Output 'DNS адаптеров не меняется'");
        if (touchAdapters && !hasV6)
        {
            Directory.CreateDirectory(Storage.Dir);
            sb.AppendLine($"$v6file = '{Storage.PathOf(Ipv6OffFile).Replace("'", "''")}'");
            // У сервера нет IPv6, а роутер раздает DNS по IPv6: Windows отправляла бы часть запросов туда, мимо сервера.
            // На время подключения выключаем IPv6 только на таких адаптерах и запоминаем их (строка V6OFF).
            sb.AppendLine("""
                foreach ($i in $ifs) {
                  try {
                    $v6dns = @((Get-DnsClientServerAddress -InterfaceIndex $i.ifIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue).ServerAddresses | Where-Object { $_ -and $_ -notlike 'fec0:*' })
                    $b = Get-NetAdapterBinding -Name $i.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
                    if ($v6dns.Count -gt 0 -and $b -and $b.Enabled) {
                      Disable-NetAdapterBinding -Name $i.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop
                      Add-Content -Path $v6file -Value ([string]$i.InterfaceGuid).ToUpper() -Encoding ASCII
                      Write-Output ('V6OFF ' + ([string]$i.InterfaceGuid).ToUpper())
                      Write-Output ('IPv6 на время подключения выключен: ' + $i.Name + ' (DNS роутера по IPv6 вел запросы мимо сервера)')
                    }
                  } catch { Write-Output ('WARN IPv6 ' + $i.Name + ': ' + $_.Exception.Message) }
                }
                """);
        }
        sb.AppendLine("Clear-DnsClientCache");

        // Запись о подключении сохраняем ДО смены DNS: если что-то упадет посередине, отключение все равно сработает.
        var active = new ActiveConnection
        {
            ServerId = s.Id, Name = s.Name, Addresses = addrs, DohTemplate = doh?.Replace("''", "'"),
            DohAddedFor = prev?.DohAddedFor.ToList() ?? new(),
            Ipv6Disabled = prevV6,
            Split = split != null, AdapterAddresses = split != null ? adapterAddrs : new(), GeneralName = split?.GeneralName,
            BackupId = backupAddrs.Count > 0 ? backup!.Id : null, BackupName = backupAddrs.Count > 0 ? backup!.Name : null, BackupAddresses = backupAddrs,
        };
        Storage.Save(ActiveFile, active);

        try
        {
            var (code, output) = await Runner(sb.ToString(), true);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var l = line.Trim();
                if (l.StartsWith("ADDED ")) active.DohAddedFor.Add(l[6..]);
                else if (l.StartsWith("V6OFF ")) { if (!active.Ipv6Disabled.Contains(l[6..])) active.Ipv6Disabled.Add(l[6..]); }
                else if (l.StartsWith("V6ON ")) active.Ipv6Disabled.Remove(l[5..]);
                else log(l);
            }
            Storage.Save(ActiveFile, active);
            if (code != 0) throw new InvalidOperationException("PowerShell завершился с ошибкой, см. журнал");
        }
        catch (OperationCanceledException)
        {
            // Права не выданы (UAC отклонен): скрипт не запускался, DNS не менялся. Если до этого было подключение,
            // его DNS по-прежнему стоит на адаптерах: возвращаем запись о нем, иначе отключить его будет нечем.
            if (prev != null) Storage.Save(ActiveFile, prev);
            else
            {
                var path = Storage.PathOf(ActiveFile);
                if (File.Exists(path)) File.Delete(path);
            }
            throw;
        }
        catch (Exception)
        {
            // Скрипт мог успеть поменять часть адаптеров: возвращаем все, что было до подключения.
            log("Подключение не удалось, возвращаю исходные настройки DNS");
            try { await RestoreAsync(log); }
            catch (Exception re) { log("Откат не удался, см. журнал: " + re.Message); }
            throw;
        }
        log(split != null
            ? $"Подключено, два DNS: ИИ через {s.Name} ({string.Join(", ", primaryAddrs)}{(doh != null ? " + DoH" : "")}), остальное через «{split.GeneralName}»"
            : $"Подключено: {s.Name} ({string.Join(", ", primaryAddrs)}{(doh != null ? " + DoH" : "")})");
        if (split != null && SplitDns.GeneralIsSmart(split, primaryAddrs)) log("Внимание: " + SplitDns.GeneralIsSmartWarning);
        if (backupAddrs.Count > 0)
            log($"Запасной сервер: {backup!.Name} ({string.Join(", ", backupAddrs)}). Если основной перестанет отвечать, Windows перейдет на него сама");
    }

    /// <summary>Запоминаем исходные настройки один раз — до первой смены.</summary>
    private static async Task EnsureBackupAsync(Action<string> log)
    {
        if (File.Exists(Storage.PathOf(BackupFile))) return;
        var (code, json) = await Runner(
            "$r = foreach ($a in Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }) {\n" +
            "  $p = \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\$($a.InterfaceGuid)\"\n" +
            "  $ns = (Get-ItemProperty -Path $p -Name NameServer -ErrorAction SilentlyContinue).NameServer\n" +
            "  $p6 = \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip6\\Parameters\\Interfaces\\$($a.InterfaceGuid)\"\n" +
            "  $ns6 = (Get-ItemProperty -Path $p6 -Name NameServer -ErrorAction SilentlyContinue).NameServer\n" +
            "  [pscustomobject]@{ InterfaceIndex = $a.ifIndex; Guid = [string]$a.InterfaceGuid; Name = $a.Name; Static = [string]$ns; Static6 = [string]$ns6 }\n" +
            "}\n" +
            "ConvertTo-Json -InputObject @($r) -Compress", false);
        if (code != 0) throw new InvalidOperationException("Не удалось сохранить исходные настройки DNS: " + json);
        var items = JsonSerializer.Deserialize<List<AdapterBackup>>(json, Storage.Options) ?? new();
        Storage.Save(BackupFile, items);
        log("Исходные настройки DNS сохранены");
    }

    /// <summary>
    /// Безопасное отключение:
    /// 1) каждому адаптеру из резервной копии (ищем по GUID, не по номеру) возвращаем исходные DNS;
    /// 2) любой адаптер, где остались адреса CHIMERION, сбрасываем на автоматические (DHCP);
    /// 3) удаляем добавленные CHIMERION записи DoH (глобальные и на интерфейсах);
    /// 4) сбрасываем кэш и сверяем результат — если адреса CHIMERION остались, сообщаем об ошибке.
    /// </summary>
    public static async Task RestoreAsync(Action<string> log)
    {
        var path = Storage.PathOf(BackupFile);
        var backup = File.Exists(path) ? Storage.LoadOrCreate(BackupFile, () => new List<AdapterBackup>()) : new();
        var active = GetActive();

        static string Q(string v) => "'" + v.Replace("'", "''") + "'";
        static IEnumerable<string> Ips(string? v) => (v ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => IPAddress.TryParse(x, out _)).Select(x => IPAddress.Parse(x).ToString());

        // Общий DNS режима «Два DNS» тоже наш, кроме адресов, которые и так стояли до подключения (их вернем как исходные)
        var backupIps = backup.SelectMany(a => Ips(a.Static).Concat(Ips(a.Static6))).ToHashSet();
        var general = (active?.AdapterAddresses ?? new()).SelectMany(x => Ips(x)).Where(ip => !backupIps.Contains(ip));
        var ours = (active?.Addresses ?? new()).Concat(active?.DohAddedFor ?? new()).SelectMany(x => Ips(x)).Concat(general).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$ours = @({string.Join(",", ours.Select(Q))})");
        // IPv6, выключенный на время подключения, включаем первым: иначе его DNS не вернуть
        var v6off = (active?.Ipv6Disabled ?? new()).Select(g => g.ToUpperInvariant()).Concat(Ipv6OffFromFile()).Distinct().ToList();
        sb.AppendLine($"$v6off = @({string.Join(",", v6off.Select(Q))})");
        sb.AppendLine("""
            foreach ($g in $v6off) {
              foreach ($a6 in @(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { ([string]$_.InterfaceGuid).ToUpper() -eq $g })) {
                try {
                  Enable-NetAdapterBinding -Name $a6.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop
                  # Проверяем, что IPv6 правда включился: драйвер или политика могут молча оставить его выключенным
                  $b6 = Get-NetAdapterBinding -Name $a6.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
                  if ($b6 -and -not $b6.Enabled) { Write-Output "V6FAIL $g $($a6.Name)" }
                  else { Write-Output "RESULT $($a6.Name) → IPv6 включен обратно" }
                } catch { Write-Output "V6FAIL $g $($a6.Name): $($_.Exception.Message)" }
              }
            }
            """);
        sb.AppendLine("$backup6 = @{}");
        foreach (var a in backup.Where(a => System.Guid.TryParse(a.Guid?.Trim('{', '}'), out _)))
            sb.AppendLine($"$backup6[{Q(System.Guid.Parse(a.Guid!.Trim('{', '}')).ToString("B").ToUpperInvariant())}] = {Q(string.Join(",", Ips(a.Static6)))}");
        sb.AppendLine("$backup = @{}");
        foreach (var a in backup.Where(a => System.Guid.TryParse(a.Guid?.Trim('{', '}'), out _)))
            sb.AppendLine($"$backup[{Q(System.Guid.Parse(a.Guid!.Trim('{', '}')).ToString("B").ToUpperInvariant())}] = {Q(string.Join(",", Ips(a.Static)))}");
        // Старые копии без GUID — по номеру интерфейса.
        sb.AppendLine("$byIndex = @{}");
        foreach (var a in backup.Where(a => string.IsNullOrEmpty(a.Guid)))
            sb.AppendLine($"$byIndex[{a.InterfaceIndex}] = {Q(string.Join(",", Ips(a.Static)))}");
        sb.AppendLine("""
            foreach ($a in Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue) {
              $g = ([string]$a.InterfaceGuid).ToUpper()
              $cur = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue).ServerAddresses)
              $hasOurs = @($cur | Where-Object { $ours -contains $_ }).Count -gt 0
              if ($backup.ContainsKey($g)) { $want = $backup[$g] }
              elseif ($byIndex.ContainsKey([int]$a.ifIndex)) { $want = $byIndex[[int]$a.ifIndex] }
              elseif ($hasOurs) { $want = '' }
              else { continue }
              $want6 = if ($backup6.ContainsKey($g)) { $backup6[$g] } else { '' }
              # Если в «исходных» почему-то оказались адреса CHIMERION — не возвращаем их, а ставим автоматические.
              if ($want -and @($want -split ',' | Where-Object { $ours -contains $_ }).Count -gt 0) { $want = '' }
              if ($want6 -and @($want6 -split ',' | Where-Object { $ours -contains $_ }).Count -gt 0) { $want6 = '' }
              try {
                # Сначала автоматические DNS для IPv4 и IPv6, потом исходные статические, если они были
                Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop
                $all = @(@($want -split ',') + @($want6 -split ',') | Where-Object { $_ } | Select-Object -Unique)
                if ($all.Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses $all -ErrorAction Stop }
              } catch { Write-Output "WARN $($a.Name): $($_.Exception.Message)" }
              foreach ($ip in $ours) {
                Remove-Item "HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\$g\DohInterfaceSettings\Doh\$ip" -Recurse -ErrorAction SilentlyContinue
              }
              $after = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue).ServerAddresses)
              $mode = if ($want) { 'исходные' } else { 'автоматически (DHCP)' }
              Write-Output "RESULT $($a.Name) → $($mode): $(@($after | Select-Object -Unique) -join ', ')"
              if (@($after | Where-Object { $ours -contains $_ }).Count -gt 0) { Write-Output "LEFT $($a.Name)" }
            }
            # На Windows 10 без DoH (до сборки 19628) этой команды нет: без проверки отключение падало целиком,
            # запись о подключении оставалась, и сторож подключал снова
            if (Get-Command Remove-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {
              foreach ($ip in $ours) { Remove-DnsClientDohServerAddress -ServerAddress $ip -ErrorAction SilentlyContinue }
            }
            """);
        // Правила NRPT режима «Два DNS»: снимаем всегда, даже если запись о подключении потерялась
        sb.AppendLine(SplitDns.RemoveRulesScript);
        sb.AppendLine("""
            if (@(Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq 'DNEX' }).Count -gt 0) { Write-Output 'LEFT правила NRPT' }
            Clear-DnsClientCache
            ipconfig /flushdns | Out-Null
            """);

        Ipv6NotRestored.Clear();
        var v6failed = new List<string>();
        var (code, output) = await Runner(sb.ToString(), true);
        bool left = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (line.StartsWith("RESULT ")) log("  " + line[7..]);
            else if (line.StartsWith("WARN ")) log("  ⚠ " + line[5..]);
            else if (line.StartsWith("LEFT ")) { left = true; log("  ✗ Остались адреса CHIMERION: " + line[5..]); }
            else if (line.StartsWith("V6FAIL "))
            {
                var rest = line[7..];
                var sp = rest.IndexOf(' ');
                var guid = sp > 0 ? rest[..sp] : rest;
                var name = sp > 0 ? rest[(sp + 1)..] : rest;
                v6failed.Add(guid);
                Ipv6NotRestored.Add(name);
                log("  ✗ IPv6 не включился обратно: " + name);
            }
            else if (line.Length > 0) log("  " + line);
        }
        if (code != 0) throw new InvalidOperationException("Ошибка отключения, см. журнал");
        if (left) throw new InvalidOperationException("DNS сброшен не на всех адаптерах, см. журнал");

        if (File.Exists(path)) File.Delete(path);
        var v6path = Storage.PathOf(Ipv6OffFile);
        // Адаптеры, где IPv6 включить не удалось, остаются в файле: следующее отключение или подключение попробует снова
        if (v6failed.Count > 0) File.WriteAllLines(v6path, v6failed);
        else if (File.Exists(v6path)) File.Delete(v6path);
        var activePath = Storage.PathOf(ActiveFile);
        if (File.Exists(activePath)) File.Delete(activePath);
        log("Отключено: исходные настройки DNS восстановлены");
    }

    /// <summary>
    /// Только чтение: пишет в журнал DNS каждого активного адаптера и записи DoH в Windows.
    /// Нужно, чтобы после отключения было видно, что осталось в системе. Права администратора не нужны.
    /// </summary>
    public static async Task ReportStateAsync(Action<string> log)
    {
        try
        {
            var (_, output) = await Runner(
                "foreach ($a in Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }) {\n" +
                "  $s = (Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses\n" +
                "  Write-Output ('DNS ' + $a.Name + ': ' + ($s -join ', '))\n" +
                "}\n" +
                "$d = @(if (Get-Command Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue) { Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue })\n" +
                "if ($d.Count -gt 0) { foreach ($x in $d) { Write-Output ('DoH ' + $x.ServerAddress + ' ' + $x.DohTemplate) } }\n" +
                "else { Write-Output 'DoH: записей нет' }", false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
                if (line.Length > 0) log("  " + line);
        }
        catch (Exception ex) { log("Не удалось проверить состояние DNS: " + ex.Message); }
    }

    /// <summary>Есть ли у компьютера настоящий IPv6-интернет: глобальный адрес на работающем адаптере (туннели не считаются).</summary>
    private static bool HasGlobalIpv6()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                         && n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                                                          or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && SourceParser.IsPublic6(u.Address.ToString()));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return false; }
    }

    private static bool IsV6(string a) =>
        IPAddress.TryParse(a, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    /// <summary>Работает ли интернет через системный DNS: резолвим несколько доменов, достаточно одного успеха.</summary>
    public static async Task<bool> SystemDnsWorksAsync(IEnumerable<string> hosts, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var tasks = hosts.Select(async h =>
        {
            try { return (await Dns.GetHostAddressesAsync(h, cts.Token)).Length > 0; }
            catch (Exception) { return false; }
        }).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            if (await done) return true;
            tasks.Remove(done);
        }
        return false;
    }

    private static string? _languageMode;

    /// <summary>
    /// Режим языка PowerShell на этом компьютере: FullLanguage или ConstrainedLanguage (его включают корпоративные
    /// AppLocker и WDAC). В ограниченном режиме загрузчик скриптов не работает, и CHIMERION не может менять DNS.
    /// Проверяется без загрузчика, одной короткой командой. Null, если PowerShell не ответил.
    /// </summary>
    public static async Task<string?> LanguageModeAsync()
    {
        if (_languageMode != null) return _languageMode;
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -Command \"$ExecutionContext.SessionState.LanguageMode\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            return _languageMode = output.Length > 0 ? output : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Понятное предупреждение, если PowerShell ограничен политикой. Null, если все в порядке или режим неизвестен.</summary>
    public static string? LanguageModeWarning(string? mode) =>
        mode is null or "FullLanguage" ? null
        : $"PowerShell ограничен политикой ({mode}), и CHIMERION не сможет менять DNS. Так бывает на рабочих компьютерах: обратитесь к администратору";

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Запуск PowerShell. Программа работает без прав администратора; права запрашиваются (UAC)
    /// только на сам скрипт смены DNS. Сам скрипт может быть длинным (в режиме «Два DNS» с наборами сайтов
    /// это тысячи доменов), а командная строка Windows ограничена 32767 символами. Поэтому скрипт пишется
    /// в файл в профиле пользователя, а в аргументах идет только короткий загрузчик с SHA-256 этого файла:
    /// загрузчик читает файл один раз, сверяет хеш и выполняет именно прочитанное. Подмененный до запуска
    /// файл не выполнится.
    /// </summary>
    public static async Task<(int Code, string Output)> RunPsAsync(string script, bool elevated = false)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                      "WindowsPowerShell", "v1.0", "powershell.exe"); // полный путь — не подхватим чужой powershell.exe из PATH
        Directory.CreateDirectory(Storage.Dir);
        var id = Guid.NewGuid().ToString("N");
        var scriptFile = Path.Combine(Storage.Dir, $"ps-{id}.ps1");
        var outFile = Path.Combine(Storage.Dir, $"ps-{id}.log");
        var bytes = new UTF8Encoding(false).GetBytes(script);
        await File.WriteAllBytesAsync(scriptFile, bytes);
        try
        {
            if (!elevated || IsAdmin()) return await RunHiddenAsync(powershell, Launcher(scriptFile, bytes, null));

            // Повышенный процесс не дает перенаправить вывод, поэтому он пишет его в файл в профиле пользователя.
            var psi = new ProcessStartInfo(powershell, Arguments(Launcher(scriptFile, bytes, outFile), hidden: true))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить PowerShell");
                await proc.WaitForExitAsync();
                var text = File.Exists(outFile) ? (await File.ReadAllTextAsync(outFile)).Trim() : "";
                return (proc.ExitCode, text);
            }
            catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                throw new UacDeclinedException();
            }
        }
        finally
        {
            try { File.Delete(scriptFile); } catch (IOException) { }
            try { File.Delete(outFile); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Короткий загрузчик: читает файл скрипта, сверяет SHA-256 и выполняет прочитанный текст через
    /// [scriptblock]::Create внутри try, чтобы даже синтаксическая ошибка попала в журнал, а не терялась молча.
    /// outFile задан: весь вывод уходит в этот файл (повышенный процесс), иначе в stdout.
    /// </summary>
    public static string Launcher(string scriptFile, byte[] scriptBytes, string? outFile)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(scriptBytes));
        var sf = scriptFile.Replace("'", "''");
        var check =
            $"  $b = [IO.File]::ReadAllBytes('{sf}')\n" +
            "  $h = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)).Replace('-', '')\n" +
            $"  if ($h -ne '{hash}') {{ throw 'файл скрипта изменен до запуска, выполнение отменено' }}\n" +
            "  $code = [Text.Encoding]::UTF8.GetString($b)\n";
        if (outFile == null)
            return "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n$ProgressPreference = 'SilentlyContinue'\ntry {\n" + check +
                   "  & ([scriptblock]::Create($code))\n" +
                   "} catch { [Console]::Error.WriteLine('ОШИБКА: ' + $_.Exception.Message); exit 1 }";
        // Файл вывода повышенный процесс создает сам и только новым (CreateNew): если на его месте заранее подложили
        // файл или жесткую ссылку на системный файл, запись не пойдет туда с правами администратора
        var of = outFile.Replace("'", "''");
        return "$ProgressPreference = 'SilentlyContinue'\n$w = $null\ntry {\n" +
               $"  $w = New-Object IO.StreamWriter([IO.File]::Open('{of}', [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read), (New-Object Text.UTF8Encoding $false))\n" +
               "  $w.AutoFlush = $true\n" + check +
               "  & ([scriptblock]::Create($code)) *>&1 | ForEach-Object { $w.WriteLine([string]$_) }\n" +
               "  $w.Dispose()\n  exit 0\n" +
               "} catch { if ($w) { $w.WriteLine('ОШИБКА: ' + $_.Exception.Message); $w.Dispose() }; exit 1 }";
    }

    public static string Arguments(string launcher, bool hidden) =>
        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass{(hidden ? " -WindowStyle Hidden" : "")} -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(launcher));

    private static async Task<(int Code, string Output)> RunHiddenAsync(string powershell, string launcher)
    {
        // $ProgressPreference в загрузчике: иначе PowerShell пишет в stderr служебный CLIXML («Подготовка модулей…»).
        var psi = new ProcessStartInfo(powershell, Arguments(launcher, hidden: false))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить PowerShell");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var err = (await stderr).Trim();
        err = ClixmlErrors(err);
        var outp = (await stdout).Trim();
        return (proc.ExitCode, string.IsNullOrEmpty(err) ? outp : $"{outp}\n{err}".Trim());
    }

    /// <summary>
    /// Поток ошибок PowerShell при перенаправлении приходит в формате CLIXML («#&lt; CLIXML»): там и служебный прогресс,
    /// и настоящие ошибки (строки &lt;S S="Error"&gt;). Раньше весь такой поток отбрасывался как прогресс, и причина
    /// сбоя терялась. Теперь прогресс отбрасывается, а текст ошибок остается. Обычный текст возвращается как есть.
    /// </summary>
    internal static string ClixmlErrors(string err)
    {
        const System.Text.RegularExpressions.RegexOptions S = System.Text.RegularExpressions.RegexOptions.Singleline;
        var errors = new List<string>();
        // Блоки CLIXML вырезаются, из них остаются только строки ошибок; обычный текст вокруг (например «ОШИБКА: ...»
        // из загрузчика) сохраняется как есть
        // Блоков <Objs> бывает несколько подряд, а заголовок «#< CLIXML» один
        var rest = System.Text.RegularExpressions.Regex.Replace(err, @"#< CLIXML\s*|<Objs .*?</Objs>", m =>
        {
            foreach (System.Text.RegularExpressions.Match e in System.Text.RegularExpressions.Regex.Matches(m.Value, @"<S S=""Error"">(.*?)</S>", S))
                errors.Add(WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(e.Groups[1].Value, "_x([0-9A-Fa-f]{4})_",
                    x => ((char)Convert.ToInt32(x.Groups[1].Value, 16)).ToString())));
            return "";
        }, S);
        return (string.Concat(errors).Trim() + "\n" + rest.Trim()).Trim();
    }
}
