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
    public static Brush B(string key) => (Brush)Res[key];

    /// <summary>Корневая колонка окна: одинаковые поля во всех окнах.</summary>
    public static StackPanel Root() => new() { Margin = new Thickness(24, 22, 24, 20) };

    /// <summary>Заголовок раздела, как в настройках: капс, 11 пт, приглушенный.</summary>
    public static TextBlock Section(string text, bool first = false) => new()
    {
        Text = text.ToUpperInvariant(), Foreground = B("Muted"), FontSize = 11, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, first ? 4 : 20, 0, 8),
    };

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
    public static TextBlock Status() => new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = B("Muted"), Margin = new Thickness(0, 10, 0, 0) };

    public static void SetStatus(TextBlock t, string text, bool error = false)
    {
        t.Text = text;
        t.Foreground = B(error ? "Bad" : "Muted");
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
            var p = new TextBlock { Text = pill, Foreground = B(pillBrush), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
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
    /// Длительность анимации с учетом Windows: если в «Специальных возможностях» выключены эффекты анимации,
    /// все переходы мгновенные. Логика не зависит от окончания анимаций, поэтому ничего не зависает.
    /// </summary>
    public static Duration Motion(int ms) => SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
    public static bool Animate => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Текст в буфер обмена. Буфер часто на миг держат другие программы (ShareX, менеджеры буфера, удаленный рабочий
    /// стол), и обычный Clipboard.SetText падал «буфер занят». Сначала копирование с повторами, затем без сброса
    /// буфера (OleFlushClipboard): именно сброс обычно и не проходит. false: скопировать не удалось.
    /// </summary>
    public static bool CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch (Exception) { System.Threading.Thread.Sleep(100); }
        }
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetDataObject(text, false); return true; }
            catch (Exception) { System.Threading.Thread.Sleep(150); }
        }
        return false;
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
    public static Border Separator() => new() { Height = 1, Background = B("Border"), Margin = new Thickness(0, 10, 0, 10) };

    /// <summary>Строка настройки: название и пояснение слева, элемент управления справа.</summary>
    public static Grid Row(string label, string? hint, UIElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(hint)) left.Children.Add(Hint(hint, 2).With(t => t.FontSize = 11));
        g.Children.Add(left);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
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
        if (style == "Danger") { b.Background = B("BadButton"); b.BorderBrush = b.Background; }
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
        if (left != null) d.Children.Add(left);
        return new Border { BorderBrush = B("Border"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(0, 14, 0, 0), Child = d };
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
        Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80,
    };
}
