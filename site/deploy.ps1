# Выкладка сайта chimerion.dev: копия папки во временную, сжатие HTML, wrangler pages deploy.
#   powershell -ExecutionPolicy Bypass -File site\deploy.ps1
# Нужен вход в Cloudflare (npx wrangler login). Исходники в site/ остаются читаемыми.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dist = Join-Path $env:TEMP 'chimerion-site-dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
Copy-Item $src $dist -Recurse
Remove-Item (Join-Path $dist 'deploy.ps1')
foreach ($f in 'index.html', '404.html') {
    $p = Join-Path $dist $f
    npx -y html-minifier-terser@7.2.0 --collapse-whitespace --conservative-collapse --remove-comments --minify-css true --minify-js true -o "$p.min" $p
    if ($LASTEXITCODE -ne 0) { throw "Не удалось сжать $f" }
    Move-Item "$p.min" $p -Force
}
Push-Location $env:TEMP
try { npx wrangler pages deploy $dist --project-name chimerion --branch main --commit-dirty=true }
finally { Pop-Location }
