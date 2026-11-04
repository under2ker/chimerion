using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Тест скорости»: кто вы в сети (IP, провайдер, локация, DNS), подключенный Smart DNS, скорость интернета и скорость DNS.
/// Ход замера виден дорожкой шагов, шкалы плавно доезжают до значения, итоги появляются с проявлением. В режиме скрытия данных
/// личные значения закрыты, пока человек не нажмет «Показать». Открытие окна ничего не скачивает: замер по кнопке.
/// </summary>
internal sealed class SpeedWindow
{
    private static SpeedWindow? _open;
    private readonly Window _w;
    private readonly AppSettings _settings;
    private readonly DnsServer? _server;
    private readonly Action<string> _log;
    private readonly StackPanel _you = new(), _dns = new(), _uses = new();
    private readonly Gauge _down = new("Скачивание"), _up = new("Отправка");
    private readonly TextBlock _ping = Value("—"), _jitter = Value("—"), _source = Small("—");
    private readonly TextBlock _dnsMedian = Value("—"), _dnsSlow = Value("—"), _dnsRate = Value("—"), _dnsGrade = Value("—"), _dnsNote = Small("Подключите Smart DNS, чтобы измерить его скорость");
    private readonly ProgressBar _bar = new() { Height = 4, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Hidden };
    private readonly TextBlock _state = Small("Замер скачает и отправит до нескольких сотен мегабайт");
    private readonly Steps _steps = new("Пинг", "Скачивание", "Отправка", "DNS");
    private readonly Button _run, _show;
    private NetSnapshot? _snap;
    private bool _reveal;
    private CancellationTokenSource? _cts;

