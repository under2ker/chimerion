<#
  ОСНОВНОЙ ИНСТРУМЕНТ ВИЗУАЛЬНЫХ ТЕСТОВ CHIMERION.
  Запускает отладочную сборку на отдельных копиях данных, проходит все страницы, вкладки, диалоги и прокрутку
  в темной и светлой теме, проверяет, что каждый шаг открыл нужное, снимает окна и сравнивает с прошлым прогоном.

  .\tools\ui-shots.ps1                            # полный прогон: темная и светлая тема параллельно, сравнение с прошлым
  .\tools\ui-shots.ps1 -Themes dark               # только темная тема
  .\tools\ui-shots.ps1 -Langs ru,en               # еще и английский интерфейс (длинные строки, переносы)
  .\tools\ui-shots.ps1 -Only settings,journal     # только эти шаги (по началу имени)
  .\tools\ui-shots.ps1 -Sizes 1440x1300,1000x760  # в заданных размерах окна (по умолчанию обычный размер)
  .\tools\ui-shots.ps1 -Baseline E:\...\прогон    # сравнить с конкретным прогоном; -NoCompare без сравнения
  .\tools\ui-shots.ps1 -Sequential                # проходы по очереди (если машина слабая)
  .\tools\ui-shots.ps1 -Trace                     # время каждого нажатия и снимка

  Итог: папка C:\CHIMERION\ui-shots\<дата> с PNG, index.html (все снимки, изменившиеся отмечены, рядом карта
  отличий) и report.txt. Код выхода 1, если какой-то шаг не открылся или не нашел своей страницы.

  Скорость:
  - фиксированных пауз нет: ждем только то, что реально грузится (элемент появился, кадр сменился и перестал
    меняться, окно открылось или закрылось, кнопка стала доступна); анимации программы выключены (DNEX_NO_ANIMATION);
  - копии данных для всех проходов делаются параллельно (robocopy /MT) одновременно с проверкой сборки;
  - все программы запускаются сразу и грузятся, пока готовится помощник (C#, кэшируется в DLL по хэшу исходника);
  - каждая тема и язык снимаются своим процессом параллельно: у тестовой копии свой ключ «единственной копии»;
  - проверка страницы идет в фоне параллельно со снимком, PNG пишутся в фоне, сравнение многопоточное.

  Безопасность:
  - каждый проход работает на свежей копии данных в %TEMP% с DNEX_NO_AUTORESTORE=1: программа не меняет DNS
    компьютера, не поднимает прокси, ничего не отправляет в общую карту и счетчик, ID устройства подменен.
    Поэтому настоящая CHIMERION может оставаться запущенной;
  - нажимаются только кнопки из $Plan; переключатели, ссылки, «Подключить», «Сохранить», «Отправить», покупки
    и все, что открывает браузер или Telegram, запрещены и дополнительно проверяются перед нажатием;
  - мышь и клавиатура не используются: нажатия через UI Automation, снимки через PrintWindow,
    поэтому другие окна на экране не мешают и не попадают в кадр;
  - диалоги закрываются как крестиком (WM_CLOSE), то есть без сохранения.

  Не снимается (нужны особые условия): «Выход» (закрытие программы), «Обновление» (нужна новая версия),
  «Сайты для обхода» (нужен установленный zapret), проверка браузера (открывает браузер), меню в трее.
#>
param(
    [string]$Out,
    [string]$DataDir = "C:\CHIMERION\tiktok-01\data",
    [string[]]$Themes = @("dark", "light"),
    [string[]]$Langs = @("ru"),
    [string[]]$Sizes = @(),          # пусто: окно в том размере, в каком программа открылась сама
    [string[]]$Only,
    [string]$Baseline,               # пусто: последний прошлый прогон
    [int]$MaxScroll = 5,             # сколько экранов прокрутки снимать на страницу
    [double]$MinDiff = 0.5,          # отличия меньше, % кадра: живые данные (пинги, время), в «мелочи», а не в «изменилось»
    [switch]$NoCompare,
    [switch]$NoBuild,
    [switch]$NoScroll,
    [switch]$Sequential,
    [switch]$Animations,             # оставить анимации программы
    [switch]$Trace,
    # Внутреннее: исполнитель одного прохода (его запускает основной процесс)
    [string]$Worker, [int]$AppPid, [string]$Dll
)
$ErrorActionPreference = "Stop"
$sw = [Diagnostics.Stopwatch]::StartNew()
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo "bin\Debug\net8.0-windows\CHIMERION.exe"
$shotsRoot = "C:\CHIMERION\ui-shots"
$run = Join-Path $env:TEMP "chimerion-ui-shots"
if (-not $Out) { $Out = Join-Path $shotsRoot (Get-Date -Format "yyyy-MM-dd_HH-mm-ss") }
function Split-List($v) { @($v | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }   # -File передает «a,b» одной строкой
$Themes = Split-List $Themes; $Langs = Split-List $Langs; $Sizes = Split-List $Sizes; $Only = Split-List $Only
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

# ---------- Что нажимать ----------
# n: имя шага; nav: страница (пункт меню), открывается, только если сейчас открыта другая; c: что нажать на странице
# (имя элемента, * — шаблон); expect: что должно быть на странице; tabs: вкладки; dialog: шаг открывает окно;
# sub: что нажать внутри окна (вложенное окно); back: чем вернуться; stable: ожидание стабильного кадра, мс;
# wait: сколько ждать окно, мс (перед формой обращения идет диагностика до 20 с)
# zoneSearch: проверка поиска поясов. collapse: свернуть меню на время шага.
$Plan = @(
    @{ n = "main";            nav = "Главная";            expect = "ИИ-сервисы" },
    @{ n = "servers";         nav = "Серверы";            expect = "Проверка подключения"; tabs = @("Рабочие", "Все", "Избранные"); stable = 500 },
    @{ n = "servers-graph";   nav = "Серверы";            c = @("График качества"); dialog = $true },
    @{ n = "addserver";                                   c = @("Добавить сервер"); dialog = $true },
    @{ n = "mode";            nav = "Режим";              expect = "Готовые режимы" },
    @{ n = "tools";           nav = "Telegram и YouTube"; expect = "Если не работает" },   # «Как это работает» по ТЗ спрятано в «?»
    @{ n = "flow";            nav = "Google Flow";        expect = "Как подключить"; tabs = @("Chrome", "Edge", "Яндекс Браузер") },
    @{ n = "diag";            nav = "Диагностика";        expect = "Проверить сайт" },
    @{ n = "diag-history";    nav = "Диагностика";        c = @("История сети"); dialog = $true },
    @{ n = "diag-tickets";    nav = "Диагностика";        c = @("Мои обращения*"); dialog = $true },
    @{ n = "speed";           nav = "Тест скорости";      expect = "Скорость DNS" },
    @{ n = "accounts";        nav = "Аккаунты";           expect = "Как беречь аккаунт" },
    @{ n = "accounts-zone";   nav = "Аккаунты";           c = @("Выбрать пояс"); expect = "Где изменить время"; tabs = @("В браузере", "Claude Code", "Приложения Windows", "Вся Windows"); zoneSearch = $true; back = "Закрыть выбор пояса" },
    @{ n = "accounts-zone-collapsed"; nav = "Аккаунты"; c = @("Выбрать пояс"); collapse = $true; expect = "Время в выбранном поясе"; back = "Закрыть выбор пояса" },
    @{ n = "journal";         nav = "Журнал";             expect = "Сохранить в файл"; tabs = @("Все", "Успех*", "Предупреждения*", "Ошибки*") },   # к названию добавляется число записей
    @{ n = "settings";        nav = "Настройки";          expect = "Отмена"; tabs = @("Основные", "Приватность", "Проверка серверов", "Сторож и трей", "Два DNS", "Внешний вид", "Данные", "О программе") },
    @{ n = "settings-twodns"; nav = "Настройки";          c = @("Два DNS", "Настроить два DNS"); dialog = $true },
    @{ n = "dns";             nav = "CHIMERION DNS";      expect = "Сроки и цены" },
    @{ n = "help";            nav = "Помощь";             expect = "Что делать, если*"; tabs = @("Не открывается", "Под наблюдением", "На телефоне") },
    @{ n = "report";                                      c = @("Сообщить о сбое"); dialog = $true; sub = @("Посмотреть"); wait = 25000 },
    @{ n = "compact";                                     c = @("Компактно"); expect = "Полный вид"; back = "Полный вид" }
)
# Никогда не нажимать, даже если имя совпало случайно. Ссылки (Hyperlink) и галочки не нажимаются никогда
$Deny = '^(Защита|Включено|Выключено|Подключить.*|Отключить|Сохранить|Отправить.*|Очистить|Купить.*|Написать.*|Подробнее.*|Найти новые серверы|Подобрать .*|Запустить тест|Перепроверить|Проверить .*|Включить.*|Убрать|Открыть.*|Экспорт.*|Импорт.*|Вернуть .*|Рекомендуемые значения|English|Русский|Светлая|Темная|Текст лицензии|История изменений|Баг-репорты|Идеи|Скопировать.*|Применить в Windows|Изменить пояс|Сохранить в файл|Папка журналов|Отчет в один клик|Канал в Telegram|Сайт chimerion\.dev|Версия.*|Новое обращение)$'

# ================= Исполнитель одного прохода =================
function Run-Pass([string]$pass, [int]$appId) {
    $script:pass = $pass
    $script:wantLight = $pass -like "light-*"
    $script:englishNames = if ($pass -like "*-en") {
        Add-Type -AssemblyName System.Web.Extensions
        [System.Web.Script.Serialization.JavaScriptSerializer]::new().DeserializeObject([IO.File]::ReadAllText((Join-Path $repo "i18n/en.json")))
    } else { @{} }
    $script:fails = New-Object System.Collections.Generic.List[string]
    $script:count = 0; $script:last = 0L; $script:page = $null
    $script:pid_ = $appId
    $script:main = [UiShot]::WaitMain($appId, 15000)
    if ($script:main -eq [IntPtr]::Zero) { Fail "окно программы не появилось"; return }
    [UiShot]::ShowWindow($script:main, 5) | Out-Null   # показать как есть: размер и развернутость не меняем
    $script:root = [Windows.Automation.AutomationElement]::FromHandle($script:main)
    $steps = if ($Only) { $Plan | Where-Object { $n = $_.n; [bool]($Only | Where-Object { $n -like "$_*" }) } } else { $Plan }

    foreach ($size in $(if ($Sizes) { $Sizes } else { @("") })) {
        if ($size) {
            $w, $h = $size -split "x" | ForEach-Object { [int]$_ }
            [UiShot]::SetWindowPos($script:main, [IntPtr]::Zero, 0, 0, $w, $h, 0x14) | Out-Null
            $script:last = 0L
        }
        $r = New-Object UiShot+RECT; [UiShot]::GetWindowRect($script:main, [ref]$r) | Out-Null
        $tag = "{0}x{1}" -f ($r.R - $r.L), ($r.B - $r.T)
        Write-Host "[$pass] размер окна $tag" -ForegroundColor Cyan
        foreach ($s in $steps) {
            $t0 = $sw.Elapsed.TotalSeconds
            $ok = $true
            if ($s.nav -and $s.nav -ne $script:page) { $ok = Press $s.nav; $script:page = $s.nav }
            $menuCollapsed = $false
            if ($ok -and $s.collapse) { $menuCollapsed = Toggle-Menu; $ok = $menuCollapsed }
            foreach ($c in $s.c) { if ($ok) { $ok = Press $c } }
            if ($ok) {
                $stable = if ($s.stable) { $s.stable } else { 1200 }
                $label = "$tag-$($s.n)"
                if ($s.dialog) { Shoot-Dialogs $label $s.sub $(if ($s.wait) { $s.wait } else { 4000 }) }
                elseif ($s.tabs) {
                    if ($s.zoneSearch -and -not [UiShot]::ExpectAsync($script:main, (UiName "Claude, ChatGPT и Gemini"), 4000).GetAwaiter().GetResult()) {
                        Fail "Часовой пояс: в режиме браузера не перечислены сервисы"
                    }
                    foreach ($t in $s.tabs) {
                        if (-not (Press $t)) { continue }
                        $expect = $s.expect
                        if ($s.zoneSearch) {
                            $expect = switch ($t) {
                                "Claude Code" { "Вставьте команду в PowerShell. Она передаст выбранный пояс новому сеансу." }
                                "В браузере" { "Откройте нужную вкладку в Chrome или Edge. Нажмите F12, затем Ctrl+Shift+P и выберите «Show Protocol monitor»." }
                                "Приложения Windows" { "Выбрать системный пояс" }
                                "Вся Windows" { "Применить в Windows" }
                            }
                            if (-not [UiShot]::ExpectAsync($script:main, (UiName $expect), 4000).GetAwaiter().GetResult()) {
                                Fail "Часовой пояс: не открылся режим «$t»"
                                continue
                            }
                        }
                        Shoot "$label-$(($t -replace '[*\\/:?"<>| ]', '').ToLower())" $expect $stable
                    }
                } elseif ($s.collapse -and -not [UiShot]::ExpectAsync($script:main, (UiName $s.expect), 4000).GetAwaiter().GetResult()) {
                    Fail "Часовой пояс не открылся при свернутом меню"
                } else { Shoot $label $s.expect $stable }
                if ($s.zoneSearch) { Test-TimeZoneSearch $label $stable }
                if ($s.back) { Press $s.back | Out-Null; $script:last = 0L; $script:page = $null }
            }
            if ($menuCollapsed) { Toggle-Menu | Out-Null }
            if (-not $ok) { $script:page = $null }   # что-то пошло не так: следующий шаг откроет свою страницу заново
            Write-Host ("[{0}] {1,-16} {2,5:0.0} с  (+{3:0.0})" -f $pass, $s.n, $sw.Elapsed.TotalSeconds, ($sw.Elapsed.TotalSeconds - $t0))
        }
    }
    [UiShot]::Flush()
}

function Fail([string]$msg) { $script:fails.Add("$($script:pass): $msg"); Write-Host "[$($script:pass)] ОШИБКА: $msg" -ForegroundColor Red }
function T([string]$what, [double]$from) { if ($Trace) { Write-Host ("[{0}]   {1,6:0} мс  {2}" -f $script:pass, (($sw.Elapsed.TotalMilliseconds) - $from), $what) -ForegroundColor DarkGray } }

$AE = [Windows.Automation.AutomationElement]
$walker = [Windows.Automation.TreeWalker]::ControlViewWalker

# Сам элемент или ближайший предок с действием (нажать, выбрать, переключить); $null, если такого нет
function Get-Actionable($el) {
    $pat = $null
    while ($el) {
        if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat) -or
            $el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat) -or
            $el.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { return @($el, $pat) }
        $el = $walker.GetParent($el)
    }
    return $null
}

