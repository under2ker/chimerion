using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Окно: раскладка и размеры, компактный вид, тема и язык, уведомления, строка работы и счетчики</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _working;                  // идет долгая операция (проверка, подключение, парсинг)
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private readonly Queue<(string Text, string Key)> _toastQueue = new();
    private string _toastKey = "Ok";
    private DateTime _toastSince;

    // Темный заголовок окна Windows 10/11 под цвет программы
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.DarkTitle.Apply(this);
        // Своя рамка (WindowStyle=None): без этого развернутое окно занимает весь экран вместе с местом под панелью
        // задач, и нижняя строка (добавление сервера, журнал, обновление) уходит под панель
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(MaxSizeHook);
        FitToScreen();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>WM_GETMINMAXINFO: при разворачивании окно занимает рабочую область своего монитора (без панели задач).</summary>
    private IntPtr MaxSizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        // Минимальный размер окна из MinWidth/MinHeight, в пикселях экрана
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.MinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
        mmi.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private void UpdateStats()
    {
        _statsTimer.Stop();
        if (_view == null) return;
        int shown = _view.Cast<object>().Count();
        StatsText.Text = $"{_servers.Count(s => s.Status == ServerStatus.Ok)} работают   ·   " +
                         $"{_servers.Count(s => s.Status == ServerStatus.Partial)} частично   ·   " +
                         $"показано {shown} из {_servers.Count}";
        UpdateEmptyState(shown);
        UpdateTopList();
    }

    /// <summary>
    /// Пустая таблица: объяснение, почему строк нет, и кнопки, которые это исправят (сбросить поиск, показать все,
    /// найти серверы). Раньше при пустом фильтре была видна только шапка таблицы.
    /// </summary>
    private void UpdateEmptyState(int shown)
    {
        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown > 0) return;
        // В низкой таблице только заголовок и кнопки: иначе иконка и пояснение обрезаются
        var h = ServerGrid.ActualHeight;
        EmptyIcon.Visibility = h > 0 && h < 300 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = h > 0 && h < 220 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Margin = new Thickness(24, h > 0 && h < 300 ? 36 : 48, 24, 0);
        EmptyActions.Children.Clear();
        void Act(string text, string style, Action click)
        {
            var b = new Button { Content = text, Style = (Style)FindResource(style), Margin = new Thickness(4, 0, 4, 0) };
            b.Click += (_, _) => click();
            EmptyActions.Children.Add(b);
        }
        var search = SearchBox.Text.Trim();
        bool checking = _working || _servers.Any(s => s.Status == ServerStatus.Checking); // серверы в очереди еще без статуса «проверка»
        if (_servers.Count == 0)
        {
            (EmptyIcon.Kind, EmptyTitle.Text, EmptyText.Text) = ("globe", "Список серверов пуст", "CHIMERION найдет Smart DNS по источникам и в интернете и сразу их проверит");
            Act("Найти новые серверы", "Primary", () => Fetch_Click(this, new RoutedEventArgs()));
        }
        else if (search.Length > 0)
        {
            (EmptyIcon.Kind, EmptyTitle.Text, EmptyText.Text) = ("search", $"По запросу «{search}» ничего нет", "Поиск идет по имени, адресу и источнику");
            Act("Сбросить поиск", "Strong", () => SearchBox.Clear());
        }
        else if (FavTab.IsChecked == true)
        {
            (EmptyIcon.Kind, EmptyTitle.Text, EmptyText.Text) = ("star", "В избранном пусто", "Отметьте сервер звездочкой в начале строки, и он появится здесь");
            Act("Показать все", "Strong", () => AllTab.IsChecked = true);
        }
        else if (checking)
        {
            (EmptyIcon.Kind, EmptyTitle.Text, EmptyText.Text) = ("refresh-cw", "Идет проверка", "Рабочие серверы появятся здесь по мере проверки");
            Act("Показать все", "Ghost", () => AllTab.IsChecked = true);
        }
        else
        {
            (EmptyIcon.Kind, EmptyTitle.Text, EmptyText.Text) = ("triangle-alert", "Рабочих серверов нет", "Проверьте список заново или найдите новые серверы");
            Act("Показать все", "Ghost", () => AllTab.IsChecked = true);
            Act("Найти новые серверы", "Primary", () => Fetch_Click(this, new RoutedEventArgs()));
        }
    }


    private bool _compact;
    private Rect _fullBounds;

    /// <summary>Обрезает содержимое таблицы по скруглению карточки, чтобы прямые углы заголовков не выходили за рамку.</summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border card || card.Child is not DockPanel content) return;
        // Радиус карточки 12, рамка 1 px: внутри скругление 11
        var w = Math.Max(0, card.ActualWidth - 2);
        var h = Math.Max(0, card.ActualHeight - 2);
        content.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 11, 11);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Значок кнопки разворота: квадрат или два квадрата, когда окно развернуто.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaxIcon != null) MaxIcon.Kind = WindowState == WindowState.Maximized ? "copy" : "square";
    }

    private void Mode_Click(object sender, RoutedEventArgs e) => SetCompact(!_compact);

    private void SetCompact(bool compact, bool animate = true)
    {
        _compact = compact;
        var v = compact ? Visibility.Collapsed : Visibility.Visible;
        ToolbarCard.Visibility = v;
        BottomPanel.Visibility = v;
        StatsText.Visibility = v;
        SettingsButton.Visibility = v;
        DataButton.Visibility = v;
        Watchdog.Visibility = v;
        // Компактный вид: одна кнопка подключения, плашки сервисов, таблица только со статусом, именем и пингом
        var cv = compact ? Visibility.Visible : Visibility.Collapsed;
        // В компактном виде блок ИИ-сервисов остается: три плитки с логотипом, флагом доступа и статусом, без заголовка
        VerdictCard.Visibility = Visibility.Visible;
        VerdictCard.Padding = compact ? new Thickness(6, 6, 6, 6) : new Thickness(16, 12, 16, 12);
        VerdictHeader.Visibility = v;
        CompareGrid.Visibility = Visibility.Visible;
        CompareGrid.Margin = compact ? new Thickness(0) : new Thickness(0, 12, 0, 0); // в компактном виде без шапки блока: без пустого отступа
        _compareSig = null; // плитки пересобираются с отступами текущего вида
        SelectionBar.Visibility = v;
        ColFav.Visibility = v;  // узкое окно: звездочка через меню строки
        ApplyActions.Visibility = v;
        HeaderSep.Visibility = v;
        DnsLine.Visibility = v;
        CompactConnect.Visibility = cv;
        ConnText.MaxWidth = compact ? 200 : 300;
        ConnPill.Margin = new Thickness(0);
        RootGrid.Margin = compact ? new Thickness(12) : new Thickness(20, 16, 20, 16);
        ColStatus.Width = compact ? new DataGridLength(40) : new DataGridLength(128);
        ColStatus.Header = compact ? "" : "СТАТУС"; // в узкой колонке заголовок обрезается, точка и так понятна
        ColPing.Width = compact ? new DataGridLength(130) : new DataGridLength(140);
        ServerGrid.RowStyle = compact ? (Style)FindResource("RowCompact") : null;
        ServerGrid.Tag = compact ? "compact" : null; // по нему строки прячут текст статуса (см. XAML)
        ModeButtonText.Text = compact ? "Полный вид" : "Компактно";
        ((Controls.Icon)((StackPanel)ModeButton.Content).Children[0]).Kind = compact ? "expand" : "shrink";
        if (compact)
        {
            // В компактном виде — только рабочие серверы.
            _filterIndex = 5;
            _view?.Refresh();
            _fullBounds = new Rect(Left, Top, Width, Height);
            MinWidth = 360; MinHeight = 420;
            WindowState = WindowState.Normal;
            // Статистика — отдельной строкой под кнопкой подключения: в узкой шапке ей не хватает места
            Grid.SetRow(HeaderStats, 2); Grid.SetColumn(HeaderStats, 0); Grid.SetColumnSpan(HeaderStats, 3);
            HeaderStats.Margin = new Thickness(0, 10, 0, 0);
            HeaderStats.HorizontalAlignment = HorizontalAlignment.Center; // в компактном виде статистика по центру
            ResizeTo(485, 775, animate);
        }
        else
        {
            MinWidth = 1000; MinHeight = 760; // ниже проверка подключения забирала все место у таблицы
            Grid.SetRow(HeaderStats, 0); Grid.SetColumn(HeaderStats, 1); Grid.SetColumnSpan(HeaderStats, 1);
            HeaderStats.Margin = new Thickness(18, 0, 0, 0);
            HeaderStats.HorizontalAlignment = HorizontalAlignment.Left;
            if (_fullBounds.Width > 0) ResizeTo(_fullBounds.Width, _fullBounds.Height, animate);
        }
        ApplyResponsive();
        ShowCompare();
        if (compact) _ = RefreshTrayServicesAsync(); // компактный вид сразу проверяет сервисы
        try { Storage.Save("ui.json", new Dictionary<string, bool> { ["compact"] = compact }); } catch (Exception) { }
    }


    /// <summary>
    /// Ширина окна решает, что переносится и что скрывается. Приоритет: сначала уходят второстепенные колонки таблицы,
    /// потом шапка переносит статистику и кнопки действий на вторую строку. В компактном виде раскладку задает SetCompact.
    /// </summary>
    private void ApplyResponsive()
    {
        if (ApplyActions == null || HeaderGrid == null) return;
        var w = ActualWidth;
        bool full = !_compact;

        // Таблица: по мере сужения окна уходят источник, стабильность и оценка
        ColLocation.Visibility = ColServices.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        // Ширины колонок (см. XAML): без источника и без стабильности и оценки таблица нужна от ~1100 px,
        // с ними от ~1360 px, со всеми колонками от ~1540 px окна.
        ColSource.Visibility = full ? Visibility.Visible : Visibility.Collapsed; // узкая колонка с одной кнопкой: место есть всегда
        ColStab.Visibility = ColScore.Visibility = full && w >= 1360 ? Visibility.Visible : Visibility.Collapsed;

        // Рамка «Лучшие по стабильности» справа от проверки подключения: только в полном виде и в широком окне
        TopCard.Visibility = full && w >= 1300 ? Visibility.Visible : Visibility.Collapsed;

        // Версия внизу на узком окне теснит ссылки; она остается в заголовке окна
        HeaderVersion.Visibility = full && w < 1200 ? Visibility.Collapsed : Visibility.Visible;

        if (!full) return; // компактный вид шапку раскладывает сам

        // Шапка: ниже 1480 кнопки действий и статистика уходят на вторую строку.
        // На 1350 статистика в одной строке сжималась в столбик и ломала сетку, поэтому порог выше.
        bool narrow = w < 1480;
        _navPanel ??= ApplyActions.Parent as StackPanel;
        if (narrow && ApplyActions.Parent == _navPanel)
        {
            _navPanel?.Children.Remove(ApplyActions);
            HeaderGrid.Children.Add(ApplyActions);
            Grid.SetRow(ApplyActions, 1);
            Grid.SetColumn(ApplyActions, 2);
            ApplyActions.HorizontalAlignment = HorizontalAlignment.Right;
            ApplyActions.Margin = new Thickness(0, 10, 0, 0);
        }
        else if (!narrow && ApplyActions.Parent != _navPanel && _navPanel != null)
        {
            (ApplyActions.Parent as Panel)?.Children.Remove(ApplyActions);
            _navPanel.Children.Add(ApplyActions);
            ApplyActions.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApplyActions.Margin = new Thickness(0);
        }

        Grid.SetRow(HeaderStats, narrow ? 1 : 0);
        Grid.SetColumn(HeaderStats, narrow ? 0 : 1);
        HeaderStats.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(18, 0, 0, 0);
        HeaderSep.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    private StackPanel? _navPanel; // исходный контейнер кнопок действий в шапке

    /// <summary>Строки статистики в шапке справа от карточки подключения: пинг, стабильность, сервисы.</summary>
    private void UpdateHeaderStats()
    {
        var active = DnsSwitcher.GetActive();
        var bypass = UpdateBypassStat();
        HeaderStats.Visibility = active == null && !bypass ? Visibility.Collapsed : Visibility.Visible;
        // Без подключения в шапке только обход: пинг и ИИ относятся к серверу
        var conn = active == null ? Visibility.Collapsed : Visibility.Visible;
        StatPingBlock.Visibility = StatSep1.Visibility = StatStabBlock.Visibility = StatSep2.Visibility = StatServicesBlock.Visibility = ActiveRefresh.Visibility = conn;
        StatSep3.Visibility = active != null && bypass ? Visibility.Visible : Visibility.Collapsed;
        if (active == null) return;
        var srv = _servers.FirstOrDefault(x => x.Id == active.ServerId);
        StatPing.Text = srv?.LatencyMs == null ? "—" : $"{srv.PingText} мс";
        StatPingBars.Latency = srv?.LatencyMs;
        StatStab.Text = srv?.Stability ?? "—";
        var snap = _traySnap;
        StatServices.Text = snap == null ? "—" : $"{snap.Services.Count(x => x.Access.StartsWith("✓"))} из {snap.Services.Count}";
    }

    /// <summary>
    /// Блок «В обходе» в шапке: сколько сайтов идет через zapret, а при обходе по адресам и сколько сетей.
    /// Возвращает, виден ли блок (обход работает).
    /// </summary>
    private bool UpdateBypassStat()
    {
        var z = _tools.OfType<ZapretTool>().First();
        var on = z.IsInstalled && z.IsRunning();
        StatBypassBlock.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return false;
        var (sites, nets) = z.Coverage(BypassCatalog.LoadCached());
        var n = new System.Globalization.CultureInfo("ru-RU").NumberFormat;
        StatBypass.Text = nets > 0 ? $"{sites.ToString("N0", n)} сайтов · {nets.ToString("N0", n)} сетей" : $"{sites.ToString("N0", n)} сайтов";
        StatBypassBlock.ToolTip = nets > 0
            ? $"zapret обходит блокировку для {sites:N0} сайтов из списков и для {nets:N0} сетей по адресам (IPSet), в том числе сайтов на Cloudflare и хостингах"
            : $"zapret обходит блокировку для {sites:N0} сайтов из списков. Обход по адресам (IPSet) выключен: его можно включить в «Инструментах»";
        return true;
    }


    /// <summary>Длительность анимации. Если в Windows выключены анимации (Специальные возможности), переход мгновенный.</summary>
    private static TimeSpan Motion(int ms) => SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    /// <summary>Плавная смена цвета кнопки: синий «Подключить» → красный «Отключить» и обратно.</summary>
    /// <summary>
    /// Сменился язык. Текст элементов переводит Loc сам; здесь то, что вне элементов: меню трея и данные в таблице
    /// (ячейки берут текст из привязок, их перечитываем).
    /// </summary>
    private void OnLanguageChanged()
    {
        UpdateToolsTray();
        TranslateTray();
        foreach (var s in _servers) s.NotifyStability();
        _view?.Refresh();
        _compareSig = null;
        if (_before != null || _after != null) ShowCompare(DnsSwitcher.GetActive() == null && _after == null);
        _topSig = null;
        UpdateTopList();
        UpdateStats();
        if (_updateResultTimer.IsEnabled) ResetUpdateResult(null, EventArgs.Empty);
        HeaderVersion.Text = $"версия {UpdateChecker.CurrentVersion}";
    }

    /// <summary>Сменились цвета: части, которые окно рисует кодом, перерисовываются с новыми кистями.</summary>
    private void OnThemeChanged()
    {
        // Знак CHIMERION в значках окон и трея: полосы следуют за акцентом
        Controls.Brand.RefreshWindowIcons();
        if (_tray != null) { var old = _tray.Icon; _tray.Icon = Controls.Brand.TrayIcon(); old?.Dispose(); }
        ServerGrid.Resources[SystemColors.HighlightBrushKey] = FindResource("Selection");
        ServerGrid.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = FindResource("SelectionInactive");
        ServerGrid.Resources[SystemColors.HighlightTextBrushKey] = FindResource("Text"); // текст выделенной строки читается и на светлой теме
        foreach (Window w in Application.Current.Windows) Services.DarkTitle.Apply(w); // заголовок окна Windows в цвет темы
        if (!IsLoaded) return; // при запуске цвета ставятся до загрузки списка: перерисовывать еще нечего
        if (_connBrush != null) AnimateConnectColor(DnsSwitcher.GetActive() != null ? "BadButton" : "AccentButton");
        _compareSig = null; // карточки перерисовываются, даже если данные те же: поменялись цвета
        if (_before != null || _after != null) ShowCompare(DnsSwitcher.GetActive() == null && _after == null);
        _topSig = null;
        UpdateTopList();
        UpdateStatusVisuals();
        Grid_SelectionChanged(ServerGrid, null!); // строка выбранного сервера над таблицей
        ServerGrid.Items.Refresh(); // строки заново: логотипы сервисов и цвета, взятые при создании строк
    }

    /// <summary>Точка рядом с плашкой: синяя мигает во время работы, зеленая пульсирует при подключении.</summary>
    private void UpdateStatusVisuals()
    {
        bool on = DnsSwitcher.GetActive() != null;
        string mode = _working ? "busy" : on ? "on" : "idle";
        if (mode == _dotMode) return;
        _dotMode = mode;

        ConnDot.BeginAnimation(UIElement.OpacityProperty, null);
        ConnDot.Opacity = 1;
        ConnDot.Fill = (Brush)FindResource(mode switch { "busy" => "Accent", "on" => "Ok", _ => "Idle" });
        // Пульсация и мигание точки отключаются вместе с анимациями Windows
        if (mode == "idle" || !SystemParameters.ClientAreaAnimation) return;

        bool busy = mode == "busy";
        ConnDot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, busy ? 0.25 : 0.4, TimeSpan.FromSeconds(busy ? 0.5 : 1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase(),
        });
    }

    private void ShowToast(string text, string colorKey = "Ok")
    {
        // Окно в трее или свернуто: уведомление в окне никто не увидит. Важное показываем значком в трее
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            if (colorKey is "Warn" or "Bad") _tray?.ShowBalloonTip(6000, "CHIMERION", PrivacyGuard.Mask(Loc.T(text)),
                colorKey == "Bad" ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }
        // Внизу одна панель: если видна отмена, уведомление становится ее второй строкой
        if (_undo != null && UndoBar.Visibility == Visibility.Visible)
        {
            SetUndoHint(text.Replace("\n", " · "));
            return;
        }
        var shown = _toastTimer.IsEnabled;
        // То же сообщение еще на экране: только продлеваем, без повторной анимации
        if (shown && ToastText.Text == Loc.T(text)) { _toastTimer.Stop(); _toastTimer.Start(); return; }
        // Предупреждение или ошибку не затираем сразу: следующее сообщение ждет, пока его прочтут (не дольше очереди из трех)
        if (shown && _toastKey is "Warn" or "Bad" && DateTime.Now - _toastSince < TimeSpan.FromSeconds(1.5) && colorKey == "Ok")
        {
            if (_toastQueue.Count < 3 && !_toastQueue.Contains((text, colorKey))) _toastQueue.Enqueue((text, colorKey));
            return;
        }
        _toastKey = colorKey;
        _toastSince = DateTime.Now;
        // Сколько держать: ошибки и длинные сообщения дольше
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(2.4 + text.Length / 30.0 + (colorKey == "Bad" ? 2.5 : colorKey == "Warn" ? 1.2 : 0), 2.8, 9));
        ToastText.Text = text;
        ToastDot.Fill = (Brush)FindResource(colorKey);
        Toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(200)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, null);
        ToastShift.Y = 12;
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Motion(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Убирает уведомление справа внизу сразу: его место заняла панель отмены.</summary>
    private void HideToast()
    {
        _toastTimer.Stop();
        _toastQueue.Clear();
        Toast.BeginAnimation(UIElement.OpacityProperty, null);
        Toast.Opacity = 0;
    }

    /// <summary>Плавный переход размера окна; при запуске — сразу.</summary>
    /// <summary>
    /// Окно целиком на рабочей области экрана (без панели задач). Размер по умолчанию 1600×1040 больше экрана ноутбука,
    /// без этого низ окна с кнопками уходил за край.
    /// </summary>
    private void FitToScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var area = SystemParameters.WorkArea;
        double w = Math.Min(Width, area.Width), h = Math.Min(Height, area.Height);
        if (w < MinWidth) MinWidth = w;
        if (h < MinHeight) MinHeight = h;
        Width = w; Height = h;
        if (double.IsNaN(Left) || Left + w > area.Right || Left < area.Left) Left = Math.Max(area.Left, area.Left + (area.Width - w) / 2);
        if (double.IsNaN(Top) || Top + h > area.Bottom || Top < area.Top) Top = Math.Max(area.Top, area.Top + (area.Height - h) / 2);
    }

    private void ResizeTo(double w, double h, bool animate)
    {
        var area = SystemParameters.WorkArea;
        w = Math.Min(w, area.Width);
        h = Math.Min(h, area.Height);
        if (!animate || !IsLoaded) { Width = w; Height = h; FitToScreen(); return; }
        var d = Motion(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var wa = new DoubleAnimation(w, d) { EasingFunction = ease };
        wa.Completed += (_, _) => { BeginAnimation(WidthProperty, null); Width = w; };
        var ha = new DoubleAnimation(h, d) { EasingFunction = ease };
        ha.Completed += (_, _) => { BeginAnimation(HeightProperty, null); Height = h; FitToScreen(); };
        BeginAnimation(WidthProperty, wa);
        BeginAnimation(HeightProperty, ha);
        if (Content is UIElement root)
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, 1, Motion(260)));
    }


    private int _workDepth;

    /// <summary>Начало долгой работы. Вложенная работа (подключение во время проверки или поиска) получает
    /// токен внешней: «Стоп» по-прежнему останавливает проверку, а кнопки включаются только после конца обеих.</summary>
    private CancellationToken BeginWork()
    {
        if (_workDepth++ > 0 && _cts != null) return _cts.Token;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _working = true;
        ListActions.IsEnabled = ApplyActions.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        UpdateStats();
        UpdateStatusVisuals();
        return _cts.Token;
    }

    /// <summary>Бегущая полоса прогресса для работы без известного объема (подключение, отключение).</summary>
    private void ShowBusyBar()
    {
        WorkBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        WorkBar.IsIndeterminate = true;
        WorkBar.Visibility = Visibility.Visible;
    }

    private void EndWork()
    {
        if (_workDepth > 0) _workDepth--;
        if (_workDepth > 0) return; // внешняя работа еще идет
        _working = false;
        ListActions.IsEnabled = ApplyActions.IsEnabled = true;
        StopButton.Visibility = Visibility.Collapsed;
        UpdateStats(); // пустое состояние таблицы: «идет проверка» сменится итогом
        ProgressText.Text = "";
        WorkBar.Visibility = Visibility.Collapsed;
        WorkBar.IsIndeterminate = false;
        UpdateStatusVisuals();
        UpdateStats();
    }
}
