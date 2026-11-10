<#
  Быстрый обход интерфейса отладочной сборки CHIMERION со снимками всех страниц, вкладок, диалогов и прокрутки.

  .\tools\ui-shots.ps1                          # собрать при изменениях, запустить, снять все в обычном размере окна
  .\tools\ui-shots.ps1 -Sizes 1440x1300,1000x760  # еще и в заданных размерах
  .\tools\ui-shots.ps1 -Only settings,journal   # только эти шаги (по началу имени)
  .\tools\ui-shots.ps1 -NoScroll -NoBuild       # без снимков прокрутки и без проверки сборки

  Скорость: фиксированных пауз нет. После нажатия скрипт ждет только то, что реально грузится:
  нужный элемент появился, кадр сменился и перестал меняться, диалог открылся или закрылся.
  PNG пишутся в фоне, поиск прокрутки идет одним запросом UI Automation.

  Безопасность:
  - работает только с копией данных (DNEX_DATA_DIR), рабочие настройки и серверы владельца не трогаются;
  - нажимает только кнопки из списка $Plan; переключатели (защита, YouTube, Telegram), «Подключить»,
    «Сохранить», «Отправить», покупки и внешние ссылки запрещены и дополнительно проверяются перед нажатием;
  - мышь и клавиатура не используются: нажатия через UI Automation, снимки через PrintWindow,
    поэтому другие окна на экране не мешают и не попадают в кадр;
  - диалоги закрываются как крестиком (WM_CLOSE), то есть без сохранения.
  Если запущен обычный CHIMERION, скрипт остановится: два экземпляра программы не запускаются.
#>
param(
    [string]$Out,
    [string]$DataDir = "E:\CHIMERION\tiktok-01\data",
    # Пусто: окно в том размере, в каком программа открылась сама
    [string[]]$Sizes = @(),
    [string[]]$Only,
    [switch]$NoBuild,
    [switch]$NoScroll,
    [switch]$KeepOpen,
    [switch]$Trace,     # печатать время каждого нажатия и снимка
    [int]$MaxScroll = 5, # сколько экранов прокрутки снимать на страницу
    [switch]$Animations # оставить анимации программы (по умолчанию выключены: снимки без ожидания переходов)
)
$ErrorActionPreference = "Stop"
$sw = [Diagnostics.Stopwatch]::StartNew()
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo "bin\Debug\net8.0-windows\CHIMERION.exe"
if (-not $Out) { $Out = Join-Path "E:\CHIMERION\ui-shots" (Get-Date -Format "yyyy-MM-dd_HH-mm-ss") }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ("UiShot2" -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging;
using System.Runtime.InteropServices; using System.Threading; using System.Threading.Tasks;
public static class UiShot2 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

    // Видимые окна процесса крупнее подсказок и всплывающих меню
    public static IntPtr[] Windows(int pid) {
        var list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint id; RECT r;
            GetWindowThreadProcessId(h, out id);
            if (id == pid && IsWindowVisible(h) && GetWindowRect(h, out r) && r.R - r.L > 200 && r.B - r.T > 120) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    // Ждать окна, кроме main: появилось (want=true) или все закрылись (want=false)
    public static IntPtr[] WaitOthers(int pid, IntPtr main, bool want, int maxMs) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            var all = Windows(pid); var others = new List<IntPtr>();
            foreach (var h in all) if (h != main) others.Add(h);
            if ((others.Count > 0) == want || sw.ElapsedMilliseconds >= maxMs) return others.ToArray();
            Thread.Sleep(25);
        }
    }

    static Bitmap Grab(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        var b = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
        return b;
    }

    // Хэш по каждому 7-му пикселю: быстро и ловит любое изменение крупнее точки
    static long Hash(Bitmap b) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            int n = d.Stride * d.Height / 4; var buf = new int[n]; Marshal.Copy(d.Scan0, buf, 0, n);
            long h = unchecked((long)14695981039346656037UL);
            for (int i = 0; i < n; i += 7) { h ^= buf[i]; h *= 1099511628211L; }
            return h ^ ((long)b.Width << 40) ^ ((long)b.Height << 20);
        } finally { b.UnlockBits(d); }
    }

    static readonly List<Task> Pending = new List<Task>();

    // Снимок окна: сначала ждем, что кадр сменился с прошлого снимка (prev, до changeMs: страница
    // еще не перерисовалась), потом что он перестал меняться (анимации, загрузка), но не дольше stableMs.
    // PNG пишется в фоне. Возвращает хэш снятого кадра.
    public static long Save(IntPtr h, string path, long prev, int changeMs, int stableMs) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var b = Grab(h); long last = Hash(b);
        while (last == prev && sw.ElapsedMilliseconds < changeMs) {
            Thread.Sleep(25); b.Dispose(); b = Grab(h); last = Hash(b);
        }
        sw.Restart();
        while (sw.ElapsedMilliseconds < stableMs) {
            Thread.Sleep(40);
            var n = Grab(h); long hh = Hash(n);
            b.Dispose(); b = n;
            if (hh == last) break;
            last = hh;
        }
        var bmp = b;
        lock (Pending) Pending.Add(Task.Run(() => { bmp.Save(path, ImageFormat.Png); bmp.Dispose(); }));
        return last;
    }

    public static void Flush() { Task[] all; lock (Pending) { all = Pending.ToArray(); Pending.Clear(); } Task.WaitAll(all); }
}
"@
}