# Поиск ждет, пока элемент появится (страница еще строится), до maxMs
function Find-El($scope, [string]$name, [int]$maxMs = 4000) {
    $t = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            if ($name.Contains("*")) {
                $cr = New-Object Windows.Automation.CacheRequest; $cr.Add($AE::NameProperty)
                $cr.Push()
                try { $all = $scope.FindAll("Descendants", [Windows.Automation.Condition]::TrueCondition) } finally { $cr.Pop() }
                foreach ($e in $all) { if ($e.Cached.Name -like $name) { return $e } }
            } else {
                # Обычно хватает первого совпадения (надпись внутри кнопки, по ней поднимаемся к кнопке). Если у него
                # нет действия (такая же надпись в другом месте раньше кнопки), берем совпадение, у которого действие есть
                $byName = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, $name)
                $el = $scope.FindFirst("Descendants", $byName)
                if ($el -and (Get-Actionable $el)) { return $el }
                foreach ($e in $scope.FindAll("Descendants", $byName)) { if (Get-Actionable $e) { return $e } }
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 30
    } while ($t.ElapsedMilliseconds -lt $maxMs)
    return $null
}

function UiName([string]$name) {
    if ($script:englishNames.ContainsKey($name)) { return $script:englishNames[$name] }
    return $name
}

