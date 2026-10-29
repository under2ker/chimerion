using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Карточка обновления внизу левого меню (на главной и в панели управления). Единственное место, где проверяется и
/// ставится новая версия: показывает текущую версию, итог проверки и найденное обновление. В узком меню видна одна иконка.
/// Во время проверки значок крутится и по низу карточки бежит полоса; итог приходит плавно: цвета перетекают,
/// текст сменяется сдвигом, значок пружинисто «щелкает».
/// </summary>
public partial class MainWindow
{
    private readonly List<UpdateCardView> _updateCards = new();
    private readonly DispatcherTimer _updateResultTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _updateChecking;
    private (string Text, string Color, string? Reason)? _updateResult;
    /// <summary>Самое короткое время «Проверяю…»: быстрый ответ иначе мелькал бы, и вращение не было бы видно.</summary>
    internal static readonly TimeSpan UpdateCheckMinShow = TimeSpan.FromMilliseconds(900);

    private sealed class UpdateCardView
    {
        public Border Card = null!;
        public Border Badge = null!;
        public Controls.Icon Icon = null!;
        public RotateTransform Spin = new();
        public TextBlock Title = null!, Sub = null!;
        public StackPanel Text = null!;
        public Grid Bar = null!;
        public TranslateTransform BarMove = new();
        public string? State;
        public bool Spinning, Pulsing;
    }

