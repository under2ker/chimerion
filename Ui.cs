using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dnex;

/// <summary>
/// Общие элементы окон CHIMERION, чтобы все окна, собранные в коде, выглядели как окно настроек:
/// одинаковые ширина и поля, заголовки разделов капсом, карточки, строки настроек «название и пояснение
/// слева, переключатель справа», кнопки выбора с «▾», нижняя строка с линией сверху.
/// </summary>
internal static class Ui
{
    /// <summary>Ширина окон с настройками и списками и ширина коротких диалогов.</summary>
    public const double Wide = 600, Narrow = 480;

    private static ResourceDictionary Res => Application.Current.Resources;

    /// <summary>
    /// Рабочая область (без панели задач) экрана, где находится окно, в единицах WPF. SystemParameters.WorkArea описывает
    /// только основной экран: окно на втором мониторе из-за этого сдвигалось на основной. Окно без дескриптора (еще не
    /// показано) или null: экран видимого главного окна, иначе основной.
    /// </summary>
    public static Rect WorkAreaOf(Window? w)
    {
        try
        {
            var owner = w;
            var h = w != null ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero;
            if (h == IntPtr.Zero && Application.Current?.MainWindow is { IsVisible: true } m) { owner = m; h = new System.Windows.Interop.WindowInteropHelper(m).Handle; }
            if (h != IntPtr.Zero && owner != null)
            {
                var r = System.Windows.Forms.Screen.FromHandle(h).WorkingArea;
                var dpi = VisualTreeHelper.GetDpi(owner);
                return new Rect(r.X / dpi.DpiScaleX, r.Y / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);
            }
        }
        catch (Exception) { /* не вышло узнать экран: основной */ }
        return SystemParameters.WorkArea;
    }
    public static Brush B(string key) => (Brush)Res[key];

    /// <summary>Корневая колонка окна: одинаковые поля во всех окнах.</summary>
    public static StackPanel Root() => new() { Margin = new Thickness(24, 22, 24, 20) };