function Press([string]$name, $scope = $script:root) {
    if ($name -match $Deny) { Fail "«$name» в запрете, не нажато"; return $false }
    $name = UiName $name
    $t0 = $sw.Elapsed.TotalMilliseconds
    try {
        # Страница могла перестроиться между поиском и нажатием: следующая попытка ищет элемент заново
        for ($try = 0; $try -lt 3; $try++) {
            $r = Press-Once $name $scope
            if (-not ($r -is [string])) { return $r }
            Start-Sleep -Milliseconds 80
        }
        Fail "«$name» не нажалось: элемент пропадал при каждой попытке"
        return $false
    } finally { T "нажать $name" $t0 }
}

function Press-Once([string]$name, $scope) {
    $el = Find-El $scope $name
    if (-not $el) { Fail "нет элемента «$name»"; return $false }
    try {
        $act = Get-Actionable $el
        if (-not $act) { Fail "«$name» не нажимается (нет действия для UI Automation)"; return $false }
        $el, $pat = $act
        $real = $el.Current.Name
        $ct = $el.Current.ControlType
        if ($ct -eq [Windows.Automation.ControlType]::CheckBox -or $ct -eq [Windows.Automation.ControlType]::Hyperlink -or $real -match $Deny) {
            Fail "«$real» в запрете, не нажато"; return $false
        }
        # Кнопка бывает недоступна, пока идет проверка (диагностика до ~15 с): тут ждем
        for ($i = 0; $i -lt 400 -and -not $el.Current.IsEnabled; $i++) { Start-Sleep -Milliseconds 50 }
        if ($pat -is [Windows.Automation.InvokePattern]) { $pat.Invoke() }
        elseif ($pat -is [Windows.Automation.SelectionItemPattern]) { $pat.Select() }
        else { $pat.Toggle() }
    } catch [System.Windows.Automation.ElementNotAvailableException] { return "retry" }
    catch { Fail "«$name» не нажалось: $($_.Exception.Message)"; return $false }
    return $true
}

