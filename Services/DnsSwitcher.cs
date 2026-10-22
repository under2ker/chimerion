using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>Смена DNS на активных адаптерах Windows через PowerShell (NetTCPIP / DnsClient).</summary>
public static class DnsSwitcher
{
    private const string BackupFile = "dns-backup.json";
    private const string ActiveFile = "active.json";

    /// <summary>Сведения о текущем подключении. Файл переживает перезапуск и сбой программы,
    /// поэтому отключиться можно всегда, даже если DNEX закрылась аварийно.</summary>
    public sealed class ActiveConnection
    {
        public Guid ServerId { get; set; }
        public string Name { get; set; } = "";
        public List<string> Addresses { get; set; } = new();
        public string? DohTemplate { get; set; }
        /// <summary>Адреса, для которых DNEX сама зарегистрировала DoH, — только их и удаляем при отключении.</summary>
        public List<string> DohAddedFor { get; set; } = new();
        public DateTime Since { get; set; } = DateTime.Now;
    }

    public static ActiveConnection? GetActive()
    {
        if (!File.Exists(Storage.PathOf(ActiveFile))) return null;
        var a = Storage.LoadOrCreate<ActiveConnection?>(ActiveFile, () => null);
        return a is { Addresses.Count: > 0 } ? a : null;
    }

    private sealed class AdapterBackup
    {
        public int InterfaceIndex { get; set; }
        /// <summary>Постоянный идентификатор адаптера: номер интерфейса может смениться, GUID — нет.</summary>
        public string? Guid { get; set; }
        public string? Name { get; set; }
        /// <summary>Статически заданные DNS (пусто — адреса от DHCP).</summary>
        public string? Static { get; set; }
    }

    public static async Task<string> GetCurrentAsync()
    {
        var (_, output) = await RunPsAsync(
            "$idx = (Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }).ifIndex\n" +
            "$a = Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $idx -contains $_.InterfaceIndex } | ForEach-Object { $_.ServerAddresses }\n" +
            "($a | Select-Object -Unique) -join ', '");
        return string.IsNullOrWhiteSpace(output) ? "не определён" : output;
    }

