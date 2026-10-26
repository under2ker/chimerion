# Развертывание общей карты серверов на Cloudflare (Workers + D1) и подключение Telegram-бота.
# Запуск: powershell -ExecutionPolicy Bypass -File E:\DNEX\DNEX\backend\crowd\deploy.ps1
# Нужно: npx wrangler login (уже сделан для R2) и токен бота в пользовательской переменной TG_BOT_TOKEN.
# Секреты не печатаются. Ключ администратора и адрес сервера сохраняются в E:\DNEX\signing (вне репозитория).
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 по умолчанию пробует старый TLS, и запрос к Worker обрывается
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Set-Location $PSScriptRoot
$signing = 'E:\DNEX\signing'

function Random-Secret { -join ((1..40) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) }) }
function Put-Secret([string]$name, [string]$value) {
    $value | npx wrangler secret put $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не удалось записать секрет $name" }
    Write-Output "Секрет $name записан"
}

# 1. База D1: создать, если еще нет, и вписать ее id в wrangler.toml
$list = npx wrangler d1 list --json | Out-String | ConvertFrom-Json
$db = $list | Where-Object { $_.name -eq 'chimerion-crowd' } | Select-Object -First 1
if (-not $db) {
    npx wrangler d1 create chimerion-crowd | Out-Null
    $db = (npx wrangler d1 list --json | Out-String | ConvertFrom-Json) | Where-Object { $_.name -eq 'chimerion-crowd' } | Select-Object -First 1
}
if (-not $db) { throw 'База chimerion-crowd не создалась' }
$toml = Get-Content wrangler.toml -Raw
$toml = $toml -replace 'database_id = ".*"', "database_id = `"$($db.uuid)`""
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'wrangler.toml'), $toml, (New-Object Text.UTF8Encoding $false))
Write-Output "База D1: $($db.uuid)"

# 2. Таблицы
npx wrangler d1 execute chimerion-crowd --remote --file schema.sql | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать таблицы' }
Write-Output 'Таблицы созданы'

# 3. Секреты. Соль и секрет вебхука случайные; ключ администратора сохраняется, чтобы переподключить бота
$adminFile = Join-Path $signing 'crowd-admin.txt'
$admin = if (Test-Path $adminFile) { (Get-Content $adminFile -Raw).Trim() } else { Random-Secret }
[IO.File]::WriteAllText($adminFile, $admin)
$token = [Environment]::GetEnvironmentVariable('TG_BOT_TOKEN', 'User')
if (-not $token) { throw 'Нет пользовательской переменной TG_BOT_TOKEN' }

# 4. Выкладка: сначала Worker (секреты можно записать только существующему), потом секреты
$out = npx wrangler deploy 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "Выкладка не удалась: $out" }
$url = [regex]::Match($out, 'https://chimerion-crowd\.[a-z0-9-]+\.workers\.dev').Value
if (-not $url) { throw "Не нашел адрес Worker в выводе: $out" }
[IO.File]::WriteAllText((Join-Path $signing 'crowd-url.txt'), $url)
Write-Output "Worker: $url"

Put-Secret 'SALT' (Random-Secret)
Put-Secret 'TG_WEBHOOK_SECRET' (Random-Secret)
Put-Secret 'ADMIN_KEY' $admin
Put-Secret 'TG_BOT_TOKEN' $token

# 5. Бот: вебхук и команды. Запрос к Telegram делает сам Worker, поэтому блокировка Telegram здесь не мешает
Start-Sleep -Seconds 5
$r = Invoke-RestMethod -Method Post -Uri "$url/tg-setup" -Headers @{ 'X-Admin-Key' = $admin }
Write-Output "Бот: вебхук $($r.webhook), команды $($r.commands)"
$s = Invoke-RestMethod -Uri "$url/v1/stats"
Write-Output "Проверка: сеть AS$($s.asn) $($s.org), записей $($s.global.Count)"
Write-Output 'Готово'