function File([string]$label, [string]$suffix = "") { Join-Path $Out ("{0}-{1:000}-{2}{3}.png" -f $script:pass, $script:count, $label, $suffix) }

# Снимок страницы; одновременно в фоне проверяется, что на ней есть expect
function Shoot([string]$label, [string]$expect, [int]$stable = 1200) {
    $script:count++
    $t0 = $sw.Elapsed.TotalMilliseconds
    # В фоне, пока снимается кадр: проверка страницы и поиск области прокрутки
    $check = if ($expect) { [UiShot]::ExpectAsync($script:main, (UiName $expect), 4000) }
    $scroll = if (-not $NoScroll) { [UiShot]::FindScrollAsync($script:main) }
    $script:last = [UiShot]::Save($script:main, (File $label), $script:last, 400, $stable)
    T "снимок $label" $t0
    if ($check -and -not $check.Result) { Fail "$label`: на странице нет «$expect»" }
    if ((([UiShot]::LastLuma) -gt 110) -ne $script:wantLight) { Fail "$label`: тема не та (средняя яркость $([int][UiShot]::LastLuma))" }
    $t0 = $sw.Elapsed.TotalMilliseconds
    if ($scroll -and $scroll.Result) { Shoot-Scroll $label $stable $scroll.Result; T "прокрутка" $t0 }
}