    public static void Show(AppSettings settings, DnsServer? server, NetSnapshot? snapshot, Action<string> log)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new SpeedWindow(settings, server, snapshot, log);
        _open._w.Show();
    }

    private static TextBlock Value(string s) => new TextBlock { Text = s, FontSize = 22, FontWeight = FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, "Text");

    private static TextBlock Small(string s) => new TextBlock { Text = s, FontSize = 12, TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, "Muted");

    /// <summary>Новое значение появляется плавно: короткое проявление и сдвиг снизу.</summary>
    private static void Reveal(TextBlock t, string text, string? color = null)
    {
        if (t.Text == text && color == null) return;
        t.Text = text;
        if (color != null) t.SetResourceReference(TextBlock.ForegroundProperty, color);
        var shift = new TranslateTransform(0, 6);
        t.RenderTransform = shift;
        t.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ui.Motion(260)));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Ui.Motion(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private SpeedWindow(AppSettings settings, DnsServer? server, NetSnapshot? snapshot, Action<string> log)
    {
        _settings = settings; _server = server; _snap = snapshot; _log = log;
        _w = DnexDialog.CreateWindow("Тест скорости", 900);
        _w.MaxHeight = Ui.WorkAreaOf(null).Height - 40;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };
        _w.Closed += (_, _) => { _cts?.Cancel(); _down.Stop(); _up.Stop(); _open = null; };

        var body = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        _show = Ui.Action("Показать", "Ghost", () => { _reveal = !_reveal; RenderInfo(); }).With(b => { b.Padding = new Thickness(10, 3, 10, 3); b.MinHeight = 0; b.Margin = new Thickness(0); });
        var you = new StackPanel();
        var youHead = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_show, Dock.Right);
        youHead.Children.Add(_show);
        youHead.Children.Add(new TextBlock { Text = "Ваше подключение", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        you.Children.Add(youHead);
        you.Children.Add(_you);
        top.Children.Add(Ui.Card(you, bottom: 0).With(c => c.Padding = new Thickness(16, 12, 16, 12)));
        var dns = new StackPanel();
        dns.Children.Add(new TextBlock { Text = "Подключенный DNS", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 11) });
        dns.Children.Add(_dns);
        var dnsCard = Ui.Card(dns, bottom: 0).With(c => c.Padding = new Thickness(16, 12, 16, 12));
        Grid.SetColumn(dnsCard, 2);
        top.Children.Add(dnsCard);
        body.Children.Add(top);

        body.Children.Add(Ui.Section("Скорость интернета"));
        var speed = new Grid();
        foreach (var w in new[] { 1.0, 1, 0.85 }) speed.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        speed.Children.Add(_down.View);
        Grid.SetColumn(_up.View, 1);
        speed.Children.Add(_up.View);
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        side.Children.Add(Small("Пинг"));
        side.Children.Add(_ping);
        side.Children.Add(Small("Джиттер").With(t => t.Margin = new Thickness(0, 12, 0, 0)));
        side.Children.Add(_jitter);
        side.Children.Add(Small("Сервер замера").With(t => t.Margin = new Thickness(0, 12, 0, 0)));
        side.Children.Add(_source);
        Grid.SetColumn(side, 2);
        speed.Children.Add(side);
        var speedBody = new StackPanel();
        speedBody.Children.Add(_steps.View);
        speedBody.Children.Add(speed.With(g => g.Margin = new Thickness(0, 10, 0, 0)));
        speedBody.Children.Add(_bar);
        speedBody.Children.Add(_uses.With(p => p.Margin = new Thickness(0, 12, 0, 0)));
        body.Children.Add(Ui.Card(speedBody, bottom: 0).With(c => c.Padding = new Thickness(16, 14, 16, 14)));
        RenderUses(null);

        body.Children.Add(Ui.Section("Скорость DNS"));
        var dnsSpeed = new UniformGrid { Columns = 4 };
        foreach (var (value, label) in new[] { (_dnsGrade, "оценка"), (_dnsMedian, "обычный ответ"), (_dnsSlow, "самые медленные"), (_dnsRate, "запросов в секунду") })
        {
            var cell = new StackPanel();
            cell.Children.Add(value);
            cell.Children.Add(Small(label));
            dnsSpeed.Children.Add(cell);
        }
        var dnsBody = new StackPanel();
        dnsBody.Children.Add(dnsSpeed);
        dnsBody.Children.Add(_dnsNote.With(t => t.Margin = new Thickness(0, 10, 0, 0)));
        body.Children.Add(Ui.Card(dnsBody, bottom: 0).With(c => c.Padding = new Thickness(16, 12, 16, 12)));

        _run = Ui.Action("Запустить тест", "Primary", () => _ = ToggleAsync());
        var footer = Ui.Footer(_state, _run, Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true));
        footer.Margin = new Thickness(0, 14, 0, 0);

        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        var header = DnexDialog.Header(DialogKind.Info, "Тест скорости", "Ваше подключение, скорость интернета и скорость подключенного DNS", glyph: "gauge");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        _w.Content = root;
        RenderInfo();
        if (_snap == null) _w.Loaded += async (_, _) => await LoadSnapshotAsync();
    }

    private async Task LoadSnapshotAsync()
    {
        _you.Children.Clear();
        _you.Children.Add(Small("Узнаю адрес и провайдера…"));
        try { _snap = await ConnectionProbe.TakeAsync(_settings, light: true); }
        catch (Exception e) { _log("Тест скорости: не удалось узнать адрес: " + e.Message); }
        if (_snap != null) PrivacyGuard.RememberPersonal(_snap.PublicIp, _snap.Location, _snap.Provider);
        RenderInfo();
    }

    private static FrameworkElement Line(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(Small(label));
        var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    /// <summary>Личные значения: в режиме скрытия закрыты, пока не нажата «Показать».</summary>
    private string Private(string? value) => string.IsNullOrEmpty(value) || value == "—" ? "—" : PrivacyGuard.Enabled && !_reveal ? "••••••" : value;

    private void RenderInfo()
    {
        _show.Visibility = PrivacyGuard.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _show.Content = Loc.T(_reveal ? "Скрыть" : "Показать");
        _you.Children.Clear();
        _you.Children.Add(Line("Внешний IP", Private(_snap?.PublicIp)));
        _you.Children.Add(Line("Провайдер", Private(_snap?.Provider)));
        _you.Children.Add(Line("Локация", Private(_snap?.Location)));
        _you.Children.Add(Line("DNS системы", _snap?.SystemDns ?? "—"));

        _dns.Children.Clear();
        if (_server is not { } s)
        {
            _dns.Children.Add(Line("Состояние", "Smart DNS не подключен"));
            _dns.Children.Add(Line("DNS системы", _snap?.SystemDns ?? "—"));
            return;
        }
        var place = string.Join(", ", new[] { s.CountryName, string.IsNullOrEmpty(s.City) ? "" : GeoLocation.CityName(s.City) }.Where(x => !string.IsNullOrEmpty(x)));
        _dns.Children.Add(Line("Сервер", s.DisplayName));
        _dns.Children.Add(Line("Адрес", Uri.TryCreate(s.DohUrl?.Split('{')[0], UriKind.Absolute, out var u) ? u.Host : s.Primary));
        _dns.Children.Add(Line("Тип", string.IsNullOrEmpty(s.DohUrl) ? "обычный DNS" : "DoH"));
        _dns.Children.Add(Line("Локация", place.Length > 0 ? place : "не определена"));
        if (s.ProxyCountryName.Length > 0) _dns.Children.Add(Line("Страна выхода", s.ProxyCountryName));
    }

    /// <summary>На что хватает: строки с отметкой. До замера показываются бледно.</summary>
    private void RenderUses(InternetResult? r)
    {
        _uses.Children.Clear();
        var grid = new UniformGrid { Columns = 4 };
        var uses = SpeedVerdict.For(r ?? new InternetResult(null, null, null, null, null));
        foreach (var use in uses)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = Loc.T("Нужно: ") + Loc.T(use.Need), Opacity = r == null ? 0.45 : 1, Margin = new Thickness(0, 0, 12, 0) };
            var icon = new Controls.Icon { Kind = r == null ? "minus" : use.Ok ? "circle-check" : "circle-x", Size = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Controls.Icon.ForegroundProperty, r == null ? "Muted" : use.Ok ? "Ok" : "Bad");
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = use.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            grid.Children.Add(row);
        }
        _uses.Children.Add(grid);
        if (r != null) _uses.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ui.Motion(300)));
    }

    private async Task ToggleAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        using var cts = _cts = new CancellationTokenSource();
        _run.Content = Loc.T("Остановить");
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = true;
        _down.Reset(); _up.Reset();
        _ping.Text = _jitter.Text = _source.Text = "—";
        _dnsGrade.Text = _dnsMedian.Text = _dnsSlow.Text = _dnsRate.Text = "—";
        RenderUses(null);
        _steps.Reset();
        _state.Text = Loc.T("Выбираю сервер замера…");
        var labels = new Dictionary<SpeedPhase, string> { [SpeedPhase.Ping] = "Измеряю пинг…", [SpeedPhase.Download] = "Измеряю скачивание…", [SpeedPhase.Upload] = "Измеряю отправку…" };
        var progress = new Progress<SpeedProgress>(p =>
        {
            _state.Text = Loc.T(labels[p.Phase]);
            _bar.IsIndeterminate = false;
            _bar.Value = p.Fraction;
            _steps.Active((int)p.Phase);
            _down.Live(p.Phase == SpeedPhase.Download);
            _up.Live(p.Phase == SpeedPhase.Upload);
            if (p.Phase == SpeedPhase.Download) _down.To(p.Mbit);
            else if (p.Phase == SpeedPhase.Upload) _up.To(p.Mbit);
        });
        try
        {
            var r = await InternetSpeed.RunAsync(progress, cts.Token);
            ShowInternet(r);
            if (r.Source == null)
            {
                _steps.Reset();
                _state.Text = Loc.T("Серверы замера недоступны, подробности в журнале");
                throw new OperationCanceledException("нет серверов замера");
            }
            _steps.Active(3);
            await MeasureDnsAsync(cts.Token);
            _steps.Active(4);
            _state.Text = Loc.T($"Готово, замер {DateTime.Now:HH:mm}");
        }
        catch (OperationCanceledException) { if (!cts.IsCancellationRequested) { } else { _state.Text = Loc.T("Тест остановлен"); } _steps.Reset(); }
        catch (Exception e)
        {
            _state.Text = Loc.T("Тест не удался, подробности в журнале");
            _log("Тест скорости не удался: " + e.Message);
            _steps.Reset();
        }
        _cts = null;
        if (_open != this) return;
        _down.Live(false); _up.Live(false);
        _bar.Visibility = Visibility.Hidden;
        _run.Content = Loc.T("Запустить тест");
    }

    private void ShowInternet(InternetResult r)
    {
        if (r.DownMbit is { } d) _down.To(d); else _down.Fail();
        if (r.UpMbit is { } u) _up.To(u); else _up.Fail();
        Reveal(_ping, r.PingMs is { } p ? Loc.T($"{p} мс") : "—");
        Reveal(_jitter, r.JitterMs is { } j ? Loc.T($"{j} мс") : "—");
        Reveal(_source, r.Source ?? Loc.T("не ответил ни один"));
        RenderUses(r.Source == null ? null : r);
        _log(r.Source == null ? $"Тест скорости: серверы замера недоступны ({r.Error})" : $"Тест скорости: скачивание {r.DownMbit:0.#} Мбит/с, отправка {r.UpMbit:0.#} Мбит/с, пинг {r.PingMs} мс, сервер {r.Source}");
    }

    private async Task MeasureDnsAsync(CancellationToken ct)
    {
        if (_server is not { } s) { _dnsNote.Text = Loc.T("Smart DNS не подключен: его скорость не измерялась"); return; }
        _state.Text = Loc.T("Измеряю скорость DNS…");
        _bar.IsIndeterminate = true;
        var checker = new ServerChecker(_settings);
        ShowDns(await SpeedTest.MeasureAsync((host, token) => checker.ResolveViaAsync(s, host, token), ct));
    }

    private void ShowDns(SpeedResult r)
    {
        bool ok = r.Ok > 0;
        Reveal(_dnsGrade, r.Grade, SpeedVerdict.DnsColor(r.Grade));
        Reveal(_dnsMedian, ok ? Loc.T($"{r.MedianMs} мс") : "—");
        Reveal(_dnsSlow, ok ? Loc.T($"{r.SlowMs} мс") : "—");
        Reveal(_dnsRate, ok ? r.QueriesPerSecond.ToString("0.#", CultureInfo.InvariantCulture) : "—");
        _dnsNote.Text = Loc.T($"Ответов {r.Ok} из {r.Total}, сервер {_server?.DisplayName}");
    }

    /// <summary>Дорожка этапов: ожидает, идет (подсвечен и мерцает), готово (галочка).</summary>
    private sealed class Steps
    {
        private readonly (Border Chip, Controls.Icon Mark, TextBlock Text)[] _items;
        public FrameworkElement View { get; }

        public Steps(params string[] titles)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            _items = titles.Select((t, i) =>
            {
                var mark = new Controls.Icon { Kind = "check", Size = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
                mark.SetResourceReference(Controls.Icon.ForegroundProperty, "Ok");
                var text = new TextBlock { Text = t, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(mark);
                row.Children.Add(text);
                var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(0, 0, 6, 0), Child = row, BorderThickness = new Thickness(1) };
                panel.Children.Add(chip);
                return (chip, mark, text);
            }).ToArray();
            View = panel;
            Reset();
        }

        public void Reset() => Active(-1);

        /// <summary>index: идущий этап, все до него готовы. -1: ничего не идет.</summary>
        public void Active(int index)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                var (chip, mark, text) = _items[i];
                bool done = index >= 0 && i < index, now = i == index;
                chip.SetResourceReference(Border.BackgroundProperty, now ? "AccentSoft" : "Surface2");
                chip.SetResourceReference(Border.BorderBrushProperty, now ? "AccentBorder" : "Border");
                text.SetResourceReference(TextBlock.ForegroundProperty, now ? "Accent" : done ? "Text" : "Muted");
                mark.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
                if (now) chip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.6, Ui.Motion(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                else { chip.BeginAnimation(UIElement.OpacityProperty, null); chip.Opacity = 1; }
            }
        }
    }

    /// <summary>
    /// Круговая шкала на 270 градусов, логарифмическая (1, 10, 100, 1000 Мбит/с): скорости отличаются на порядки.
    /// Значение доезжает плавно на каждом кадре. Во время замера дуга светится.
    /// </summary>
    private sealed class Gauge
    {
        private const double Size = 180, Radius = 74, Stroke = 11, Max = 1000, Sweep = 270, Start = 135;
        private readonly Path _arc = new() { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly TextBlock _value = new() { FontSize = 32, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Text = "—" };
        private readonly TextBlock _title;
        private readonly DropShadowEffect _glow = new() { BlurRadius = 18, ShadowDepth = 0, Opacity = 0 };
        private double _shown, _target;
        private bool _ticking, _failed;
        public FrameworkElement View { get; }

        public Gauge(string title)
        {
            _arc.SetResourceReference(Shape.StrokeProperty, "Accent");
            _arc.Effect = _glow;
            var track = new Path { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = Arc(1) };
            track.SetResourceReference(Shape.StrokeProperty, "Border");
            var canvas = new Canvas { Children = { track, _arc } };
            // Подписи шкалы
            foreach (var mark in new[] { 1, 10, 100, 1000 })
            {
                var p = At(Start + Share(mark) * Sweep, Radius - 22);
                var t = new TextBlock { Text = mark.ToString(CultureInfo.InvariantCulture), FontSize = 10 }.Ref(TextBlock.ForegroundProperty, "Muted");
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(t, p.X - t.DesiredSize.Width / 2);
                Canvas.SetTop(t, p.Y - t.DesiredSize.Height / 2);
                canvas.Children.Add(t);
            }
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            center.Children.Add(_value);
            center.Children.Add(new TextBlock { Text = "Мбит/с", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted"));
            var host = new Grid { Width = Size, Height = Size - 16 };
            host.Children.Add(canvas);
            host.Children.Add(center);
            _title = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            root.Children.Add(host);
            root.Children.Add(_title);
            View = root;
            Draw();
        }

        private static double Share(double mbit) => mbit <= 0 ? 0 : Math.Min(1, Math.Log10(1 + mbit) / Math.Log10(1 + Max));

        private static Point At(double deg, double r) => new(Size / 2 + r * Math.Cos(deg * Math.PI / 180), Size / 2 + r * Math.Sin(deg * Math.PI / 180));

        private static Geometry Arc(double share)
        {
            var figure = new PathFigure { StartPoint = At(Start, Radius) };
            double sweep = Math.Clamp(share, 0.001, 1) * Sweep;
            figure.Segments.Add(new ArcSegment(At(Start + sweep, Radius), new Size(Radius, Radius), 0, sweep > 180, SweepDirection.Clockwise, true));
            return new PathGeometry { Figures = { figure } };
        }

        private void Draw()
        {
            _arc.Data = _shown <= 0.05 ? Geometry.Empty : Arc(Share(_shown));
            _value.Text = _failed ? "—" : _shown <= 0.05 ? "0" : _shown >= 100 ? _shown.ToString("0", CultureInfo.InvariantCulture) : _shown.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public void To(double mbit)
        {
            _failed = false;
            _target = Math.Max(0, mbit);
            if (_ticking) return;
            _ticking = true;
            CompositionTarget.Rendering += Tick;
        }

        private void Tick(object? sender, EventArgs e)
        {
            _shown += (_target - _shown) * 0.16;
            if (Math.Abs(_target - _shown) < 0.05) { _shown = _target; Stop(); }
            Draw();
        }

        public void Stop()
        {
            if (!_ticking) return;
            _ticking = false;
            CompositionTarget.Rendering -= Tick;
        }

        public void Live(bool on)
        {
            _title.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Text");
            if (_arc.Stroke is SolidColorBrush b) _glow.Color = b.Color;
            _glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(on ? 0.7 : 0, Ui.Motion(300)));
        }

        public void Reset() { Stop(); _failed = false; _shown = _target = 0; Draw(); }

        public void Fail() { Stop(); _failed = true; _shown = _target = 0; Draw(); }
    }
}