    /// <summary>Заголовок раздела, как в настройках: обычными буквами, 12 пт, приглушенный.</summary>
    public static TextBlock Section(string text, bool first = false) => new TextBlock
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, first ? 4 : 20, 0, 8),
    }.Ref(TextBlock.ForegroundProperty, "Muted");

    /// <summary>Свойство по ссылке на ресурс палитры: при смене цвета в настройках обновится само.</summary>
    public static T Ref<T>(this T el, DependencyProperty dp, string key) where T : FrameworkElement { el.SetResourceReference(dp, key); return el; }

    /// <summary>Пояснение мелким приглушенным текстом.</summary>
    public static TextBlock Hint(string text, double top = 4, string brush = "Muted") => new TextBlock
    {
        Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), LineHeight = 18,
    }.Ref(TextBlock.ForegroundProperty, brush);

    /// <summary>
    /// Текст с иконкой в той же строке (иконки только из Controls.Icon, без символов шрифта вроде ▾ или ⚠).
    /// before: иконка перед текстом, иначе после. Цвет иконки берется у текста.
    /// </summary>
    public static void SetTextWithIcon(TextBlock tb, string text, string kind, bool before = false, double size = 13)
    {
        tb.Inlines.Clear();
        var icon = new Controls.Icon { Kind = kind, Size = size, Margin = before ? new Thickness(0, 0, 5, -2) : new Thickness(5, 0, 0, -2) };
        icon.SetBinding(Controls.Icon.ForegroundProperty, new System.Windows.Data.Binding(nameof(TextBlock.Foreground)) { Source = tb });
        var box = new System.Windows.Documents.InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center };
        if (before) tb.Inlines.Add(box);
        tb.Inlines.Add(new System.Windows.Documents.Run(Services.Loc.T(text))); // строки внутри Inlines перевод сам не находит
        if (!before) tb.Inlines.Add(box);
    }

    /// <summary>Строка состояния: ход работы или ошибка.</summary>
    public static TextBlock Status() => new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }.Ref(TextBlock.ForegroundProperty, "Muted");

    public static void SetStatus(TextBlock t, string text, bool error = false)
    {
        t.Text = text;
        t.SetResourceReference(TextBlock.ForegroundProperty, error ? "Bad" : "Muted");
        t.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Карточка (как в настройках). selected — выбранный вариант с синей рамкой.</summary>
    public static Border Card(UIElement child, bool selected = false, double bottom = 10) => new Border
    {
        BorderThickness = new Thickness(selected ? 1.5 : 1), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, bottom), Child = child,
    }.Ref(Border.BackgroundProperty, selected ? "Surface2" : "Surface").Ref(Border.BorderBrushProperty, selected ? "Accent" : "Border");

    /// <summary>Заголовок карточки: название слева, метка состояния справа.</summary>
    public static DockPanel CardTitle(string title, string? pill = null, string pillBrush = "Muted")
    {
        var d = new DockPanel();
        if (pill != null)
        {
            var p = new TextBlock { Text = pill, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, pillBrush);
            DockPanel.SetDock(p, Dock.Right);
            d.Children.Add(p);
        }
        d.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        return d;
    }

    /// <summary>Метка «рекомендуем»: салатовая плашка рядом с вариантом, который стоит выбрать.</summary>
    public static Border Badge(string text = "рекомендуем") => new Border
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, "Lime"),
    }.Ref(Border.BackgroundProperty, "LimeSoft");

    /// <summary>Галочка набора: название с числом доменов и метка «рекомендуем», если набор советуем выбрать.</summary>
    public static CheckBox SetCheck(string title, int count, string note, bool recommended, bool on)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 205 };
        text.Inlines.Add(new System.Windows.Documents.Run($"{title} ({count})"));
        if (recommended)
        {
            text.Inlines.Add(new System.Windows.Documents.Run(" "));
            text.Inlines.Add(new System.Windows.Documents.InlineUIContainer(Badge().With(b => { b.Margin = new Thickness(2, 0, 0, -3); }))
                             { BaselineAlignment = BaselineAlignment.Center });
        }
        return new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 4, 12, 4), ToolTip = note.Length > 0 ? note : null };
    }

    /// <summary>
    /// Быстрый выбор для списка галочек: «Рекомендуемые» (ровно рекомендуемые), «Выбрать все», «Снять все».
    /// Ручной выбор остается: после кнопки любую галочку можно поменять.
    /// </summary>
    public static WrapPanel ChoiceBar(IReadOnlyList<CheckBox> checks, Func<CheckBox, bool> recommended)
    {
        void Set(Func<CheckBox, bool> on) { foreach (var c in checks) c.IsChecked = on(c); }
        var rec = Action("Рекомендуемые", "Strong", () => Set(recommended)).With(b => b.ToolTip = "Отметить только то, что помечено «рекомендуем»");
        var all = Action("Выбрать все", "Ghost", () => Set(_ => true));
        var none = Action("Снять все", "Ghost", () => Set(_ => false));
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var b in new[] { rec, all, none }) { b.Margin = new Thickness(0, 0, 8, 0); bar.Children.Add(b); }
        return bar;
    }

    /// <summary>
    /// Заголовок раздела в списке галочек со ссылкой «выбрать все» или «снять все» для этого раздела.
    /// Текст ссылки следит за галочками: все отмечены, значит «снять все».
    /// </summary>
    public static DockPanel CategoryHeader(string title, IReadOnlyList<CheckBox> checks, bool first)
    {
        var d = new DockPanel { Margin = new Thickness(0, first ? 0 : 12, 12, 2) };
        var link = new Button
        {
            Style = (Style)Res["Ghost"], Padding = new Thickness(8, 2, 8, 2), MinHeight = 0, Margin = new Thickness(0), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Refresh() => link.Content = checks.All(c => c.IsChecked == true) ? "снять все" : "выбрать все";
        link.Click += (_, _) => { var on = !checks.All(c => c.IsChecked == true); foreach (var c in checks) c.IsChecked = on; };
        foreach (var c in checks) { c.Checked += (_, _) => Refresh(); c.Unchecked += (_, _) => Refresh(); }
        Refresh();
        DockPanel.SetDock(link, Dock.Right);
        d.Children.Add(link);
        d.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return d;
    }

    /// <summary>Подсказка «что нажать»: короткий порядок действий синим текстом.</summary>
    public static TextBlock Steps(string text, double top = 8) => Hint(text, top, "Accent");

    /// <summary>
    /// Если в Windows выключены «Эффекты анимации», длительности переходов из App.xaml (наведение, вкладки, строки таблицы)
    /// становятся нулевыми. Вызывать до создания окон: стили читают их при первом показе. Бегущие индикаторы загрузки
    /// остаются: это сигнал «идет работа».
    /// </summary>
    public static void ApplyMotionPreference()
    {
        if (SystemParameters.ClientAreaAnimation) return;
        foreach (var key in new[] { "MotionFast", "MotionTab", "MotionOut", "MotionFade", "MotionRow" })
            if (Res.Contains(key)) Res[key] = new Duration(TimeSpan.Zero);
    }

    /// <summary>
    /// Кнопка только со значком (крестик, свернуть, обновить) без имени для экранного диктора: берем текст ее подсказки.
    /// Один обработчик на все кнопки программы.
    /// </summary>
    public static void NameIconButtons() =>
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is not System.Windows.Controls.Primitives.ButtonBase b || b.Content is not (Viewbox or Image or System.Windows.Shapes.Shape)) return;
            if (!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b))) return;
            if (b.ToolTip is string tip && tip.Length > 0) System.Windows.Automation.AutomationProperties.SetName(b, Services.Loc.T(tip));
        }));

    /// <summary>
    /// Длительность анимации с учетом Windows: если в «Специальных возможностях» выключены эффекты анимации,
    /// все переходы мгновенные. Логика не зависит от окончания анимаций, поэтому ничего не зависает.
    /// </summary>
    public static Duration Motion(int ms) => SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
    public static bool Animate => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Текст в буфер обмена. Буфер часто на миг держат другие программы (ShareX, менеджеры буфера, удаленный рабочий
    /// стол). Пишем напрямую через Windows с короткими повторами: Clipboard.SetDataObject из WPF на части машин
    /// отказывает «буфер занят» даже при свободном буфере и каждый раз тратит около секунды на свои повторы, а
    /// копирование идет в потоке окна, и программа замирала на несколько секунд. WPF — запасной путь, одна попытка.
    /// Занятый буфер — около 0,5 с, иначе не больше ~2,5 с. false: скопировать не удалось.
    /// </summary>
    public static bool CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (NativeCopy(text, OwnerWindow(), out var code)) return true;
        // Буфер так и не открылся, его держит другая программа: WPF упрется в то же и потратит еще около секунды
        if (code == -1) { Dnex.Services.SupportTrace.Write($"Буфер обмена занят другой программой; символов={text.Length}"); return false; }
        // Только с copy: true. Без него данные отдаются позже по запросу, а программы, которые читают буфер после каждой
        // записи (общий буфер VirtualBox, RDP, менеджеры буфера), ждут занятый поток окна и держат буфер открытым
        try { Clipboard.SetDataObject(text, true); return true; }
        catch (Exception e)
        {
            Dnex.Services.SupportTrace.Write($"Буфер обмена не записан: код Windows={code}, WPF {e.GetType().Name} 0x{e.HResult:X8}; символов={text.Length}");
            return false;
        }
    }

    /// <summary>Окно программы для владения буфером: без владельца Windows может не принять данные после EmptyClipboard.</summary>
    private static IntPtr OwnerWindow()
    {
        // Не из потока окон (например, из фоновой задачи): окна трогать нельзя, копируем без владельца
        if (Application.Current is not { } app || !app.Dispatcher.CheckAccess()) return IntPtr.Zero;
        foreach (var w in new[] { app.MainWindow }.Concat(app.Windows.OfType<Window>()))
            if (w != null && new System.Windows.Interop.WindowInteropHelper(w).Handle is var h && h != IntPtr.Zero) return h;
        return IntPtr.Zero;
    }

    /// <summary>Запись напрямую через Windows. error = -1: буфер так и не открылся, его держит другая программа.</summary>
    private static bool NativeCopy(string text, IntPtr owner, out int error)
    {
        error = -1;
        // До 12 попыток через 40 мс (около 0,5 с): дольше буфер держат только зависшие программы
        for (int i = 0; i < 12; i++)
        {
            if (!NativeClip.OpenClipboard(owner)) { System.Threading.Thread.Sleep(40); continue; }
            error = 0;
            IntPtr mem = IntPtr.Zero;
            try
            {
                var bytes = (text.Length + 1) * 2;
                mem = NativeClip.GlobalAlloc(0x0042, (UIntPtr)bytes); // GMEM_MOVEABLE | GMEM_ZEROINIT
                if (mem == IntPtr.Zero) { error = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); return false; }
                var ptr = NativeClip.GlobalLock(mem);
                if (ptr == IntPtr.Zero) { error = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); return false; }
                System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                NativeClip.GlobalUnlock(mem);
                NativeClip.EmptyClipboard();
                if (NativeClip.SetClipboardData(13, mem) == IntPtr.Zero) { error = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); return false; }
                mem = IntPtr.Zero; // теперь памятью владеет система
                return true;
            }
            finally
            {
                NativeClip.CloseClipboard();
                if (mem != IntPtr.Zero) NativeClip.GlobalFree(mem);
            }
        }
        return false;
    }

    private static class NativeClip
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr owner);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr mem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalUnlock(IntPtr mem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalFree(IntPtr mem);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, (SolidColorBrush Brush, Color Target)>> Fades = new();

    /// <summary>
    /// Цвет из палитры с плавным переходом: вместо мгновенной замены кисти цвет перетекает за ms. Вызывать можно часто
    /// (таймер): тот же цвет не перезапускает анимацию, смена темы плавно перекрашивает.
    /// </summary>
    public static void FadeBrush(DependencyObject d, DependencyProperty p, string key, int ms = 320)
    {
        var res = (d as FrameworkElement)?.TryFindResource(key) ?? Application.Current?.TryFindResource(key);
        if (res is not SolidColorBrush want) { if (d is FrameworkElement f) f.SetResourceReference(p, key); return; }
        var target = want.Color;
        if (want.Opacity < 1) target.A = (byte)(target.A * want.Opacity);
        var map = Fades.GetOrCreateValue(d);
        var anim = (Color to) => new System.Windows.Media.Animation.ColorAnimation(to, Motion(ms)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
        if (map.TryGetValue(p, out var e) && ReferenceEquals(d.GetValue(p), e.Brush))
        {
            if (e.Target == target) return;
            e.Brush.BeginAnimation(SolidColorBrush.ColorProperty, anim(target));
            map[p] = (e.Brush, target);
            return;
        }
        var start = d.GetValue(p) is SolidColorBrush old ? old.Color : Color.FromArgb(0, target.R, target.G, target.B);
        if (d.GetValue(p) is SolidColorBrush o2 && o2.Opacity < 1) start.A = (byte)(start.A * o2.Opacity);
        var brush = new SolidColorBrush(start);
        d.SetValue(p, brush);
        if (start != target) brush.BeginAnimation(SolidColorBrush.ColorProperty, anim(target));
        map[p] = (brush, target);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, string> SwapPending = new();

    /// <summary>Смена текста с переходом: старый уходит вверх и гаснет, новый приходит снизу.</summary>
    public static void SwapText(TextBlock t, string text, double shift = 6)
    {
        if (SwapPending.TryGetValue(t, out _)) { SwapPending.AddOrUpdate(t, text); return; } // переход идет: подставится последний текст
        if (t.Text == text) return;
        if (!Animate || !t.IsLoaded || !t.IsVisible || string.IsNullOrEmpty(t.Text)) { t.Text = text; return; }
        var move = t.RenderTransform as TranslateTransform ?? new TranslateTransform();
        t.RenderTransform = move;
        SwapPending.AddOrUpdate(t, text);
        var ease = new System.Windows.Media.Animation.QuadraticEase();
        var outFade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
        outFade.Completed += (_, _) =>
        {
            if (SwapPending.TryGetValue(t, out var last)) t.Text = last;
            SwapPending.Remove(t);
            t.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(shift, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        };
        t.BeginAnimation(UIElement.OpacityProperty, outFade);
        move.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, -shift, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
    }

    /// <summary>Короткий «щелчок» масштабом: элемент сжимается и пружинисто возвращается (смена состояния).</summary>
    public static void Pop(FrameworkElement e, double from = .82)
    {
        if (!Animate) return;
        var scale = e.RenderTransform as ScaleTransform ?? new ScaleTransform();
        e.RenderTransform = scale; e.RenderTransformOrigin = new Point(.5, .5);
        var a = new System.Windows.Media.Animation.DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = .45, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a); scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Тонкая линия между строками внутри карточки.</summary>
    public static Border Separator() => new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) }.Ref(Border.BackgroundProperty, "Border");

    /// <summary>Строка настройки: название и пояснение слева, элемент управления справа.</summary>
    public static Grid Row(string label, string? hint, UIElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var name = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        left.Children.Add(name);
        if (!string.IsNullOrEmpty(hint)) left.Children.Add(Hint(hint, 2));
        g.Children.Add(left);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        // Название строки как имя элемента для экранного диктора: у галочки и переключателя собственного текста нет
        if (control is DependencyObject dep) System.Windows.Automation.AutomationProperties.SetLabeledBy(dep, name);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>Переключатель для строки настройки (галочка без текста: текст в самой строке).</summary>
    public static CheckBox Toggle(bool on, [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var check = new CheckBox { IsChecked = on, Margin = new Thickness(0) };
        SupportTraceUi.Identify(check, member, line);
        return check;
    }

    /// <summary>Кнопка выбора значения: текущее значение и «▾», по щелчку меню вариантов.</summary>
    public static Button Picker(string value, Action<Button> open,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new Controls.Icon { Kind = "chevron-down", Size = 14, Margin = new Thickness(8, 1, 0, 0), Opacity = 0.8 });
        var b = new Button { Content = row, Padding = new Thickness(12, 6, 12, 6), MinHeight = 32, Margin = new Thickness(0), MaxWidth = 260 };
        b.Click += (_, _) => open(b);
        SupportTraceUi.Identify(b, member, line);
        return b;
    }

    /// <summary>Кнопка действия. style: Primary, Strong, Ghost, Danger (красная заливка).</summary>
    public static Button Action(string text, string style, Action click,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(text))] string expression = "")
    {
        var b = DnexDialog.MakeButton(text, style == "Danger" ? "Primary" : style, click);
        if (style == "Danger")
        {
            // Заливка красная, текст подбирается по контрасту (у Primary он темный под салатовый акцент: на красном 3,6:1)
            b.SetResourceReference(Control.BackgroundProperty, "BadButton");
            b.SetResourceReference(Control.BorderBrushProperty, "BadButton");
            b.Foreground = new SolidColorBrush(Services.ThemeColors.OnFill(((SolidColorBrush)B("BadButton")).Color));
        }
        b.MinWidth = 0;
        b.Margin = new Thickness(0, 0, 8, 0);
        SupportTraceUi.Identify(b, member, line, expression);
        return b;
    }

    /// <summary>Ряд кнопок внутри карточки, слева направо.</summary>
    public static WrapPanel Actions(params Button[] buttons)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in buttons) w.Children.Add(b);
        return w;
    }

    /// <summary>
    /// Нижняя строка окна, как в настройках: линия сверху, слева пояснение или ошибка, справа кнопки
    /// (первая в списке встает крайней справа: обычно основная).
    /// </summary>
    public static Border Footer(UIElement? left, params Button[] right)
    {
        var d = new DockPanel { LastChildFill = true };
        foreach (var b in right)
        {
            b.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(b, Dock.Right);
            d.Children.Add(b);
        }
        // Последний элемент DockPanel занимает все свободное место. Без левой части им оказывалась крайняя левая кнопка
        // и растягивалась (в диалогах «Понятно» выходила на всю ширину): пустой заполнитель забирает место вместо нее
        d.Children.Add(left ?? new Border());
        return new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(0, 14, 0, 0), Child = d }
            .Ref(Border.BorderBrushProperty, "Border");
    }

    /// <summary>Многострочное поле: текст сверху, а не по центру (общий стиль полей центрирует однострочные).</summary>
    public static TextBox MultiLine(string text, double height) => new()
    {
        Text = text, AcceptsReturn = true, Height = height, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 8, 10, 8),
    };

    /// <summary>
    /// Плавное изменение полосы прогресса: заливка доезжает до нового значения за 300 мс, а не прыгает рывком
    /// на каждый проверенный сервер или скачанный кусок. Сброс к нулю мгновенный.
    /// </summary>
    public static void SetProgress(ProgressBar bar, double value)
    {
        if (value <= bar.Minimum || value < bar.Value || !Animate) { bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null); bar.Value = value; return; }
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new System.Windows.Media.Animation.DoubleAnimation(value, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } },
            System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Прокручиваемая область для длинных списков внутри окна.</summary>
    public static ScrollViewer Scroll(UIElement content, double maxHeight) => new()
    {
        Content = content, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0),
    };

    /// <summary>Содержимое окна с прокруткой, если оно выше экрана.</summary>
    public static ScrollViewer WindowContent(UIElement root) => new()
    {
        Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = WorkAreaOf(null).Height - 80,
    };
}