function Toggle-Menu {
    try {
        $id = New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "PanelCollapseButton")
        $button = $script:root.FindFirst("Descendants", $id)
        $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    } catch {
        Fail "Не удалось переключить меню: $($_.Exception.Message)"
        return $false
    }
}

function Wait-TimeZoneElement($condition, [string]$label) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $el = $script:root.FindFirst("Descendants", $condition)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 30
    } while ($timer.ElapsedMilliseconds -lt 4000)
    throw "нет элемента «$label»"
}

function Test-TimeZoneSearch([string]$label, [int]$stable) {
    if (-not (Press "Приложения Windows") -or -not (Press "Выбрать системный пояс")) { return }
    if (-not [UiShot]::ExpectAsync($script:main, (UiName "Применить в Windows"), 4000).GetAwaiter().GetResult()) {
        Fail "Часовой пояс: переход из режима приложений не открыл настройки Windows"
    }
    if (-not (Press "В браузере")) { return }
    try {
        $name = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, (UiName "Поиск часового пояса"))
        $search = Wait-TimeZoneElement $name "Поиск часового пояса"
        $inputBox = $search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
        $inputBox.SetValue("NoSuchTimeZone")
        $empty = UiName "Пояса не найдены. Попробуйте другой город, страну или смещение UTC."
        $copy = New-Object Windows.Automation.PropertyCondition($AE::NameProperty, (UiName "Скопировать команду"))
        $copyButton = Wait-TimeZoneElement $copy "Скопировать команду"
        if (-not [UiShot]::ExpectAsync($script:main, $empty, 4000).GetAwaiter().GetResult() -or $copyButton.Current.IsEnabled) {
            Fail "Часовой пояс: пустой поиск должен показывать подсказку и отключать копирование"
        }
        $inputBox.SetValue($(if ($script:pass -like "*-en") { "Germany" } else { "Германия" }))
        $berlin = New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "Europe/Berlin")
        $item = Wait-TimeZoneElement $berlin "Europe/Berlin"
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $labelBerlin = if ($script:pass -like "*-en") { "Berlin, Germany" } else { "Берлин, Германия" }
        if (-not [UiShot]::ExpectAsync($script:main, "11:30", 4000).GetAwaiter().GetResult() -or $item.Current.Name -ne $labelBerlin) {
            Fail "Часовой пояс: страна должна находить Берлин и показывать его местное время"
        }
        Shoot "$label-country-search" "11:30" $stable
        $inputBox.SetValue("UTC+9 Tokyo")
        $tokyo = New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "Asia/Tokyo")
        $item = Wait-TimeZoneElement $tokyo "Asia/Tokyo"
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Shoot "$label-search" "19:30" $stable
        if (-not (Press "Очистить поиск")) { return }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($inputBox.Current.Value -ne "" -and $timer.ElapsedMilliseconds -lt 4000) { Start-Sleep -Milliseconds 30 }
        if ($inputBox.Current.Value -ne "" -or -not [UiShot]::ExpectAsync($script:main, "19:30", 4000).GetAwaiter().GetResult()) {
            Fail "Часовой пояс: после очистки поиска выбранный пояс должен остаться"
        }
        $countryId = New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "time-zone-country")
        $country = Wait-TimeZoneElement $countryId "Страна"
        $dropdown = $country.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $dropdown.Expand()
        $countries = $country.GetCurrentPattern([Windows.Automation.ItemContainerPattern]::Pattern)
        $timer.Restart()
        do {
            $netherlands = $countries.FindItemByProperty($null, $AE::AutomationIdProperty, "NL")
            if ($netherlands) { break }
            Start-Sleep -Milliseconds 30
        } while ($timer.ElapsedMilliseconds -lt 4000)
        if (-not $netherlands) { throw "в списке стран нет Нидерландов" }
        $netherlands.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if ($dropdown.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded) { $dropdown.Collapse() }

        $amsterdamId = New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "Europe/Amsterdam")
        $amsterdam = Wait-TimeZoneElement $amsterdamId "Europe/Amsterdam"
        $amsterdam.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if ($script:root.FindFirst("Descendants", $berlin)) {
            Fail "Часовой пояс: фильтр Нидерландов показал город из другой страны"
        }
        Shoot "$label-netherlands" "11:30" $stable
    } catch {
        Fail "Часовой пояс: поиск не прошел: $($_.Exception.Message)"
    }
}

