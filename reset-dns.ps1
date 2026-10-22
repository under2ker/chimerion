# Аварийный сброс DNS, который выставила DNEX.
# Трогает только физические адаптеры, на которых DNEX меняла настройки:
# исходные DNS берутся из dns-backup.json, без копии адаптер переводится на автоматические (DHCP).
# Если DNEX ничего не меняла, скрипт ничего не делает.
# Файлы DNEX удаляются только после успешного восстановления и проверки.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:APPDATA 'DNEX'
$backupPath = Join-Path $dir 'dns-backup.json'
$activePath = Join-Path $dir 'active.json'

if (-not (Test-Path $backupPath) -and -not (Test-Path $activePath)) {
    Write-Host 'DNEX не меняла DNS на этом компьютере: файлов подключения нет. Ничего не сделано.'
    exit 0
}

$backup = @()
if (Test-Path $backupPath) { $backup = @(Get-Content $backupPath -Raw -Encoding UTF8 | ConvertFrom-Json) }
$active = $null
if (Test-Path $activePath) { $active = Get-Content $activePath -Raw -Encoding UTF8 | ConvertFrom-Json }

# Адреса, которые ставила DNEX (серверы и DoH-адреса)
$ours = @()
if ($active) {
    $ours += @($active.addresses)
    $ours += @($active.dohAddedFor)
}
$ours = @($ours | Where-Object { $_ } | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)

$ok = $true
foreach ($a in @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)) {
    $guid = ([string]$a.InterfaceGuid).Trim('{}').ToUpper()
    $entry = $backup | Where-Object { ([string]$_.Guid).Trim('{}').ToUpper() -eq $guid } | Select-Object -First 1
    $cur = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
    $hasOurs = @($cur | Where-Object { $ours -contains $_ }).Count -gt 0
    if (-not $entry -and -not $hasOurs) { continue }

    $want = @()
    if ($entry -and $entry.Static) {
        $want = @(([string]$entry.Static) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not ($ours -contains $_) })
    }
    try {
        if ($want.Count -gt 0) {
            Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses $want
        } else {
            Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses
        }
        foreach ($ip in $ours) {
            Remove-Item "HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\$($a.InterfaceGuid)\DohInterfaceSettings\Doh\$ip" -Recurse -ErrorAction SilentlyContinue
        }
        $mode = if ($want.Count -gt 0) { 'исходные адреса' } else { 'автоматически (DHCP)' }
        Write-Host "Адаптер $($a.Name): DNS $mode"
    } catch {
        $ok = $false
        Write-Host "Ошибка на адаптере $($a.Name): $($_.Exception.Message)"
    }
}

foreach ($ip in $ours) { Remove-DnsClientDohServerAddress -ServerAddress $ip -ErrorAction SilentlyContinue }
Clear-DnsClientCache
ipconfig /flushdns | Out-Null

# Проверка: адресов DNEX не должно остаться ни на одном физическом адаптере
$left = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | ForEach-Object {
    Get-DnsClientServerAddress -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
} | ForEach-Object { $_.ServerAddresses } | Where-Object { $ours -contains $_ })
if ($left.Count -gt 0) { $ok = $false }

if ($ok) {
    Remove-Item $backupPath, $activePath -ErrorAction SilentlyContinue
    Write-Host ''
    Write-Host 'Готово. DNS восстановлен, файлы подключения DNEX удалены.'
} else {
    Write-Host ''
    Write-Host 'Восстановлено не полностью. Файлы DNEX сохранены. Запустите сброс еще раз или проверьте адаптеры в Параметрах Windows.'
}