    public static async Task ApplyAsync(DnsServer s, AppSettings settings, Action<string> log)
    {
        var addrs = new List<string>();
        if (IPAddress.TryParse(s.Primary, out var p)) addrs.Add(p.ToString());
        if (IPAddress.TryParse(s.Secondary, out var sec)) addrs.Add(sec.ToString());

        string? doh = null;
        if (!string.IsNullOrWhiteSpace(s.DohUrl))
        {
            if (!Uri.TryCreate(s.DohUrl.Split('{')[0], UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Некорректный DoH-адрес");
            doh = u.GetLeftPart(UriPartial.Path).Replace("'", "''");

            if (addrs.Count == 0)
            {
                // Для DoH Windows нужен IP сервера: узнаём его через эталонный резолвер.
                var (ips, _) = await DnsWire.ResolveDohAsync(settings.ReferenceDoh, u.Host, TimeSpan.FromSeconds(8), CancellationToken.None);
                addrs.AddRange(ips.Take(2).Select(i => i.ToString()));
                log($"IP DoH-сервера {u.Host}: {string.Join(", ", addrs)}");
            }
        }
        if (addrs.Count == 0) throw new InvalidOperationException("У сервера нет IP-адреса");

        await EnsureBackupAsync(log);

        var list = string.Join(",", addrs.Select(a => $"'{a}'"));
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        // Только физические адаптеры: VPN-туннели и виртуальные сети (Hyper-V, WSL) не трогаем — иначе сломаем их DNS.
        sb.AppendLine("$ifs = Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }");
        if (doh != null)
        {
            sb.AppendLine("if (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {");
            foreach (var a in addrs)
            {
                // Существующую системную запись DoH не трогаем; свою помечаем строкой ADDED, чтобы при отключении удалить только её.
                sb.AppendLine($"  if (-not (Get-DnsClientDohServerAddress -ServerAddress '{a}' -ErrorAction SilentlyContinue)) {{");
                sb.AppendLine($"    Add-DnsClientDohServerAddress -ServerAddress '{a}' -DohTemplate '{doh}' -AllowFallbackToUdp $false -AutoUpgrade $true; Write-Output 'ADDED {a}' }}");
            }
            sb.AppendLine("  Write-Output 'DoH зарегистрирован'");
            sb.AppendLine("} else { Write-Output 'Эта версия Windows не поддерживает DoH — используется обычный DNS' }");
        }
        sb.AppendLine($"foreach ($i in $ifs) {{ Set-DnsClientServerAddress -InterfaceIndex $i.ifIndex -ServerAddresses @({list}); Write-Output ('Адаптер: ' + $i.Name) }}");
        sb.AppendLine("Clear-DnsClientCache");

        // Запись о подключении сохраняем ДО смены DNS: если что-то упадёт посередине, отключение всё равно сработает.
        var prev = GetActive();
        var active = new ActiveConnection
        {
            ServerId = s.Id, Name = s.Name, Addresses = addrs, DohTemplate = doh?.Replace("''", "'"),
            DohAddedFor = prev?.DohAddedFor ?? new(),
        };
        Storage.Save(ActiveFile, active);

        try
        {
            var (code, output) = await RunPsAsync(sb.ToString(), elevated: true);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var l = line.Trim();
                if (l.StartsWith("ADDED ")) active.DohAddedFor.Add(l[6..]);
                else log(l);
            }
            Storage.Save(ActiveFile, active);
            if (code != 0) throw new InvalidOperationException("PowerShell завершился с ошибкой, см. журнал");
        }
        catch (OperationCanceledException)
        {
            // Права не выданы (UAC отклонён): скрипт не запускался, DNS не менялся — запись о подключении не нужна.
            var path = Storage.PathOf(ActiveFile);
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        catch (Exception)
        {
            // Скрипт мог успеть поменять часть адаптеров: возвращаем всё, что было до подключения.
            log("Подключение не удалось — возвращаю исходные настройки DNS");
            try { await RestoreAsync(log); }
            catch (Exception re) { log("Откат не удался, см. журнал: " + re.Message); }
            throw;
        }
        log($"Подключено: {s.Name} ({string.Join(", ", addrs)}{(doh != null ? " + DoH" : "")})");
    }

    /// <summary>Запоминаем исходные настройки один раз — до первой смены.</summary>
    private static async Task EnsureBackupAsync(Action<string> log)
    {
        if (File.Exists(Storage.PathOf(BackupFile))) return;
        var (code, json) = await RunPsAsync(
            "$r = foreach ($a in Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' }) {\n" +
            "  $p = \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\$($a.InterfaceGuid)\"\n" +
            "  $ns = (Get-ItemProperty -Path $p -Name NameServer -ErrorAction SilentlyContinue).NameServer\n" +
            "  [pscustomobject]@{ InterfaceIndex = $a.ifIndex; Guid = [string]$a.InterfaceGuid; Name = $a.Name; Static = [string]$ns }\n" +
            "}\n" +
            "ConvertTo-Json -InputObject @($r) -Compress");
        if (code != 0) throw new InvalidOperationException("Не удалось сохранить исходные настройки DNS: " + json);
        var items = JsonSerializer.Deserialize<List<AdapterBackup>>(json, Storage.Options) ?? new();
        Storage.Save(BackupFile, items);
        log("Исходные настройки DNS сохранены");
    }

    /// <summary>
    /// Безопасное отключение:
    /// 1) каждому адаптеру из резервной копии (ищем по GUID, не по номеру) возвращаем исходные DNS;
    /// 2) любой адаптер, где остались адреса DNEX, сбрасываем на автоматические (DHCP);
    /// 3) удаляем добавленные DNEX записи DoH (глобальные и на интерфейсах);
    /// 4) сбрасываем кэш и сверяем результат — если адреса DNEX остались, сообщаем об ошибке.
    /// </summary>
    public static async Task RestoreAsync(Action<string> log)
    {
        var path = Storage.PathOf(BackupFile);
        var backup = File.Exists(path) ? Storage.LoadOrCreate(BackupFile, () => new List<AdapterBackup>()) : new();
        var active = GetActive();

        static string Q(string v) => "'" + v.Replace("'", "''") + "'";
        static IEnumerable<string> Ips(string? v) => (v ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => IPAddress.TryParse(x, out _)).Select(x => IPAddress.Parse(x).ToString());

        var ours = (active?.Addresses ?? new()).Concat(active?.DohAddedFor ?? new()).SelectMany(x => Ips(x)).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$ours = @({string.Join(",", ours.Select(Q))})");
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
              $cur = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
              $hasOurs = @($cur | Where-Object { $ours -contains $_ }).Count -gt 0
              if ($backup.ContainsKey($g)) { $want = $backup[$g] }
              elseif ($byIndex.ContainsKey([int]$a.ifIndex)) { $want = $byIndex[[int]$a.ifIndex] }
              elseif ($hasOurs) { $want = '' }
              else { continue }
              # Если в «исходных» почему-то оказались адреса DNEX — не возвращаем их, а ставим автоматические.
              if ($want -and @($want -split ',' | Where-Object { $ours -contains $_ }).Count -gt 0) { $want = '' }
              try {
                if ($want) { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses ($want -split ',') -ErrorAction Stop }
                else { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop }
              } catch { Write-Output "WARN $($a.Name): $($_.Exception.Message)" }
              foreach ($ip in $ours) {
                Remove-Item "HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\$g\DohInterfaceSettings\Doh\$ip" -Recurse -ErrorAction SilentlyContinue
              }
              $after = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
              $mode = if ($want) { 'исходные' } else { 'автоматически (DHCP)' }
              Write-Output "RESULT $($a.Name) → $($mode): $($after -join ', ')"
              if (@($after | Where-Object { $ours -contains $_ }).Count -gt 0) { Write-Output "LEFT $($a.Name)" }
            }
            foreach ($ip in $ours) { Remove-DnsClientDohServerAddress -ServerAddress $ip -ErrorAction SilentlyContinue }
            Clear-DnsClientCache
            ipconfig /flushdns | Out-Null
            """);

        var (code, output) = await RunPsAsync(sb.ToString(), elevated: true);
        bool left = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (line.StartsWith("RESULT ")) log("  " + line[7..]);
            else if (line.StartsWith("WARN ")) log("  ⚠ " + line[5..]);
            else if (line.StartsWith("LEFT ")) { left = true; log("  ✗ Остались адреса DNEX: " + line[5..]); }
            else if (line.Length > 0) log("  " + line);
        }
        if (code != 0) throw new InvalidOperationException("Ошибка отключения, см. журнал");
        if (left) throw new InvalidOperationException("DNS сброшен не на всех адаптерах — см. журнал");

        if (File.Exists(path)) File.Delete(path);
        var activePath = Storage.PathOf(ActiveFile);
        if (File.Exists(activePath)) File.Delete(activePath);
        log("Отключено: исходные настройки DNS восстановлены");
    }

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

    public static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Запуск PowerShell. Программа работает без прав администратора; права запрашиваются (UAC)
    /// только на сам скрипт смены DNS. Скрипт передаётся через -EncodedCommand прямо в аргументах,
    /// без временного .ps1-файла, который другой процесс мог бы подменить до запуска.
    /// </summary>
    public static async Task<(int Code, string Output)> RunPsAsync(string script, bool elevated = false)
    {
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                      "WindowsPowerShell", "v1.0", "powershell.exe"); // полный путь — не подхватим чужой powershell.exe из PATH
        if (!elevated || IsAdmin()) return await RunHiddenAsync(powershell, script);

        // Повышенный процесс не даёт перенаправить вывод, поэтому он пишет его в файл в профиле пользователя.
        Directory.CreateDirectory(Storage.Dir);
        var outFile = Path.Combine(Storage.Dir, $"ps-{Guid.NewGuid():N}.log");
        // Скрипт передаётся внутрь как строка и разбирается через [scriptblock]::Create внутри try:
        // так даже синтаксическая ошибка попадает в журнал, а не теряется молча.
        var inner = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var of = outFile.Replace("'", "''");
        var wrapped =
            "$ProgressPreference = 'SilentlyContinue'\n" +
            "try {\n" +
            $"  $code = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{inner}'))\n" +
            $"  & ([scriptblock]::Create($code)) *>&1 | Out-File -FilePath '{of}' -Encoding utf8\n" +
            "  exit 0\n" +
            $"}} catch {{ ('ОШИБКА: ' + $_.Exception.Message) | Out-File -FilePath '{of}' -Append -Encoding utf8; exit 1 }}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        var psi = new ProcessStartInfo(powershell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
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
            throw new OperationCanceledException("Запрос прав администратора отклонён");
        }
        finally
        {
            try { File.Delete(outFile); } catch (IOException) { }
        }
    }

    private static async Task<(int Code, string Output)> RunHiddenAsync(string powershell, string script)
    {
        // $ProgressPreference: иначе PowerShell пишет в stderr служебный CLIXML («Подготовка модулей…»).
        var full = "$ProgressPreference = 'SilentlyContinue'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var psi = new ProcessStartInfo(powershell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
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
        if (err.StartsWith("#< CLIXML")) err = ""; // служебный поток прогресса, не ошибка
        var outp = (await stdout).Trim();
        return (proc.ExitCode, string.IsNullOrEmpty(err) ? outp : $"{outp}\n{err}".Trim());
    }
}