# Окна, открытые шагом: ждем появления, снимаем, жмем sub внутри (вложенные окна), закрываем без сохранения
function Shoot-Dialogs([string]$label, [string[]]$sub, [int]$wait = 4000) {
    $script:count++
    $known = [IntPtr[]]@($script:main)
    $extra = [IntPtr[]]@([UiShot]::WaitOthers($script:pid_, $known, $true, $wait))
    if (-not $extra.Count) { Fail "$label`: окно не открылось"; return }
    $both = [IntPtr[]]($known + $extra)
    $k = 0
    foreach ($h in $extra) {
        $k++
        [UiShot]::Save($h, (File $label "-dialog$k"), 0, 0, 1200) | Out-Null
        foreach ($s in $sub) {
            if (Press $s ($AE::FromHandle($h))) {
                $inner = @([UiShot]::WaitOthers($script:pid_, $both, $true, 4000))
                if (-not $inner.Count) { Fail "$label`: «$s» не открыло окно" }
                foreach ($i in $inner) {
                    [UiShot]::Save($i, (File $label "-dialog$k-$(($s -replace '\W', '').ToLower())"), 0, 0, 1200) | Out-Null
                    [UiShot]::PostMessage($i, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                }
                [UiShot]::WaitOthers($script:pid_, $both, $false, 3000) | Out-Null
            }
        }
    }
    foreach ($h in $extra) { [UiShot]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
    if ([UiShot]::WaitOthers($script:pid_, $known, $false, 3000).Count) { Fail "$label`: окно не закрылось" }
}

# Длинные страницы: найденная в фоне область листается экран за экраном
function Shoot-Scroll([string]$label, [int]$stable, $el) {
    try {
        $sp = $el.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
        $view = [Math]::Max(10, $sp.Current.VerticalViewSize)
        $part = 1
        for ($p = $view * 0.9; $part -lt $MaxScroll; $p += $view * 0.9) {
            $sp.SetScrollPercent(-1, [Math]::Min(100, $p))
            $part++
            $script:last = [UiShot]::Save($script:main, (File $label "-scroll$part"), $script:last, 250, [Math]::Min(600, $stable))
            if ($p -ge 100) { break }
        }
        $sp.SetScrollPercent(-1, 0)
    } catch [System.Windows.Automation.ElementNotAvailableException] { }
}

if ($Worker) {
    Add-Type -Path $Dll
    Run-Pass $Worker $AppPid
    try { Stop-Process -Id $AppPid -Force } catch {}
    [IO.File]::WriteAllLines((Join-Path $Out "fails-$Worker.txt"), [string[]]$script:fails, [Text.UTF8Encoding]::new($false))
    exit 0
}

# ================= Основной процесс =================
$passes = @(foreach ($th in $Themes) { foreach ($lg in $Langs) { "$th-$lg" } })
# Остатки прошлых прогонов отладочной сборки (настоящая CHIMERION установлена в другой папке и не трогается)
Get-Process CHIMERION -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
New-Item -ItemType Directory -Force $Out, $run | Out-Null

# Копии данных для всех проходов параллельно, пока проверяется сборка
$copies = @{}
$robo = foreach ($p in $passes) {
    $dst = Join-Path $run "data-$p"; $copies[$p] = $dst
    if (Test-Path $DataDir) { Start-Process robocopy -ArgumentList "`"$DataDir`" `"$dst`" /MIR /MT:16 /R:0 /W:0 /NFL /NDL /NJH /NJS /NP" -NoNewWindow -PassThru }
    else { New-Item -ItemType Directory -Force $dst | Out-Null }
}

if (-not $NoBuild) {
    $stale = -not (Test-Path $exe)
    if (-not $stale) {
        # Только исходники программы: bin, obj, tests, tools, backend и служебные папки не обходим
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
foreach ($r in $robo) { if ($r) { $r.WaitForExit() } }
Write-Host ("Сборка и копии данных: {0:0.0} с" -f $sw.Elapsed.TotalSeconds)

# Копия: тема и язык прохода, без проверки обновлений и общей карты, обычный (не компактный) вид
function Set-CopySettings([string]$dir, [string]$theme, [string]$lang) {
    $p = Join-Path $dir "settings.json"
    if (-not (Test-Path $p)) { return }
    $s = [IO.File]::ReadAllText($p)
    $set = @{ theme = "`"$theme`""; language = "`"$lang`""; checkUpdates = "false"; crowdConsent = "false"; crowdSites = "false"; startCompact = "false" }
    foreach ($k in $set.Keys) { $s = [regex]::Replace($s, "(`"$k`"\s*:\s*)(`"[^`"]*`"|true|false|null)", "`${1}$($set[$k])") }
    [IO.File]::WriteAllText($p, $s, [Text.UTF8Encoding]::new($false))
}

# Все программы стартуют сразу: каждая тестовая копия со своим ключом экземпляра
$apps = @{}
foreach ($p in $passes) {
    $th, $lg = $p -split "-"
    Set-CopySettings $copies[$p] $th $lg
    $env:DNEX_DATA_DIR = $copies[$p]; $env:DNEX_NO_AUTORESTORE = "1"
    if (-not $Animations) { $env:DNEX_NO_ANIMATION = "1" }
    $apps[$p] = (Start-Process $exe -PassThru).Id
    if ($Sequential) { break }   # по очереди: следующая программа стартует после прохода предыдущей
}
Remove-Item Env:DNEX_DATA_DIR, Env:DNEX_NO_AUTORESTORE, Env:DNEX_NO_ANIMATION -ErrorAction SilentlyContinue

# Помощник: компилируется, только если изменился tools/ui-shots.cs
$src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "ui-shots.cs"))
$hash = [BitConverter]::ToString([Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($src))).Replace("-", "").Substring(0, 12)
$Dll = Join-Path $run "UiShot-$hash.dll"
if (-not (Test-Path $Dll)) { Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing, System.Core, UIAutomationClient, UIAutomationTypes, WindowsBase -OutputAssembly $Dll -OutputType Library }
Add-Type -Path $Dll
Write-Host ("Программы запущены, помощник готов: {0:0.0} с" -f $sw.Elapsed.TotalSeconds)

# Программы закрываются при любом исходе: ошибка, Ctrl+C, аварийный исполнитель
$workers = @()
try {
    $script:allFails = New-Object System.Collections.Generic.List[string]
    $common = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Out", "`"$Out`"", "-Dll", "`"$Dll`"", "-MaxScroll", $MaxScroll)
    if ($Only) { $common += @("-Only", ($Only -join ",")) }
    if ($Sizes) { $common += @("-Sizes", ($Sizes -join ",")) }
    if ($NoScroll) { $common += "-NoScroll" }
    if ($Trace) { $common += "-Trace" }
    if ($passes.Count -gt 1 -and -not $Sequential) {
        # Каждый проход своим процессом, параллельно
        $workers = foreach ($p in $passes) { Start-Process powershell -ArgumentList ($common + @("-Worker", $p, "-AppPid", $apps[$p])) -NoNewWindow -PassThru }
        foreach ($w in $workers) { $w.WaitForExit() }
        foreach ($p in $passes) {
            $f = Join-Path $Out "fails-$p.txt"
            if (Test-Path $f) { $script:allFails.AddRange([string[]]@(Get-Content $f -Encoding UTF8 | Where-Object { $_ })); Remove-Item $f }
            else { $script:allFails.Add("${p}: исполнитель прохода завершился аварийно") }
        }
    } else {
        foreach ($p in $passes) {
            if (-not $apps[$p]) {
                $th, $lg = $p -split "-"
                Set-CopySettings $copies[$p] $th $lg
                $env:DNEX_DATA_DIR = $copies[$p]; $env:DNEX_NO_AUTORESTORE = "1"; if (-not $Animations) { $env:DNEX_NO_ANIMATION = "1" }
                $apps[$p] = (Start-Process $exe -PassThru).Id
                Remove-Item Env:DNEX_DATA_DIR, Env:DNEX_NO_AUTORESTORE, Env:DNEX_NO_ANIMATION -ErrorAction SilentlyContinue
            }
            Run-Pass $p $apps[$p]
            try { Stop-Process -Id $apps[$p] -Force } catch {}
            $script:allFails.AddRange([string[]]@($script:fails))
        }
    }
} finally {
    foreach ($w in $workers) { if ($w -and -not $w.HasExited) { try { $w.Kill() } catch {} } }
    foreach ($id in @($apps.Values)) { try { Stop-Process -Id $id -Force -ErrorAction Stop } catch {} }
}

# ---------- Сравнение с прошлым прогоном ----------
$cmp = @{}
if (-not $NoCompare) {
    if (-not $Baseline) {
        $Baseline = Get-ChildItem $shotsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ne (Resolve-Path $Out).Path -and (Test-Path (Join-Path $_.FullName "report.txt")) } |
            Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
    }
    if ($Baseline -and (Test-Path $Baseline)) {
        foreach ($line in [UiShot]::Compare($Out, $Baseline, (Join-Path $Out "diff"))) { $p = $line -split '\|'; $cmp[$p[0]] = $p }
        Write-Host ("Сравнение с {0}: {1:0.0} с" -f $Baseline, $sw.Elapsed.TotalSeconds)
    }
}

# ---------- Итог: report.txt и index.html ----------
$imgs = Get-ChildItem $Out -Filter *.png | Sort-Object Name
$inv = [Globalization.CultureInfo]::InvariantCulture
function Pct($c) { if ($c[1] -eq "-") { -1.0 } else { [double]::Parse($c[1], $inv) } }
$changed = @($imgs | Where-Object { $cmp[$_.Name] -and (Pct $cmp[$_.Name]) -ge $MinDiff } | Sort-Object { -(Pct $cmp[$_.Name]) })
$minor = @($imgs | Where-Object { $cmp[$_.Name] -and (Pct $cmp[$_.Name]) -gt 0 -and (Pct $cmp[$_.Name]) -lt $MinDiff })
$new = @($imgs | Where-Object { $cmp[$_.Name] -and $cmp[$_.Name][1] -eq "-" })
$lines = @("Прогон $Out", "Проходы: $($passes -join ', ')", "Снимков: $($imgs.Count), время $([Math]::Round($sw.Elapsed.TotalSeconds, 1)) с", "Ошибок: $($script:allFails.Count)") + @($script:allFails | ForEach-Object { "  $_" })
if ($cmp.Count) {
    $lines += "Сравнение с $Baseline`: изменилось $($changed.Count) (от $MinDiff%), мелочей $($minor.Count), новых $($new.Count)"
    $lines += @($changed + $new | ForEach-Object { "  $($_.Name): $($cmp[$_.Name][1])% $($cmp[$_.Name][2])" })
    if ($minor.Count) { $lines += "Мелочи (меньше $MinDiff%, обычно живые данные):"; $lines += @($minor | ForEach-Object { "  $($_.Name): $($cmp[$_.Name][1])%" }) }
}
[IO.File]::WriteAllLines((Join-Path $Out "report.txt"), [string[]]$lines, [Text.UTF8Encoding]::new($false))

$enc = { param($s) [Net.WebUtility]::HtmlEncode($s) }
$figs = foreach ($f in $imgs) {
    $c = $cmp[$f.Name]; $note = ""; $cls = ""
    if ($c) {
        if ($c[2] -like "diff/*") { if ((Pct $c) -ge $MinDiff) { $cls = " class=changed" }; $note = " · <a href='$($c[2])'>отличия $($c[1])%</a>" }
        elseif ($c[2] -and $c[1] -ne "-") { $cls = " class=changed"; $note = " · $(& $enc $c[2])" }
        elseif ($c[1] -eq "-") { $note = " · новый" }
    }
    "<figure$cls><a href='$($f.Name)'><img src='$($f.Name)' loading=lazy></a><figcaption>$(& $enc $f.BaseName)$note</figcaption></figure>"
}
$head = "<h1>CHIMERION, снимки интерфейса</h1><p>$($imgs.Count) снимков · проходы $($passes -join ', ') · $([Math]::Round($sw.Elapsed.TotalSeconds,1)) с · ошибок $($script:allFails.Count)" +
    $(if ($cmp.Count) { " · изменилось $($changed.Count), мелочей $($minor.Count), новых $($new.Count) по сравнению с $(& $enc (Split-Path $Baseline -Leaf))" } else { "" }) + "</p>" +
    $(if ($script:allFails.Count) { "<ul class=err>" + (($script:allFails | ForEach-Object { "<li>$(& $enc $_)</li>" }) -join "") + "</ul>" } else { "" })
$html = "<!doctype html><meta charset=utf-8><title>CHIMERION UI</title><style>body{background:#111;color:#ccc;font:14px Segoe UI;margin:16px}a{color:#7ab}h1{font-size:18px}.err{color:#f77}figure{display:inline-block;margin:8px;vertical-align:top;padding:4px}figure.changed{outline:2px solid #e85}img{max-width:460px;border:1px solid #333}figcaption{margin-top:4px;max-width:460px}</style>" +
    $head + ($figs -join "`n")
[IO.File]::WriteAllText((Join-Path $Out "index.html"), $html, [Text.UTF8Encoding]::new($false))

$color = if ($script:allFails.Count) { "Red" } else { "Green" }
Write-Host ("Готово: {0} снимков, ошибок {1}{2}, {3:0.0} с -> {4}" -f $imgs.Count, $script:allFails.Count, $(if ($cmp.Count) { ", изменилось $($changed.Count), мелочей $($minor.Count), новых $($new.Count)" } else { "" }), $sw.Elapsed.TotalSeconds, $Out) -ForegroundColor $color
if ($script:allFails.Count) { exit 1 }
