# Выкладка сайта chimerion.dev: копия папки во временную, сжатие HTML, wrangler pages deploy.
#   powershell -ExecutionPolicy Bypass -File site\deploy.ps1
# Нужен вход в Cloudflare (npx wrangler login). Исходники в site/ остаются читаемыми.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dist = Join-Path $env:TEMP 'chimerion-site-dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
Copy-Item $src $dist -Recurse
Remove-Item (Join-Path $dist 'deploy.ps1')

# Version of the latest GitHub release goes into the download links (from Russia the page often cannot reach
# the GitHub API, and then the links in the HTML are what people download). No answer: stop, do not publish stale links.
# -DryRun: only print what would be substituted.
$rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/under2ker/chimerion/releases/latest' -TimeoutSec 20 -Headers @{ 'User-Agent' = 'chimerion-site-deploy' }
$ver = ([string]$rel.tag_name) -replace '^v', ''
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "Latest release tag is not a version: '$($rel.tag_name)'" }
$zipName = "CHIMERION-$ver-win-x64.zip"
$zip = @($rel.assets) | Where-Object { $_.name -eq $zipName } | Select-Object -First 1
if (-not $zip) { throw "Release v$ver has no $zipName" }
$size = ([math]::Round($zip.size / 1MB, 1)).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture).Replace('.', ',')
$utf8 = New-Object System.Text.UTF8Encoding($false)
$indexPath = Join-Path $dist 'index.html'
$html = [IO.File]::ReadAllText($indexPath, [Text.Encoding]::UTF8)
$html = [regex]::Replace($html, '/releases/download/v\d+\.\d+\.\d+/', "/releases/download/v$ver/")
$html = [regex]::Replace($html, 'CHIMERION-\d+\.\d+\.\d+-win-x64\.zip', $zipName)
$html = [regex]::Replace($html, '(class="dl-text">[^<]*?)\d+\.\d+\.\d+(\s*<small>)[\d,]+', "`${1}$ver`${2}$size")
if ($html -notmatch [regex]::Escape("/releases/download/v$ver/$zipName")) { throw 'Download link not found in index.html' }
[IO.File]::WriteAllText($indexPath, $html, $utf8)
Write-Host "Download links: v$ver, $size MB"
if ($args -contains '-DryRun') { Write-Host "Dry run: $dist"; return }

foreach ($f in 'index.html', '404.html') {
    $p = Join-Path $dist $f
    npx -y html-minifier-terser@7.2.0 --collapse-whitespace --conservative-collapse --remove-comments --minify-css true --minify-js true -o "$p.min" $p
    if ($LASTEXITCODE -ne 0) { throw "Не удалось сжать $f" }
    Move-Item "$p.min" $p -Force
}
Push-Location $env:TEMP
try { npx wrangler pages deploy $dist --project-name chimerion --branch main --commit-dirty=true }
finally { Pop-Location }
