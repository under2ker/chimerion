# Аварийный сброс DNS, который выставила CHIMERION.
# Трогает только физические адаптеры, на которых CHIMERION меняла настройки:
# исходные DNS берутся из dns-backup.json, без копии адаптер переводится на автоматические (DHCP).
# Если CHIMERION ничего не меняла, скрипт ничего не делает.
# Файлы CHIMERION удаляются только после успешного восстановления и проверки.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:APPDATA 'DNEX'
$backupPath = Join-Path $dir 'dns-backup.json'
$activePath = Join-Path $dir 'active.json'
$v6Path = Join-Path $dir 'ipv6-disabled.txt'

# Правила NRPT режима «Два DNS» (домены ИИ в Smart DNS): снимаются всегда, даже без файлов подключения
$rules = @(Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq 'DNEX' })
foreach ($r in $rules) { Remove-DnsClientNrptRule -Name $r.Name -Force -ErrorAction SilentlyContinue }
if ($rules.Count -gt 0) { Write-Host "Сняты правила для доменов ИИ: $($rules.Count)" }

if (-not (Test-Path $backupPath) -and -not (Test-Path $activePath) -and -not (Test-Path $v6Path)) {
    Write-Host 'CHIMERION не меняла DNS на этом компьютере: файлов подключения нет. Ничего не сделано.'
    exit 0
}

$backup = @()
if (Test-Path $backupPath) { $backup = @(Get-Content $backupPath -Raw -Encoding UTF8 | ConvertFrom-Json) }
$active = $null
if (Test-Path $activePath) { $active = Get-Content $activePath -Raw -Encoding UTF8 | ConvertFrom-Json }

# Адреса, которые ставила CHIMERION (серверы и DoH-адреса)
$ours = @()
if ($active) {
    $ours += @($active.addresses)
    $ours += @($active.dohAddedFor)
    $ours += @($active.adapterAddresses)
}
$ours = @($ours | Where-Object { $_ } | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)

$ok = $true

# IPv6, который CHIMERION выключила на время подключения (у сервера не было IPv6): включаем первым.
# GUID адаптеров есть в записи о подключении и в ipv6-disabled.txt (туда скрипт подключения пишет сразу)
$v6 = @()
if ($active) { $v6 += @($active.ipv6Disabled) }
if (Test-Path $v6Path) { $v6 += @(Get-Content $v6Path) }
$v6 = @($v6 | Where-Object { $_ } | ForEach-Object { ([string]$_).Trim().ToUpper() } | Select-Object -Unique)
foreach ($g in $v6) {
    foreach ($a in @(Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { ([string]$_.InterfaceGuid).ToUpper() -eq $g })) {
        try {
            Enable-NetAdapterBinding -Name $a.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop
            $b6 = Get-NetAdapterBinding -Name $a.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue
            if ($b6 -and -not $b6.Enabled) { throw 'Windows оставила IPv6 выключенным' }
            Write-Host "Адаптер $($a.Name): IPv6 включен обратно"
        } catch { $ok = $false; Write-Host "Не удалось включить IPv6 на $($a.Name): $($_.Exception.Message)" }
    }
}

foreach ($a in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
    $guid = ([string]$a.InterfaceGuid).Trim('{}').ToUpper()
    $entry = $backup | Where-Object { ([string]$_.Guid).Trim('{}').ToUpper() -eq $guid } | Select-Object -First 1
    $cur = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue).ServerAddresses)
    $hasOurs = @($cur | Where-Object { $ours -contains $_ }).Count -gt 0
    if (-not $entry -and -not $hasOurs) { continue }

    $want = @()
    if ($entry -and $entry.Static) {
        $want = @(([string]$entry.Static) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not ($ours -contains $_) })
    }
    $want6 = @()
    if ($entry -and $entry.Static6) {
        $want6 = @(([string]$entry.Static6) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not ($ours -contains $_) })
    }
    try {
        Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop
        $all = @($want + $want6 | Select-Object -Unique)
        if ($all.Count -gt 0) {
            Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses $all -ErrorAction Stop
        }
        foreach ($ip in $ours) {
            Remove-Item "HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\$($a.InterfaceGuid)\DohInterfaceSettings\Doh\$ip" -Recurse -ErrorAction SilentlyContinue
        }
        $mode = if ($all.Count -gt 0) { 'исходные адреса' } else { 'автоматически (DHCP)' }
        Write-Host "Адаптер $($a.Name): DNS $mode"
    } catch {
        $ok = $false
        Write-Host "Ошибка на адаптере $($a.Name): $($_.Exception.Message)"
    }
}

# На Windows 10 без поддержки DoH этой команды нет
if (Get-Command Remove-DnsClientDohServerAddress -ErrorAction SilentlyContinue) {
    foreach ($ip in $ours) { Remove-DnsClientDohServerAddress -ServerAddress $ip -ErrorAction SilentlyContinue }
}
Clear-DnsClientCache
ipconfig /flushdns | Out-Null

# Проверка: адресов CHIMERION не должно остаться ни на одном физическом адаптере
$left = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | ForEach-Object {
    Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue
} | ForEach-Object { $_.ServerAddresses } | Where-Object { $ours -contains $_ })
if ($left.Count -gt 0) { $ok = $false }

if ($ok) {
    Remove-Item $backupPath, $activePath, $v6Path -ErrorAction SilentlyContinue
    Write-Host ''
    Write-Host 'Готово. DNS восстановлен, файлы подключения CHIMERION удалены.'
} else {
    Write-Host ''
    Write-Host 'Восстановлено не полностью. Файлы CHIMERION сохранены. Запустите сброс еще раз или проверьте адаптеры в Параметрах Windows.'
}