# ---------- Что нажимать ----------
# n: имя файла; c: что нажать по порядку (имя элемента, * — шаблон); tabs: вкладки внутри страницы;
# dialog: шаг открывает отдельное окно; back: чем вернуться после шага
$Plan = @(
    @{ n = "main";              c = @("Главная") },
    @{ n = "servers";           c = @("Серверы"); tabs = @("Рабочие", "Все", "Избранные") },
    @{ n = "servers-graph";     c = @("Серверы", "График качества"); dialog = $true },
    @{ n = "addserver";         c = @("Добавить сервер"); dialog = $true },
    @{ n = "mode";              c = @("Режим") },
    @{ n = "tools";             c = @("Telegram и YouTube") },
    @{ n = "flow";              c = @("Google Flow"); tabs = @("Chrome", "Edge", "Яндекс Браузер") },
    @{ n = "diag";              c = @("Диагностика") },
    @{ n = "diag-history";      c = @("Диагностика", "История сети"); dialog = $true },
    @{ n = "speed";             c = @("Тест скорости") },
    @{ n = "accounts";          c = @("Аккаунты") },
    @{ n = "journal";           c = @("Журнал"); tabs = @("Все", "Успех*", "Предупреждения", "Ошибки") },
    @{ n = "settings";          c = @("Настройки"); tabs = @("Основные", "Приватность", "Проверка серверов", "Сторож и трей", "Два DNS", "Внешний вид", "Данные", "О программе") },
    @{ n = "settings-twodns";   c = @("Настройки", "Два DNS", "Настроить два DNS"); dialog = $true },
    @{ n = "dns";               c = @("CHIMERION DNS") },
    # Вкладки «Помощи» пока рамки без UI Automation: снимается только первая
    @{ n = "help";              c = @("Помощь") },
    @{ n = "report";            c = @("Сообщить о сбое"); dialog = $true },
    @{ n = "compact";           c = @("Компактно"); back = "Полный вид" }
)
# Никогда не нажимать, даже если имя совпало случайно
$Deny = '^(Защита|Включено|Выключено|Подключить.*|Отключить|Сохранить|Отправить.*|Очистить|Купить.*|Написать|Подробнее|Найти новые серверы|Подобрать .*|Запустить тест|Перепроверить|Проверить .*|Включить.*|Убрать|Открыть.*|Экспорт.*|Импорт.*|Вернуть .*|Рекомендуемые значения|English|Русский|Светлая|Текст лицензии|История изменений|Мои обращения|Баг-репорты|Идеи|Скопировать.*|Сохранить в файл|Папка журналов|Отчет в один клик|Канал в Telegram|Сайт chimerion\.dev|Версия.*)$'
# Ссылки (Hyperlink) не нажимаются никогда: они открывают браузер или Telegram