    /// <summary>Новая карточка, подписанная на общее состояние. Вызывать для каждого меню.</summary>
    private Border CreateUpdateCard()
    {
        var v = new UpdateCardView();
        v.Icon = new Controls.Icon { Kind = "refresh-cw", Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                     RenderTransformOrigin = new Point(.5, .5), RenderTransform = v.Spin };
        v.Badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = v.Icon, VerticalAlignment = VerticalAlignment.Center };
        v.Title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        v.Sub = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        v.Text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        v.Text.Children.Add(v.Title); v.Text.Children.Add(v.Sub);
        var row = new DockPanel();
        DockPanel.SetDock(v.Badge, Dock.Left); row.Children.Add(v.Badge); row.Children.Add(v.Text);
        // Бегущая полоса по низу карточки на время проверки
        var runner = new Rectangle { Width = 46, Height = 2, RadiusX = 1, RadiusY = 1, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = v.BarMove };
        runner.SetResourceReference(Shape.FillProperty, "Accent");
        v.Bar = new Grid { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, -5), ClipToBounds = true, Opacity = 0, IsHitTestVisible = false };
        v.Bar.Children.Add(runner);
        var layers = new Grid();
        layers.Children.Add(row); layers.Children.Add(v.Bar);
        v.Card = new Border { Child = layers, Padding = new Thickness(4, 6, 8, 6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                              Cursor = Cursors.Hand, Focusable = true, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(v.Card, "Обновление CHIMERION");
        v.Card.MouseEnter += (_, _) => PaintUpdateCard(v);
        v.Card.MouseLeave += (_, _) => PaintUpdateCard(v);
        v.Card.MouseLeftButtonDown += (_, _) => { if (!_updateChecking) Ui.Pop(v.Card, .97); };
        v.Card.MouseLeftButtonUp += (_, _) => UpdateCard_Click();
        v.Card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; UpdateCard_Click(); } };
        v.Card.Loaded += (_, _) => { v.Spinning = v.Pulsing = false; PaintUpdateCard(v); }; // анимации после возврата в окно
        _updateCards.Add(v);
        PaintUpdateCard(v);
        return v.Card;
    }

    /// <summary>Ссылки внизу левого меню над карточкой обновления: сайт, канал с новостями, сообщить о проблеме.</summary>
    private (string Title, string Icon, Action Click)[] SideLinks() => new (string, string, Action)[]
    {
        ("Сообщить о сбое", "circle-help", () => DiagToggle_Click(this, new RoutedEventArgs())),
        ("Канал в Telegram", "send", () => AutoStart.OpenUrl("https://t.me/chimeriondns")),
        ("Сайт chimerion.dev", "external-link", () => AutoStart.OpenUrl("https://chimerion.dev")),
    };

    /// <summary>Низ меню панели управления: те же ссылки кнопками меню и карточка обновления.</summary>
    private FrameworkElement CreatePanelBottom()
    {
        var box = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var style = (Style)FindResource("PanelNavButton");
        foreach (var (title, icon, act) in SideLinks())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Controls.Icon { Kind = icon, Size = 16, Margin = new Thickness(0, 0, 10, 0) });
            var text = new TextBlock { Text = title };
            row.Children.Add(text);
            var b = new Button { Content = row, Style = style, ToolTip = title };
            AutomationProperties.SetName(b, title);
            b.Click += (_, _) => act();
            box.Children.Add(b);
            _panelLinkTexts.Add(text);
        }
        box.Children.Add(CreateUpdateCard());
        return box;
    }

    private readonly List<TextBlock> _panelLinkTexts = new();

    private void UpdateCard_Click()
    {
        if (_updateChecking) return;
        CheckUpdate_Click(this, new RoutedEventArgs());
    }

    /// <summary>В узком меню подписи карточки прозрачны и обрезаются краем, как у пунктов меню.</summary>
    private void SetUpdateCardsExpanded(bool expanded)
    {
        foreach (var v in _updateCards.Where(c => ReferenceEquals(c.Card.Parent, PanelUpdateHost.Content))) { v.Text.Opacity = expanded ? 1 : 0; v.Card.BorderThickness = new Thickness(expanded ? 1 : 0); }
        foreach (var t in _panelLinkTexts) t.Opacity = expanded ? 1 : 0;
    }

    private void RefreshUpdateCards() { foreach (var v in _updateCards) PaintUpdateCard(v); }

    private void PaintUpdateCard(UpdateCardView v)
    {
        bool hover = v.Card.IsMouseOver;
        string state, title, sub, kind, badge, fore, subColor, border, back;
        if (_updateChecking)
        {
            (state, title, sub, kind) = ("check", "Проверяю…", "Связь с сервером обновлений", "refresh-cw");
            (badge, fore, subColor, border, back) = ("AccentSoft", "Accent", "Muted", "Accent", "Surface2");
        }
        else if (_pendingUpdate is { } info)
        {
            (state, title, sub, kind) = ("new", $"Доступна {info.Version.ToString(3)}", "Нажмите, чтобы обновить", "circle-arrow-up");
            (badge, fore, subColor, border, back) = ("Accent", "OnToggle", "Accent", "Accent", "AccentSoft");
        }
        else if (_updateResult is { } r)
        {
            bool ok = r.Color == "Ok";
            (state, title, sub, kind) = (ok ? "ok" : "fail", ok ? "Последняя версия" : "Не удалось проверить", ok ? $"Установлена {UpdateChecker.CurrentVersion}" : r.Text, ok ? "circle-check" : "triangle-alert");
            (badge, fore, subColor, border, back) = (ok ? "ChipOk" : "ChipWarn", r.Color, r.Color, r.Color, "Surface2");
        }
        else
        {
            (state, title, sub, kind) = ("idle", $"Версия {UpdateChecker.CurrentVersion}", "Проверить обновление", "refresh-cw");
            (badge, fore, subColor, border, back) = (hover ? "AccentSoft" : "Surface2", "Accent", "Muted", hover ? "Accent" : "Border", hover ? "SurfaceHover" : "Surface2");
        }
        bool changed = v.State != state;
        v.State = state;
        Ui.SwapText(v.Title, title);
        Ui.SwapText(v.Sub, sub, 4);
        v.Card.ToolTip = _updateResult?.Reason ?? (_pendingUpdate != null ? "Посмотреть, что нового, и установить" : "Проверить, есть ли новая версия CHIMERION");
        Ui.FadeBrush(v.Badge, Border.BackgroundProperty, badge);
        Ui.FadeBrush(v.Sub, TextBlock.ForegroundProperty, subColor);
        Ui.FadeBrush(v.Card, Border.BorderBrushProperty, border);
        Ui.FadeBrush(v.Card, Border.BackgroundProperty, back);
        v.Icon.SetResourceReference(Controls.Icon.ForegroundProperty, fore);
        if (v.Icon.Kind != kind) { v.Icon.Kind = kind; }
        if (changed && state is "ok" or "fail" or "new") Ui.Pop(v.Badge, .6);
        SpinUpdateIcon(v, state == "check");
        RunUpdateBar(v, state == "check");
        PulseUpdateBadge(v, state == "new");
    }

    /// <summary>Значок крутится во время проверки; после нее доворачивается до целого оборота и плавно встает.</summary>
    private static void SpinUpdateIcon(UpdateCardView v, bool on)
    {
        if (on == v.Spinning) return;
        v.Spinning = on;
        if (!Ui.Animate) { v.Spin.BeginAnimation(RotateTransform.AngleProperty, null); v.Spin.Angle = 0; return; }
        if (on)
        {
            v.Spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever });
            return;
        }
        double at = v.Spin.Angle % 360;
        var settle = new DoubleAnimation(at, 360, TimeSpan.FromMilliseconds(Math.Max(220, (360 - at) * 1.6))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        settle.Completed += (_, _) => { if (!v.Spinning) { v.Spin.BeginAnimation(RotateTransform.AngleProperty, null); v.Spin.Angle = 0; } };
        v.Spin.BeginAnimation(RotateTransform.AngleProperty, settle);
    }

    private static void RunUpdateBar(UpdateCardView v, bool on)
    {
        if (!Ui.Animate) { v.Bar.Opacity = 0; return; }
        if (on && v.Bar.Opacity < 1 && v.Bar.Tag as string != "run")
        {
            v.Bar.Tag = "run";
            double w = Math.Max(120, v.Card.ActualWidth);
            v.BarMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-50, w, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            v.Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ui.Motion(200)));
        }
        else if (!on && v.Bar.Tag as string == "run")
        {
            v.Bar.Tag = null;
            var fade = new DoubleAnimation(0, Ui.Motion(300));
            fade.Completed += (_, _) => { if (v.Bar.Tag == null) v.BarMove.BeginAnimation(TranslateTransform.XProperty, null); };
            v.Bar.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>Найдено обновление: значок мягко пульсирует, чтобы карточку заметили.</summary>
    private static void PulseUpdateBadge(UpdateCardView v, bool on)
    {
        if (on == v.Pulsing) return;
        v.Pulsing = on;
        if (on && Ui.Animate)
            v.Badge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .55, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase(), BeginTime = TimeSpan.FromMilliseconds(450) });
        else { v.Badge.BeginAnimation(OpacityProperty, null); v.Badge.Opacity = 1; }
    }

    /// <summary>Фон нашел версию: карточка становится «Доступна …» и открывает окно сразу, без новой проверки.</summary>
    private void ShowUpdateAvailable(UpdateInfo info) { _pendingUpdate = info; _updateResult = null; RefreshUpdateCards(); }

    private void ClearUpdateAvailable() { _pendingUpdate = null; RefreshUpdateCards(); }

    /// <summary>Итог проверки на карточке; через 6 с она возвращается к виду с версией.</summary>
    private void ShowUpdateResult(string text, string colorKey, string? reason)
    {
        _updateResult = (text, colorKey, reason);
        RefreshUpdateCards();
        _updateResultTimer.Stop();
        _updateResultTimer.Tick -= ResetUpdateResult;
        _updateResultTimer.Tick += ResetUpdateResult;
        _updateResultTimer.Start();
        if (!IsVisible || WindowState == WindowState.Minimized) ShowToast(text + (reason != null ? ": " + reason : ""), colorKey);
    }

    private void ResetUpdateResult(object? sender, EventArgs e)
    {
        _updateResultTimer.Stop();
        _updateResult = null;
        RefreshUpdateCards();
    }
}
