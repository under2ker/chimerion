using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>Пользователь отказал в правах администратора (UAC). Наследует отмену: старый код, ловящий отмену, работает как раньше.</summary>
public sealed class UacDeclinedException() : OperationCanceledException("Запрос прав администратора отклонен");

/// <summary>Windows не умеет подключать сервер, доступный только через DoH.</summary>
public sealed class DohUnavailableException() : InvalidOperationException("Windows не поддерживает подключение этого сервера через DoH");

/// <summary>Windows без системного DoH, а DoH-прокси программы не запустился: подключение невозможно.</summary>
public sealed class DohRelayFailedException(string message) : InvalidOperationException(message);

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

    /// <summary>Адаптеры, где IPv6 выключен и не включен обратно (остаток оборванного изменения).</summary>
    public static int Ipv6LeftOff => Ipv6OffFromFile().Count;

    public static bool BackupExists => File.Exists(Storage.PathOf(BackupFile));

    /// <summary>Копия без подключения: иначе следующее подключение вернуло бы при отключении устаревшие DNS.</summary>
    public static void DropStaleBackup()
    {
        try { File.Delete(Storage.PathOf(BackupFile)); } catch (IOException) { }
    }

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
        public string? ProtectedDohTemplate { get; set; }
        /// <summary>Адрес системного DoH для проверки наборов. Старые записи читаются без изменений.</summary>
        public string? ReadDohTemplate() => ProtectedDohTemplate is { Length: > 0 } value
            ? Storage.Unprotect(value) is { Length: > 0 } decoded ? decoded : null : DohTemplate;
        /// <summary>Адреса, для которых CHIMERION сама зарегистрировала DoH, — только их и удаляем при отключении.</summary>
        public List<string> DohAddedFor { get; set; } = new();
        /// <summary>
        /// Подключение через DoH-прокси программы (Windows без системного DoH): локальный адрес, который стоит в DNS,
        /// адрес DoH (зашифрован DPAPI), IP DoH-сервера и прежний DNS сети, который стоит на адаптерах после прокси.
        /// По ним прокси поднимается после перезапуска программы без обращения к сети.
        /// </summary>
        public string? ProxyListen { get; set; }
        public string? ProtectedProxyEndpoint { get; set; }
        public List<string> ProxyBootstrap { get; set; } = new();
        public List<string> ProxyFallback { get; set; } = new();
        /// <summary>Прежний посредник 0.1.120–0.1.125 (отдельный процесс через Планировщик). Только для перехода на прокси.</summary>
        public string? RelayChannel { get; set; }
        public string? RelayListen { get; set; }
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

    /// <summary>
    /// Текст для строки PowerShell в одинарных кавычках. PowerShell считает одинарной кавычкой не только ',
    /// но и типографские ‘ ’ ‚ ‛: замена одной лишь ' позволяла имени сервера из источника закрыть строку
    /// и дописать команду в скрипт, который выполняется с правами администратора.
    /// </summary>
    internal static string PsEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            // Удваивается та же кавычка: пара ’’ дает в строке ’, и имя сервера в журнале не искажается
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') sb.Append(c).Append(c);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Строка PowerShell в одинарных кавычках с безопасным содержимым.</summary>
    internal static string PsQuote(string value) => "'" + PsEscape(value) + "'";

    /// <summary>Есть ли IPv6-интернет. Подменяется в тестах, в программе HasGlobalIpv6.</summary>
    internal static Func<bool> Ipv6Internet = HasGlobalIpv6;

    /// <summary>Запуск DoH-прокси программы (адрес DoH, IP сервера, нужный локальный адрес или null). В программе это
    /// DohLocalProxy, сценарные тесты подставляют свой: порт 53 в тестах не нужен.</summary>
    internal static Func<Uri, IReadOnlyList<IPAddress>, string?, Action<string>, Task<DohProxySession>> DohProxyStart =
        (endpoint, upstream, preferred, log) => DohLocalProxy.StartAsync(endpoint, upstream, preferred, log);

    /// <summary>Остановка DoH-прокси на локальном адресе. Подменяется в тестах вместе с запуском.</summary>
    internal static Func<string, Action<string>, Task> DohProxyStop = (address, log) => DohLocalProxy.StopAsync(address, log);

    /// <summary>Работает ли DoH-прокси на локальном адресе.</summary>
    internal static Func<string, bool> DohProxyRunning = DohLocalProxy.IsRunning;

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
        if (_activeCache is { DohTemplate: not null } active)
        {
            // Старые версии сохраняли адрес DoH без защиты в active.json.
            try
            {
                if (active.RelayChannel == null) active.ProtectedDohTemplate = Storage.Protect(active.DohTemplate);
                active.DohTemplate = null;
                Storage.SaveRequired(ActiveFile, active);
                _activeStamp = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                Storage.Trace?.Invoke("Не удалось защитить адрес DoH в записи подключения");
                _activeStamp = default;
            }
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

    /// <summary>DNS работающих адаптеров, для которых сохранена исходная конфигурация.</summary>
    public static Task<List<IReadOnlyCollection<IPAddress>>> GetCurrentAdapterAddressesAsync() => Task.Run(() =>
    {
        try
        {
            var path = Storage.PathOf(BackupFile);
            if (!File.Exists(path)) return new List<IReadOnlyCollection<IPAddress>>();
            var backup = JsonSerializer.Deserialize<List<AdapterBackup>>(File.ReadAllText(path), Storage.Options) ?? new();
            var ids = backup.Select(a => a.Guid?.Trim('{', '}')).Where(g => !string.IsNullOrEmpty(g)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && ids.Contains(n.Id.Trim('{', '}')))
                .Select(n => (IReadOnlyCollection<IPAddress>)n.GetIPProperties().DnsAddresses.Where(a => !a.IsIPv6SiteLocal).Distinct().ToList())
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Net.NetworkInformation.NetworkInformationException)
        {
            Storage.Trace?.Invoke("Не удалось проверить DNS по адаптерам: " + e.GetType().Name);
            return new List<IReadOnlyCollection<IPAddress>>();
        }
    });

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

        DohProxySession? proxy = null;
        string? proxyEndpoint = null;
        var proxyBootstrap = new List<string>();
        string? doh = null;
        if (!string.IsNullOrWhiteSpace(s.DohUrl))
        {
            if (!Uri.TryCreate(s.DohUrl.Split('{')[0], UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Некорректный DoH-адрес");
            var endpoint = new Uri(u.GetLeftPart(UriPartial.Query));
            doh = PsEscape(endpoint.AbsoluteUri);

            {
                // Без системного DoH даже заданный IP не заменяет шифрованное подключение.
                var (capabilityCode, capability) = await Runner(
                    "if (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) { 'DOH_AVAILABLE' } else { 'DOH_UNAVAILABLE' }", false);
                if (capabilityCode != 0) throw new InvalidOperationException("Не удалось проверить поддержку DoH в Windows");
                if (capability.Trim() == "DOH_UNAVAILABLE")
                {
                    // Windows 10 без системного DoH: DNS-ссылку обслуживает DoH-прокси внутри программы. Прежний прокси
                    // (другой сервер) работает на другом локальном адресе, пока новое подключение не подтверждено.
                    var upstream = await ProxyUpstreamAsync(settings, endpoint, log);
                    try { proxy = await DohProxyStart(endpoint, upstream, null, log); }
                    catch (Exception ex) when (ex is not DohRelayFailedException and not OperationCanceledException)
                    {
                        throw new DohRelayFailedException("Не удалось запустить DoH-прокси программы: " + ex.Message);
                    }
                    proxyEndpoint = endpoint.AbsoluteUri;
                    proxyBootstrap = upstream.Select(i => i.ToString()).ToList();
                    addrs.Clear();
                    addrs.Add(proxy.ListenAddress);
                    log($"Windows не поддерживает системный DoH. DNS работает через DoH-прокси программы {proxy.ListenAddress}");
                }
                else if (capability.Trim() != "DOH_AVAILABLE") throw new InvalidOperationException("Не удалось проверить поддержку DoH в Windows");
            }

            if (addrs.Count == 0)
            {
                // Для DoH Windows нужен IP сервера: узнаем его через эталонный резолвер.
                if (IPAddress.TryParse(u.Host, out var endpointIp)) addrs.Add(endpointIp.ToString());
                else
                {
                    var (ips, _) = await DnsWire.ResolveDohAsync(settings.ReferenceDoh, u.Host, TimeSpan.FromSeconds(8), CancellationToken.None);
                    addrs.AddRange(ips.Take(2).Select(i => i.ToString()));
                }
                log($"IP DoH-сервера {u.Host}: {string.Join(", ", addrs)}");
            }

            // IPv6 DoH-сервера: с ним DNS ставится и для IPv6, и IPv6 на адаптере выключать не нужно.
            // Через DoH-прокси DNS ставится на локальный адрес, поэтому IPv6-адреса сервера не нужны.
            if (proxy == null && !addrs.Any(IsV6) && !IPAddress.TryParse(u.Host, out _))
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
                ? PsEscape(gu.GetLeftPart(UriPartial.Query)) : null;
        }
        bool touchAdapters = adapterAddrs.Count > 0;

        var prev = GetActive();
        // Прежнее подключение могло быть через DoH-прокси: новый работает на другом локальном адресе (или это тот же
        // прокси, если DoH-адрес не менялся), поэтому прежний останавливается только после успешной смены DNS или отката
        var prevProxy = prev?.ProxyListen;
        // Новый прокси, которого не было до подключения: его и останавливаем, если подключение не состоялось
        var newProxy = proxy != null && proxy.ListenAddress != prevProxy ? proxy.ListenAddress : null;

        // DoH-прокси уже запущен: если исходные настройки не сохранятся, его нельзя оставить работать.
        try { await EnsureBackupAsync(log); }
        catch (Exception) when (newProxy != null)
        {
            await StopProxyQuietlyAsync(newProxy, log);
            throw;
        }

        // DoH-прокси работает только вместе с программой: за ним на адаптерах прежний DNS сети, чтобы без нее
        // сайты открывались (нейросети при этом нет). В «Двух DNS» адаптеры получают общий DNS, прокси — только NRPT
        var proxyFallback = new List<string>();
        if (proxy != null && split == null)
        {
            proxyFallback = prev?.ProxyListen != null && prev.ProxyFallback.Count > 0 ? prev.ProxyFallback.ToList() : NetworkFallbackDns(addrs);
            addrs.AddRange(proxyFallback.Where(f => !addrs.Contains(f)));
            log(proxyFallback.Count > 0
                ? $"Пока программа закрыта, сайты откроются через прежний DNS сети ({string.Join(", ", proxyFallback)})"
                : "Прежний DNS сети не найден: пока программа закрыта, сайты открываться не будут");
        }
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
            sb.AppendLine($"foreach ($g in @({string.Join(",", prevV6.Select(PsQuote))})) {{");
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
        // Через DoH-прокси DoH в Windows не регистрируется: DNS стоит на локальном адресе прокси.
        if (doh != null && proxy == null) dohPairs.AddRange(primaryAddrs.Select(a => (a, doh)));
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
            sb.AppendLine($"Add-DnsClientNrptRule -Namespace @({string.Join(",", ns.Select(PsQuote))}) -NameServers @({string.Join(",", addrs.Select(a => $"'{a}'"))}) -Comment '{SplitDns.RuleComment}'");
            sb.AppendLine($"Write-Output 'Домены ИИ ({ns.Count} правил) направлены в {PsEscape(s.Name)}'");
        }
        if (touchAdapters)
            sb.AppendLine($"foreach ($i in $ifs) {{ Set-DnsClientServerAddress -InterfaceIndex $i.ifIndex -ServerAddresses @({list}); Write-Output ('Адаптер: ' + $i.Name) }}");
        else
            sb.AppendLine("Write-Output 'DNS адаптеров не меняется'");
        if (touchAdapters && !hasV6)
        {
            Directory.CreateDirectory(Storage.Dir);
            sb.AppendLine($"$v6file = {PsQuote(Storage.PathOf(Ipv6OffFile))}");
            // У сервера нет IPv6, а роутер раздает DNS по IPv6: Windows отправляла бы часть запросов туда, мимо сервера.
            // На время подключения выключаем IPv6 только на таких адаптерах и запоминаем их (строка V6OFF).
            sb.AppendLine("""
                foreach ($i in $ifs) {
                  try {
                    $v6dns = @((Get-DnsClientServerAddress -InterfaceIndex $i.ifIndex -AddressFamily IPv6 -ErrorAction SilentlyContinue).ServerAddresses | Where-Object { $_ -and $_ -notlike 'fec0:*' })
                    $b = Get-NetAdapterBinding -Name $i.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
                    if ($v6dns.Count -gt 0 -and $b -and $b.Enabled) {
                      Add-Content -Path $v6file -Value ([string]$i.InterfaceGuid).ToUpper() -Encoding ASCII
                      Disable-NetAdapterBinding -Name $i.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop
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
            ServerId = s.Id, Name = s.Name, Addresses = addrs,
            ProtectedDohTemplate = proxy == null && doh != null ? Storage.Protect(doh.Replace("''", "'")) : null,
            DohAddedFor = prev?.DohAddedFor.ToList() ?? new(),
            Ipv6Disabled = prevV6,
            Split = split != null, AdapterAddresses = split != null ? adapterAddrs : new(), GeneralName = split?.GeneralName,
            BackupId = backupAddrs.Count > 0 ? backup!.Id : null, BackupName = backupAddrs.Count > 0 ? backup!.Name : null, BackupAddresses = backupAddrs,
            ProxyListen = proxy?.ListenAddress, ProtectedProxyEndpoint = proxyEndpoint != null ? Storage.Protect(proxyEndpoint) : null,
            ProxyBootstrap = proxyBootstrap, ProxyFallback = proxyFallback,
        };
        try { Storage.SaveRequired(ActiveFile, active); }
        catch (Exception) when (newProxy != null)
        {
            // Запись о подключении не сохранилась: отключить прокси будет нечем, поэтому не оставляем его работать.
            await StopProxyQuietlyAsync(newProxy, log);
            throw;
        }

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
            Storage.SaveRequired(ActiveFile, active);
            if (code != 0) throw new InvalidOperationException("PowerShell завершился с ошибкой, см. журнал");
        }
        catch (OperationCanceledException)
        {
            // Права не выданы (UAC отклонен): скрипт не запускался, DNS не менялся. Если до этого было подключение,
            // его DNS по-прежнему стоит на адаптерах: возвращаем запись о нем, иначе отключить его будет нечем.
            NetHistory.Add($"Подключение к «{s.ShortName}» не выполнено", "Windows не получила права администратора, настройки сети не менялись");
            // Новый прокси уже поднят, но DNS не менялся: прежнее подключение (и его прокси) остаются как были.
            if (newProxy != null) await StopProxyQuietlyAsync(newProxy, log);
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
            NetHistory.Add($"Подключение к «{s.ShortName}» не удалось", "исходные настройки DNS возвращаются");
            try { await RestoreAsync(log); }
            catch (Exception re) { log("Откат не удался, см. журнал: " + re.Message); }
            // DNS уже исходный: прежний прокси (если был) больше не нужен, новый остановило отключение.
            if (prevProxy != null) await StopProxyQuietlyAsync(prevProxy, log);
            throw;
        }
        // Новое подключение подтверждено: прежний прокси (если был и это не он же) больше не нужен.
        if (prevProxy != null && prevProxy != proxy?.ListenAddress)
            await StopProxyQuietlyAsync(prevProxy, log);
        log(split != null
            ? $"Подключено, два DNS: ИИ через {s.ShortName} ({string.Join(", ", primaryAddrs)}{(proxy != null ? " + DoH через прокси программы" : doh != null ? " + DoH" : "")}), остальное через «{split.GeneralName}»"
            : proxy != null
                ? $"Подключено: {s.ShortName} (DoH через прокси программы {proxy.ListenAddress})"
                : $"Подключено: {s.ShortName} ({string.Join(", ", primaryAddrs)}{(doh != null ? " + DoH" : "")})");
        if (split != null && SplitDns.GeneralIsSmart(split, primaryAddrs)) log("Внимание: " + SplitDns.GeneralIsSmartWarning);
        if (backupAddrs.Count > 0)
            log($"Запасной сервер: {backup!.Name} ({string.Join(", ", backupAddrs)}). Если основной перестанет отвечать, Windows перейдет на него сама");
        // История сетевых изменений: что именно поменялось в системе
        var changed = new List<string> { split != null ? $"два DNS: нейросети через Smart DNS, остальное через «{split.GeneralName}»" : "DNS адаптеров заменен на Smart DNS" };
        if (doh != null && proxy == null) changed.Add("с шифрованием DoH");
        if (proxy != null) changed.Add($"DoH через прокси программы {proxy.ListenAddress}, в этой Windows нет своего DoH");
        if (backupAddrs.Count > 0) changed.Add($"запасной сервер «{backup!.Name}»");
        if (active.Ipv6Disabled.Count > 0) changed.Add($"IPv6 выключен на {active.Ipv6Disabled.Count} адаптерах на время подключения");
        NetHistory.Add($"Подключено: {s.ShortName}", string.Join("; ", changed));
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
        Storage.SaveRequired(BackupFile, items);
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

        static string Q(string v) => PsQuote(v);
        static IEnumerable<string> Ips(string? v) => (v ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => IPAddress.TryParse(x, out _)).Select(x => IPAddress.Parse(x).ToString());

        // Общий DNS режима «Два DNS» тоже наш, кроме адресов, которые и так стояли до подключения (их вернем как исходные)
        var backupIps = backup.SelectMany(a => Ips(a.Static).Concat(Ips(a.Static6))).ToHashSet();
        var general = (active?.AdapterAddresses ?? new()).SelectMany(x => Ips(x)).Where(ip => !backupIps.Contains(ip));
        // Прежний DNS сети за прокси — не наш: на других адаптерах он мог стоять и до подключения
        var fallbackIps = (active?.ProxyFallback ?? new()).SelectMany(x => Ips(x)).ToHashSet();
        var ours = (active?.Addresses ?? new()).Concat(active?.DohAddedFor ?? new()).SelectMany(x => Ips(x)).Concat(general)
            .Where(ip => !fallbackIps.Contains(ip)).Distinct().ToList();
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
            # Один запрос DNS-адресов на все адаптеры вместо запроса на каждый: отключение заметно быстрее, особенно со скрытыми адаптерами
            function Get-DnsByIndex {
              $h = @{}
              foreach ($d in @(Get-DnsClientServerAddress -ErrorAction SilentlyContinue)) {
                $k = [int]$d.InterfaceIndex
                $h[$k] = @($h[$k]) + @($d.ServerAddresses)
              }
              $h
            }
            $dnsBefore = Get-DnsByIndex
            $touched = @()
            foreach ($a in Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue) {
              $g = ([string]$a.InterfaceGuid).ToUpper()
              $cur = @($dnsBefore[[int]$a.ifIndex] | Where-Object { $_ })
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
              $touched += [pscustomobject]@{ Name = $a.Name; Index = [int]$a.ifIndex; Mode = $(if ($want) { 'исходные' } else { 'автоматически (DHCP)' }) }
            }
            # Итог по тронутым адаптерам одним запросом
            $dnsAfter = Get-DnsByIndex
            foreach ($t in $touched) {
              $after = @($dnsAfter[$t.Index] | Where-Object { $_ })
              Write-Output "RESULT $($t.Name) → $($t.Mode): $(@($after | Select-Object -Unique) -join ', ')"
              if (@($after | Where-Object { $ours -contains $_ }).Count -gt 0) { Write-Output "LEFT $($t.Name)" }
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
        if (code != 0) { NetHistory.Add("Отключение не удалось", "ошибка при возврате DNS, подробности в журнале"); throw new InvalidOperationException("Ошибка отключения, см. журнал"); }
        if (left) { NetHistory.Add("Отключение прошло не полностью", "на части адаптеров остались адреса CHIMERION, подробности в журнале"); throw new InvalidOperationException("DNS сброшен не на всех адаптерах, см. журнал"); }

        // DNS уже возвращен: DoH-прокси больше не нужен. Ошибка остановки не должна мешать отключению.
        if (active?.ProxyListen is { } proxyListen) await StopProxyQuietlyAsync(proxyListen, log);

        var v6path = Storage.PathOf(Ipv6OffFile);
        // Адаптеры, где IPv6 включить не удалось, остаются в файле: следующее отключение или подключение попробует снова
        if (v6failed.Count > 0) File.WriteAllLines(v6path, v6failed);
        else if (File.Exists(v6path)) File.Delete(v6path);
        var activePath = Storage.PathOf(ActiveFile);
        if (File.Exists(activePath)) File.Delete(activePath);
        // Копия удаляется последней: при обрыве раньше нее запись о подключении осталась бы без исходных DNS
        if (File.Exists(path)) File.Delete(path);
        log("Отключено: исходные настройки DNS восстановлены");
        var restored = new List<string> { "DNS адаптеров возвращен к исходному (из резервной копии или автоматический)" };
        if (active?.Split == true) restored.Add("правила «Двух DNS» сняты");
        if (active?.ProxyListen != null) restored.Add("DoH-прокси программы остановлен");
        if (v6off.Count > 0) restored.Add(v6failed.Count == 0 ? $"IPv6 включен обратно на {v6off.Count} адаптерах" : $"IPv6 включен не везде: не вернулся на {v6failed.Count} из {v6off.Count}");
        NetHistory.Add("Отключено: исходные настройки DNS восстановлены", string.Join("; ", restored));
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
                "if ($d.Count -gt 0) { foreach ($x in $d) { Write-Output ('DoH ' + $x.ServerAddress) } }\n" +
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
            // Скрипты без прав только читают (поддержка DoH, исходные DNS, состояние): зависший PowerShell не должен
            // держать подключение бесконечно. Скрипты, меняющие DNS, не обрываем: остановка посередине хуже ожидания
            if (!elevated) return await RunHiddenAsync(powershell, Launcher(scriptFile, bytes, null), ReadOnlyScriptTimeout);
            if (IsAdmin()) return await RunHiddenAsync(powershell, Launcher(scriptFile, bytes, null), null);

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
        var sf = PsEscape(scriptFile);
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
        var of = PsEscape(outFile);
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

    /// <summary>Сколько ждать скрипт только на чтение. Обычно он идет секунду-две; меняется в тестах.</summary>
    internal static TimeSpan ReadOnlyScriptTimeout = TimeSpan.FromSeconds(60);

    private static async Task<(int Code, string Output)> RunHiddenAsync(string powershell, string launcher, TimeSpan? timeout)
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
        if (timeout is { } limit)
        {
            using var cts = new CancellationTokenSource(limit);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* уже завершился */ }
                return (-1, $"PowerShell не ответил за {limit.TotalSeconds:0} с и остановлен");
            }
        }
        else await proc.WaitForExitAsync();
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

    /// <summary>Остановка прокси, когда DNS уже возвращен или еще не менялся: ошибка не должна мешать основному действию.</summary>
    private static async Task StopProxyQuietlyAsync(string address, Action<string> log)
    {
        try { await DohProxyStop(address, log); }
        catch (Exception ex) { log("Не удалось остановить DoH-прокси программы: " + ex.Message); }
    }

    /// <summary>
    /// Подключение через DoH-прокси, а прокси не работает (программу перезапустили): поднимает его на том же адресе,
    /// без пробного запроса, чтобы он работал и пока сеть еще не поднялась. false — поднять не вышло (адрес занят,
    /// нет данных в записи): подключение нужно поставить заново.
    /// </summary>
    public static async Task<bool> EnsureProxyAsync(ActiveConnection active, Action<string> log)
    {
        if (active.ProxyListen is not { } listen) return true;
        if (DohProxyRunning(listen)) return true;
        var endpoint = active.ProtectedProxyEndpoint is { Length: > 0 } secret ? Storage.Unprotect(secret) : "";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        var boot = active.ProxyBootstrap.Select(x => IPAddress.TryParse(x, out var ip) ? ip : null).OfType<IPAddress>().ToList();
        if (boot.Count == 0) return false;
        try
        {
            await DohProxyStart(uri, boot, listen, log);
            return true;
        }
        catch (Exception ex) when (ex is DohRelayFailedException or ArgumentException)
        {
            log("DoH-прокси программы не запустился: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// IP-адреса DoH-сервера для прокси: только IPv4, через эталонный DoH. Адрес с токеном и имя сервера в журнал
    /// не пишутся: журнал видят пользователь и поддержка.
    /// </summary>
    private static async Task<IReadOnlyList<IPAddress>> ProxyUpstreamAsync(AppSettings settings, Uri endpoint, Action<string> log)
    {
        if (IPAddress.TryParse(endpoint.Host, out var literal))
        {
            if (literal.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new DohRelayFailedException("DoH-прокси программы работает только по IPv4");
            return new[] { literal };
        }
        var (ips, _) = await DnsWire.ResolveDohAsync(settings.ReferenceDoh, endpoint.Host, TimeSpan.FromSeconds(8), CancellationToken.None);
        var v4 = ips.Where(i => i.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Take(2).ToArray();
        if (v4.Length == 0) throw new DohRelayFailedException("Не удалось узнать IP адрес DoH-сервера для прокси");
        log($"IP DoH-сервера для прокси: {string.Join(", ", v4.Select(ip => ip.ToString()))}");
        return v4;
    }

    /// <summary>Источник прежнего DNS сети. В программе — реестр и копия настроек, тесты подставляют свой.</summary>
    internal static Func<IReadOnlyCollection<string>, List<string>> NetworkFallbackDns = ReadNetworkFallbackDns;

    /// <summary>
    /// Прежний DNS сети для работы без программы: исходный статический из копии, иначе выданный роутером (DHCP).
    /// Windows хранит DhcpNameServer и тогда, когда поверх задан статический DNS, поэтому он есть и при смене сервера.
    /// </summary>
    private static List<string> ReadNetworkFallbackDns(IReadOnlyCollection<string> serverAddrs)
    {
        var found = new List<string>();
        void Add(string? list)
        {
            foreach (var x in (list ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (IPAddress.TryParse(x, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(ip) && !serverAddrs.Contains(ip.ToString()) && !found.Contains(ip.ToString()))
                    found.Add(ip.ToString());
        }
        try
        {
            if (File.Exists(Storage.PathOf(BackupFile)))
                foreach (var a in Storage.LoadOrCreate(BackupFile, () => new List<AdapterBackup>())) Add(a.Static);
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                  && n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                                                            or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211))
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{n.Id}");
                Add(key?.GetValue("DhcpNameServer") as string);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or System.Net.NetworkInformation.NetworkInformationException)
        {
            Storage.Trace?.Invoke("Прежний DNS сети не прочитан: " + e.GetType().Name);
        }
        return found.Take(1).ToList();
    }
}