# ---------- Сборка и запуск ----------
$other = Get-Process CHIMERION -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $exe }
if ($other) { throw "Запущен CHIMERION ($($other[0].Path)). Закройте его: два экземпляра программы не запускаются." }
Get-Process CHIMERION -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force

if (-not $NoBuild) {
    $stale = -not (Test-Path $exe)
    if (-not $stale) {
        # Только исходники программы: bin, obj, tests, tools, backend и .git не обходим
        $t = (Get-Item $exe).LastWriteTime
        $dirs = Get-ChildItem $repo -Directory | Where-Object { $_.Name -notmatch '^(bin|obj|tests|tools|backend|docs|\..*|node_modules|packages)$' }
        $files = @(Get-ChildItem -Path "$repo\*" -File -Include *.cs, *.xaml, *.csproj) + @($dirs | ForEach-Object { Get-ChildItem $_.FullName -Recurse -File })
        $stale = [bool]($files | Where-Object { $_.LastWriteTime -gt $t } | Select-Object -First 1)
    }
    if ($stale) {
        Write-Host "Сборка..."
        & dotnet build (Join-Path $repo "CHIMERION.csproj") -c Debug -v q -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Сборка не прошла: dotnet build CHIMERION.csproj -c Debug" }
    }
}
Write-Host ("Сборка проверена: {0:0.0} с" -f $sw.Elapsed.TotalSeconds)

if (-not (Test-Path $DataDir)) { $DataDir = Join-Path $env:TEMP "chimerion-ui-shots-data"; New-Item -ItemType Directory -Force $DataDir | Out-Null }
New-Item -ItemType Directory -Force $Out | Out-Null
$env:DNEX_DATA_DIR = $DataDir
if (-not $Animations) { $env:DNEX_NO_ANIMATION = "1" }
$proc = Start-Process $exe -PassThru
Remove-Item Env:DNEX_DATA_DIR, Env:DNEX_NO_ANIMATION -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 300; $i++) { $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 25 }
if ($proc.MainWindowHandle -eq 0) { throw "Окно программы не появилось" }
$main = $proc.MainWindowHandle
[UiShot2]::ShowWindow($main, 5) | Out-Null  # показать как есть: размер и развернутость не меняем
$root = [Windows.Automation.AutomationElement]::FromHandle($main)

# ---------- Помощники ----------
$AE = [Windows.Automation.AutomationElement]
$isButton = New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Button)

# Поиск ждет, пока элемент появится (страница еще строится), до maxMs
function Find-El($scope, [string]$name, [int]$maxMs = 4000) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($name.Contains("*")) {
            $cr = New-Object Windows.Automation.CacheRequest; $cr.Add($AE::NameProperty)
            $cr.Push()
            try { $all = $scope.FindAll("Descendants", [Windows.Automation.Condition]::TrueCondition) } finally { $cr.Pop() }
            foreach ($e in $all) { if ($e.Cached.Name -like $name) { return $e } }
        } else {
            $byName = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name)
            $el = $scope.FindFirst("Descendants", (New-Object Windows.Automation.AndCondition($byName, $isButton)))
            if (-not $el) { $el = $scope.FindFirst("Descendants", $byName) }
            if ($el) { return $el }
        }
        Start-Sleep -Milliseconds 30
    } while ($t.ElapsedMilliseconds -lt $maxMs)
    return $null
}

function T([string]$what, [double]$from) { if ($Trace) { Write-Host ("    {0,6:0} мс  {1}" -f (($sw.Elapsed.TotalMilliseconds) - $from), $what) -ForegroundColor DarkGray } }

function Press([string]$name) {
    $t0 = $sw.Elapsed.TotalMilliseconds
    try { return (Press-Retry $name) } finally { T "нажать $name" $t0 }
}

