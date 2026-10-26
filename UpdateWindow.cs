using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Dnex.Controls;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно обновления. Четыре состояния: предложение (версия, откуда она и что в ней нового), установка (этапы,
/// проценты, размер, скорость, оставшееся время), готово (перезапуск) и ошибка (причина и что делать).
/// Переходы между состояниями плавные; при выключенной анимации в Windows все показывается сразу.
/// Новая версия ставится прямо из программы (см. Updater) и начинает работать после перезапуска.
/// </summary>
internal static class UpdateWindow
{
    private static Window? _open;

    /// <summary>Открытое окно обновления (второе не открывается, а показывается это).</summary>
    public static Window? Current => _open is { IsVisible: true } ? _open : null;

    private static readonly string[] StepNames = { "Контрольная сумма", "Скачивание", "Проверка подписи", "Установка" };

    public static void Show(Window owner, UpdateInfo info, Action onSkip, Action onRestart, Action<string> log)
    {
        if (Current is { } existing) { existing.Activate(); return; }
        var w = DnexDialog.CreateWindow("Обновление", Ui.Narrow + 40);
        _open = w;
        w.Closed += (_, _) => { if (_open == w) _open = null; };

        var root = Ui.Root();
        var hero = new ContentControl { Focusable = false };
        var body = new ContentControl { Focusable = false, Margin = new Thickness(0, 18, 0, 0) };
        var row = new DockPanel { LastChildFill = false };
        root.Children.Add(hero);
        root.Children.Add(body);
        root.Children.Add(new Border
        {
            BorderBrush = Ui.B("Border"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(0, 14, 0, 0), Child = row,
        }.Ref(Border.BorderBrushProperty, "Border"));
        w.Content = root;
        Appear(root, 14, 260);

        CancellationTokenSource? cts = null;
        // Закрытие окна во время скачивания отменяет его: программа остается прежней
        w.Closing += (_, _) => cts?.Cancel();

        void Buttons(params Button[] buttons)
        {
            row.Children.Clear();
            foreach (var b in buttons) row.Children.Add(b);
        }
        Button Right(Button b) { DockPanel.SetDock(b, Dock.Right); return b; }
        Button Left(Button b) { DockPanel.SetDock(b, Dock.Left); b.Margin = new Thickness(0, 0, 8, 0); return b; }
        Button Btn(string text, string style, Action click) => Ui.Action(text, style, click).With(b => b.Margin = new Thickness(8, 0, 0, 0));
        void Swap(ContentControl cc, UIElement content) { cc.Content = content; Appear(content, 8, 200); }

        // ---------- Предложение ----------
        void Offer()
        {
            var versions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            versions.Children.Add(Chip(UpdateChecker.CurrentVersion.ToString(3), "Surface", "Muted"));
            versions.Children.Add(new TextBlock { Text = "→", FontSize = 14, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted"));
            versions.Children.Add(Chip(info.Version.ToString(3), "AccentSoft", "Accent", bold: true));
            versions.Children.Add(info.Hotfix != null
                ? Chip("срочное исправление", "WarnSoft", "Warn").With(c => c.Margin = new Thickness(10, 0, 0, 0))
                : Chip("релиз", "OkSoft", "Ok").With(c => c.Margin = new Thickness(10, 0, 0, 0)));
            Swap(hero, Hero(Mark(), "Доступно обновление", $"CHIMERION {info.Version.ToString(3)}", versions));

            var notes = Notes(info.Notes);
            var card = Card(Scroll(notes, 270), padding: new Thickness(16, 14, 8, 14));
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "ЧТО НОВОГО", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) }.Ref(TextBlock.ForegroundProperty, "Muted"));
            panel.Children.Add(card);
            if (Uri.TryCreate(info.PageUrl, UriKind.Absolute, out _))
                panel.Children.Add(Link(info.Hotfix != null ? "История изменений на GitHub" : "Релиз на GitHub", () => AutoStart.OpenUrl(info.PageUrl)));
            Swap(body, panel);

            var update = Right(Btn("Обновить", "Primary", () => _ = InstallAsync()));
            update.IsDefault = true;
            update.Content = IconText("download", "Обновить");
            Buttons(update, Right(Btn("Позже", "Ghost", () => w.Close())),
                    Left(Btn("Пропустить версию", "Ghost", () => { onSkip(); w.Close(); })));
            update.Focus();
        }

        // ---------- Установка ----------
        async Task InstallAsync()
        {
            Swap(hero, Hero(Mark(), "Устанавливаю", $"CHIMERION {info.Version.ToString(3)}",
                new TextBlock { Text = "Программа работает как обычно, подключение не прерывается", FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, "Muted")));

            var steps = new StepList();
            var percent = new TextBlock { Text = "0%", FontSize = 26, FontWeight = FontWeights.SemiBold };
            var detail = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, "Muted");
            var top = new DockPanel();
            DockPanel.SetDock(percent, Dock.Left);
            top.Children.Add(percent);
            top.Children.Add(detail);
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsIndeterminate = true, Margin = new Thickness(0, 8, 0, 16) };
            var inner = new StackPanel();
            inner.Children.Add(top);
            inner.Children.Add(bar);
            inner.Children.Add(steps.View);
            Swap(body, Card(inner, padding: new Thickness(18, 14, 18, 16)));
            var cancel = Right(Btn("Отмена", "Ghost", () => cts?.Cancel()));
            Buttons(cancel);

            cts = new CancellationTokenSource();
            string? via = null;
            var started = DateTime.Now;
            double speed = 0;
            long lastDone = 0;
            var lastAt = DateTime.Now;
            // Ход установки приходит из фоновых задач: в окно переносим в его поток, как бы ни был вызван Updater
            void OnUi(Action a) { if (w.Dispatcher.CheckAccess()) a(); else w.Dispatcher.BeginInvoke(a); }
            var progress = new Progress<(double? Part, string Stage)>(p => OnUi(() =>
            {
                steps.Activate(StageIndex(p.Stage));
                var m = Regex.Match(p.Stage, @"через ([^:]+):");
                if (m.Success) via = m.Groups[1].Value;
                bar.IsIndeterminate = p.Part == null;
                if (p.Part is { } part)
                {
                    Ui.SetProgress(bar, part);
                    percent.Text = $"{part * 100:0}%";
                }
                if (StageIndex(p.Stage) >= 2) detail.Text = p.Stage.TrimEnd('…');
            }));
            var bytes = new Progress<(long Done, long? Total)>(b => OnUi(() =>
            {
                var now = DateTime.Now;
                var dt = (now - lastAt).TotalSeconds;
                if (dt >= 0.25)
                {
                    var inst = (b.Done - lastDone) / dt;
                    speed = speed == 0 ? inst : speed * 0.7 + inst * 0.3; // сглаживаем, чтобы цифры не прыгали
                    lastDone = b.Done; lastAt = now;
                }
                var parts = new List<string> { b.Total is { } t ? $"{Mb(b.Done)} из {Mb(t)}" : Mb(b.Done) };
                if (speed > 0) parts.Add($"{Mb((long)speed)}/с");
                if (speed > 0 && b.Total is { } t2 && t2 > b.Done) parts.Add($"осталось {Eta((t2 - b.Done) / speed)}");
                if (via != null) parts.Add($"через {via}");
                detail.Text = string.Join("  ·  ", parts);
            }));
            log($"Обновление: скачиваю версию {info.Version}{(info.Hotfix != null ? " (быстрое исправление)" : "")}");
            try
            {
                await Updater.InstallAsync(info.Version, progress, cts.Token, info.Hotfix, bytes);
                steps.Activate(StepNames.Length);
                Ui.SetProgress(bar, 1);
                percent.Text = "100%";
                log($"Обновление: версия {info.Version} установлена, заработает после перезапуска");
                if (Ui.Animate) await Task.Delay(450); // дать увидеть заполненную полосу и все отметки
                if (w.IsVisible) Done();
            }
            catch (OperationCanceledException)
            {
                log("Обновление отменено, программа не изменена");
                if (w.IsVisible) Offer();
            }
            catch (Exception e)
            {
                log("Обновление не установилось: " + e.Message);
                if (w.IsVisible) Failed(e.Message);
            }
            finally { cts = null; }
        }

        // ---------- Готово ----------
        // Пользователь уже нажал «Обновить»: перезапуск идет сам, через пару секунд отсчета. «Не сейчас» его отменяет
        void Done()
        {
            Swap(hero, Hero(ResultMark("circle-check", "OkSoft", "Ok"), "Готово", $"Версия {info.Version.ToString(3)} установлена", null));
            int left = 3;
            var text = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 }.Ref(TextBlock.ForegroundProperty, "Muted");
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Value = 0, Margin = new Thickness(0, 12, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(text);
            panel.Children.Add(bar);
            Swap(body, Card(panel, padding: new Thickness(16, 14, 16, 14)));
            void Say() => text.Text = $"Перезапуск через {left} с. Подключение DNS сохранится, программа вернется в том же виде.";
            Say();
            if (Ui.Animate) bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(left)));
            else bar.Value = 1;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void Go() { timer.Stop(); log($"Обновление: перезапуск в версию {info.Version}"); w.Close(); onRestart(); }
            timer.Tick += (_, _) => { if (--left <= 0) Go(); else Say(); };
            w.Closed += (_, _) => timer.Stop();
            var now = Right(Btn("Перезапустить сейчас", "Primary", Go));
            now.IsDefault = true;
            now.Content = IconText("refresh-cw", "Перезапустить сейчас");
            var later = Right(Btn("Не сейчас", "Ghost", () =>
            {
                timer.Stop();
                log($"Обновление: версия {info.Version} заработает при следующем запуске");
                w.Close();
            }));
            Buttons(now, later);
            timer.Start();
        }

        // ---------- Ошибка ----------
        void Failed(string reason)
        {
            Swap(hero, Hero(ResultMark("circle-x", "BadSoft", "Bad"), "Не получилось", "Обновление не установилось", null));
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = reason, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
            panel.Children.Add(new TextBlock
            {
                Text = "Программа осталась прежней и работает как раньше. Можно повторить позже или скачать версию вручную со страницы релиза.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 8, 0, 0),
            }.Ref(TextBlock.ForegroundProperty, "Muted"));
            Swap(body, Card(panel, padding: new Thickness(16, 14, 16, 14)));
            var retry = Right(Btn("Повторить", "Primary", () => _ = InstallAsync()));
            retry.IsDefault = true;
            Buttons(retry, Right(Btn("Закрыть", "Ghost", () => w.Close())),
                    Left(Btn("Страница релиза", "Ghost", () => { AutoStart.OpenUrl(info.PageUrl); w.Close(); })));
        }

        Offer();
        w.Owner = owner.IsVisible ? owner : null;
        w.Show();
        w.Activate();
    }

    // ---------- Части окна ----------

    /// <summary>Шапка: знак слева, над заголовком мелкая подпись капсом, под ним версии или пояснение.</summary>
    private static FrameworkElement Hero(UIElement mark, string caption, string title, UIElement? extra)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(mark);
        var col = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(new TextBlock { Text = caption.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, "Accent"));
        col.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (extra != null) col.Children.Add(extra);
        Grid.SetColumn(col, 1);
        grid.Children.Add(col);
        return grid;
    }

    /// <summary>Знак программы на плитке.</summary>
    private static Border Mark() => new Border
    {
        Width = 56, Height = 56, CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Top,
        Child = new BrandMark { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    }.Ref(Border.BackgroundProperty, "AccentSoft");

    /// <summary>Итоговый значок: появляется с легким «отскоком».</summary>
    private static Border ResultMark(string icon, string bg, string fg)
    {
        var scale = new ScaleTransform(1, 1);
        var b = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
            Child = new Icon { Kind = icon, Size = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Ref(Icon.ForegroundProperty, fg),
        }.Ref(Border.BackgroundProperty, bg);
        if (Ui.Animate)
        {
            var a = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        return b;
    }

    private static Border Chip(string text, string bg, string fg, bool bold = false) => new Border
    {
        CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 4), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, fg),
    }.Ref(Border.BackgroundProperty, bg);

    private static Border Card(UIElement child, Thickness padding) => new Border
    {
        CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = padding, Child = child,
    }.Ref(Border.BackgroundProperty, "Surface").Ref(Border.BorderBrushProperty, "Border");

    private static ScrollViewer Scroll(UIElement content, double maxHeight)
    {
        var sv = new ScrollViewer { Content = content, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        SmoothScroll.SetIsEnabled(sv, true);
        return sv;
    }

    private static StackPanel IconText(string icon, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new Icon { Kind = icon, Size = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    private static FrameworkElement Link(string text, Action click)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        p.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Accent"));
        p.Children.Add(new Icon { Kind = "external-link", Size = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Ref(Icon.ForegroundProperty, "Accent"));
        p.MouseLeftButtonUp += (_, _) => click();
        return p;
    }

    /// <summary>Появление: из прозрачности и чуть снизу. Без анимации в Windows — сразу.</summary>
    private static void Appear(UIElement el, double fromY, int ms, int delay = 0)
    {
        if (!Ui.Animate) return;
        var t = new TranslateTransform(0, fromY);
        el.RenderTransform = t;
        el.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delay);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease });
    }

    private static string Mb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} МБ" : $"{Math.Max(1, bytes / 1024)} КБ";

    private static string Eta(double seconds) => seconds < 1 ? "меньше секунды" : seconds < 60 ? $"{seconds:0} с" : $"{seconds / 60:0} мин";

    /// <summary>Этап по тексту из Updater: сумма, скачивание (и проверка архива), подпись, установка.</summary>
    internal static int StageIndex(string stage) =>
        stage.StartsWith("Получаю") ? 0
        : stage.StartsWith("Скачиваю") || stage.StartsWith("Проверяю архив") ? 1
        : stage.StartsWith("Проверяю подпись") ? 2
        : stage.StartsWith("Устанавливаю") ? 3 : 0;

    /// <summary>Список этапов: выполненные с галочкой, текущий с вращающимся значком, будущие приглушены.</summary>
    private sealed class StepList
    {
        public readonly StackPanel View = new();
        private readonly List<(Border Dot, TextBlock Text)> _rows = new();
        private int _active = -1;

        public StepList()
        {
            foreach (var name in StepNames)
            {
                var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 10, 0) }
                    .Ref(Border.BorderBrushProperty, "Border");
                var text = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted");
                var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                r.Children.Add(dot);
                r.Children.Add(text);
                View.Children.Add(r);
                _rows.Add((dot, text));
            }
        }

        /// <summary>Этап index текущий: все до него выполнены. index = число этапов — все выполнены.</summary>
        public void Activate(int index)
        {
            if (index <= _active) return;
            for (int i = Math.Max(0, _active); i < _rows.Count && i < index; i++) MarkDone(i);
            _active = index;
            if (index < _rows.Count) MarkActive(index);
        }

        private void MarkDone(int i)
        {
            var (dot, text) = _rows[i];
            dot.BorderThickness = new Thickness(0);
            dot.SetResourceReference(Border.BackgroundProperty, "OkSoft");
            var icon = new Icon { Kind = "check", Size = 12, StrokeWidth = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Ref(Icon.ForegroundProperty, "Ok");
            dot.Child = icon;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            Appear(icon, 0, 220);
        }

        private void MarkActive(int i)
        {
            var (dot, text) = _rows[i];
            dot.BorderThickness = new Thickness(0);
            dot.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            var rotate = new RotateTransform();
            var icon = new Icon
            {
                Kind = "refresh-cw", Size = 12, StrokeWidth = 2.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rotate,
            }.Ref(Icon.ForegroundProperty, "Accent");
            dot.Child = icon;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            text.FontWeight = FontWeights.SemiBold;
            if (Ui.Animate) rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            if (i > 0) _rows[i - 1].Text.FontWeight = FontWeights.Normal;
        }
    }

    // ---------- Описание версии ----------

    /// <summary>
    /// Описание из релиза или быстрого исправления. Понимает простую разметку: «##» и «###» — заголовки,
    /// «- » и «1. » — пункты, **жирный**, `код` и [ссылки](адрес). Раздел «Как скачать» и подпись автора
    /// внутри программы не нужны и пропускаются. Строки появляются одна за другой.
    /// </summary>
    internal static StackPanel Notes(string markdown)
    {
        var panel = new StackPanel();
        int shown = 0;
        bool first = true;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith('#') && t.TrimStart('#', ' ').StartsWith("Как скачать", StringComparison.OrdinalIgnoreCase)) break;
            if (t == "under2ker" || t.StartsWith("Полный список в")) continue;
            FrameworkElement el;
            if (t.StartsWith("###"))
                el = new TextBlock { Text = t.TrimStart('#', ' ').ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 14, 0, 6) }
                    .Ref(TextBlock.ForegroundProperty, "Accent");
            else if (t.StartsWith('#'))
                el = new TextBlock { Text = t.TrimStart('#', ' '), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, first ? 0 : 14, 0, 6) };
            else if (Regex.Match(t, @"^(?:[-*]|\d+\.)\s+(.*)$") is { Success: true } m)
                el = Bullet(m.Groups[1].Value);
            else
                el = Para(t);
            first = false;
            panel.Children.Add(el);
            if (shown < 14) Appear(el, 6, 240, 60 + shown * 35);
            shown++;
            if (panel.Children.Count > 120) break;
        }
        if (panel.Children.Count == 0) panel.Children.Add(Para("Описание этой версии есть на странице релиза."));
        return panel;
    }

    private static FrameworkElement Bullet(string text)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 7, 0, 0) }
            .Ref(Shape.FillProperty, "Accent"));
        var tb = Rich(text);
        Grid.SetColumn(tb, 1);
        g.Children.Add(tb);
        return g;
    }

    private static TextBlock Para(string text) => Rich(text).With(t => t.Margin = new Thickness(0, 2, 0, 6));

    /// <summary>Строка с **жирным** (цвет основного текста), `кодом` (моноширинный на плашке) и [ссылками](адрес).</summary>
    internal static TextBlock Rich(string text)
    {
        var tb = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 }.Ref(TextBlock.ForegroundProperty, "Muted");
        int pos = 0;
        foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|`(.+?)`|\[([^\]]+)\]\(([^)\s]+)\)"))
        {
            if (m.Index > pos) tb.Inlines.Add(new Run(text[pos..m.Index]));
            if (m.Groups[1].Success)
                tb.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold }.With(r => r.SetResourceReference(TextElement.ForegroundProperty, "Text")));
            else if (m.Groups[2].Success)
                tb.Inlines.Add(new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 }
                    .With(r => { r.SetResourceReference(TextElement.ForegroundProperty, "Text"); r.SetResourceReference(TextElement.BackgroundProperty, "AccentSoft"); }));
            else
            {
                var url = m.Groups[4].Value;
                var link = new Hyperlink(new Run(m.Groups[3].Value)) { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                    link.Click += (_, _) => AutoStart.OpenUrl(u.AbsoluteUri);
                tb.Inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) tb.Inlines.Add(new Run(text[pos..]));
        return tb;
    }

    /// <summary>Убирает разметку Markdown, которую не показываем: заголовки, жирный, код.</summary>
    internal static string CleanNotes(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", "").TrimEnd())
            .Where(l => l.Length > 0);
        var text = string.Join(Environment.NewLine, lines);
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}