function Press-Retry([string]$name) {
    # Страница могла перестроиться между поиском и нажатием: следующая попытка ищет элемент заново
    for ($try = 0; $try -lt 3; $try++) {
        $r = Press-Once $name
        if (-not ($r -is [string])) { return $r }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Press-Once([string]$name) {
    $el = Find-El $root $name
    if (-not $el) { Write-Host "  нет элемента: $name" -ForegroundColor Yellow; return $false }
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    $pat = $null
    try {
        while ($el) {
            if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat) -or
                $el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat) -or
                $el.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { break }
            $el = $walker.GetParent($el)
        }
        if (-not $el) { Write-Host "  не нажимается: $name" -ForegroundColor Yellow; return $false }
        $real = $el.Current.Name
        $ct = $el.Current.ControlType
        if ($ct -eq [Windows.Automation.ControlType]::CheckBox -or $ct -eq [Windows.Automation.ControlType]::Hyperlink -or $real -match $Deny) {
            Write-Host "  запрещено нажимать: $real" -ForegroundColor Red; return $false
        }
        # Кнопка бывает недоступна, пока идет проверка (диагностика до ~15 с): тут ждем
        for ($i = 0; $i -lt 400 -and -not $el.Current.IsEnabled; $i++) { Start-Sleep -Milliseconds 50 }
        if ($pat -is [Windows.Automation.InvokePattern]) { $pat.Invoke() }
        elseif ($pat -is [Windows.Automation.SelectionItemPattern]) { $pat.Select() }
        else { $pat.Toggle() }
    } catch [System.Windows.Automation.ElementNotAvailableException] { return "retry" }
    catch { Write-Host "  не нажалось: $name ($($_.Exception.Message))" -ForegroundColor Yellow; return $false }
    return $true
}

$script:count = 0
$script:last = 0L
function File([string]$label, [string]$suffix = "") { Join-Path $Out ("{0:000}-{1}{2}.png" -f $script:count, $label, $suffix) }

function Shoot([string]$label) {
    $script:count++
    $t0 = $sw.Elapsed.TotalMilliseconds
    $script:last = [UiShot2]::Save($main, (File $label), $script:last, 700, 1200)
    T "снимок $label" $t0
    $t0 = $sw.Elapsed.TotalMilliseconds
    if (-not $NoScroll) { Shoot-Scroll $label; T "прокрутка" $t0 }
}

# Окна, открытые шагом: ждем появления, снимаем, закрываем без сохранения и ждем закрытия
function Shoot-Dialogs([string]$label) {
    $script:count++
    $extra = [UiShot2]::WaitOthers($proc.Id, $main, $true, 3000)
    if (-not $extra) { Write-Host "  окно не открылось: $label" -ForegroundColor Yellow; return }
    $k = 0
    foreach ($h in $extra) {
        $k++
        [UiShot2]::Save($h, (File $label "-dialog$k"), 0, 0, 1200) | Out-Null
        [UiShot2]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }
    [UiShot2]::WaitOthers($proc.Id, $main, $false, 2000) | Out-Null
}

# Длинные страницы: самая большая прокручиваемая область снимается экран за экраном.
# Свойства всех областей приходят одним запросом (CacheRequest), а не по одному на элемент
function Shoot-Scroll([string]$label) {
    $SP = [Windows.Automation.ScrollPattern]
    $cr = New-Object Windows.Automation.CacheRequest
    $cr.Add($AE::BoundingRectangleProperty); $cr.Add($SP::Pattern)
    $cr.Add($SP::VerticallyScrollableProperty); $cr.Add($SP::VerticalViewSizeProperty)
    $cond = New-Object Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)
    $best = $null; $area = 0; $view = 100
    # Вкладки одного шага обычно листают ту же область: сначала пробуем ее, без обхода всего дерева
    if ($script:scrollEl) {
        try {
            $c = $script:scrollEl.GetUpdatedCache($cr)
            if (-not $c.Current.IsOffscreen -and $c.GetCachedPropertyValue($SP::VerticallyScrollableProperty)) {
                $best = $c; $view = $c.GetCachedPropertyValue($SP::VerticalViewSizeProperty)
            }
        } catch { $script:scrollEl = $null }
    }
    $all = @()
    if (-not $best) {
        $cr.Push()
        try { $all = $root.FindAll("Descendants", $cond) } finally { $cr.Pop() }
    }
    foreach ($e in $all) {
        try {
            if (-not $e.GetCachedPropertyValue($SP::VerticallyScrollableProperty)) { continue }
            $r = $e.Cached.BoundingRectangle
            if ($r.Width * $r.Height -gt $area) { $area = $r.Width * $r.Height; $best = $e; $view = $e.GetCachedPropertyValue($SP::VerticalViewSizeProperty) }
        } catch {}
    }
    if ($best) { $script:scrollEl = $best }
    if (-not $best -or $view -ge 99.5) { return }
    $sp = $best.GetCurrentPattern($SP::Pattern)
    $view = [Math]::Max(10, $view)
    $part = 1
    for ($p = $view * 0.9; $part -lt $MaxScroll; $p += $view * 0.9) {
        $sp.SetScrollPercent(-1, [Math]::Min(100, $p))
        $part++
        $script:last = [UiShot2]::Save($main, (File $label "-scroll$part"), $script:last, 300, 600)
        if ($p -ge 100) { break }
    }
    $sp.SetScrollPercent(-1, 0)
}

# ---------- Обход ----------
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # -File передает «a,b» одной строкой
$steps = if ($Only) { $Plan | Where-Object { $n = $_.n; [bool]($Only | Where-Object { $n -like "$_*" }) } } else { $Plan }
foreach ($size in $(if ($Sizes) { $Sizes } else { @("") })) {
    if ($size) {
        $w, $h = $size -split "x" | ForEach-Object { [int]$_ }
        [UiShot2]::SetWindowPos($main, [IntPtr]::Zero, 0, 0, $w, $h, 0x14) | Out-Null
    }
    $r = New-Object UiShot2+RECT; [UiShot2]::GetWindowRect($main, [ref]$r) | Out-Null
    $tag = "{0}x{1}" -f ($r.R - $r.L), ($r.B - $r.T)
    Write-Host "Размер окна $tag" -ForegroundColor Cyan
    foreach ($s in $steps) {
        $t0 = $sw.Elapsed.TotalSeconds
        $script:scrollEl = $null
        $ok = $true
        foreach ($c in $s.c) { $ok = (Press $c) -and $ok }
        if (-not $ok) { continue }
        if ($s.dialog) { Shoot-Dialogs ("{0}-{1}" -f $tag, $s.n) }
        elseif ($s.tabs) {
            foreach ($t in $s.tabs) {
                if (Press $t) { Shoot ("{0}-{1}-{2}" -f $tag, $s.n, ($t -replace '[*\\/:?"<>| ]', '').ToLower()) }
            }
        } else { Shoot ("{0}-{1}" -f $tag, $s.n) }
        if ($s.back) { Press $s.back | Out-Null; $script:last = 0L }
        Write-Host ("  {0,-18} {1,5:0.0} с  (+{2:0.0})" -f $s.n, $sw.Elapsed.TotalSeconds, ($sw.Elapsed.TotalSeconds - $t0))
    }
}
[UiShot2]::Flush()

# ---------- Итог: страница со всеми снимками ----------
$imgs = Get-ChildItem $Out -Filter *.png | Sort-Object Name
$html = "<!doctype html><meta charset=utf-8><title>CHIMERION UI</title><style>body{background:#111;color:#ccc;font:14px Segoe UI;margin:16px}figure{display:inline-block;margin:8px;vertical-align:top}img{max-width:460px;border:1px solid #333}figcaption{margin-top:4px}</style>" +
    (($imgs | ForEach-Object { "<figure><a href='$($_.Name)'><img src='$($_.Name)' loading=lazy></a><figcaption>$($_.BaseName)</figcaption></figure>" }) -join "`n")
[IO.File]::WriteAllText((Join-Path $Out "index.html"), $html, [Text.UTF8Encoding]::new($false))

if (-not $KeepOpen) { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(3000)) { $proc | Stop-Process -Force } }
Write-Host ("Готово: {0} снимков за {1:0.0} с -> {2}" -f $imgs.Count, $sw.Elapsed.TotalSeconds, $Out) -ForegroundColor Green
