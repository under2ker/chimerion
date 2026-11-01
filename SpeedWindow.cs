using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Тест скорости»: кто вы в сети (IP, провайдер, локация, DNS), подключенный Smart DNS, скорость интернета и скорость DNS.
/// В режиме скрытия данных личные значения закрыты, пока человек не нажмет «Показать». Открытие окна ничего не скачивает:
/// замер начинается по кнопке.
/// </summary>
internal sealed class SpeedWindow
{
    private const double GaugeMax = 1000, GaugeSweep = 270;

    private static SpeedWindow? _open;
    private readonly Window _w;
    private readonly AppSettings _settings;
    private readonly DnsServer? _server;
    private readonly Action<string> _log;
    private readonly StackPanel _you = new(), _dns = new();
    private readonly Gauge _down = new("Скачивание"), _up = new("Отправка");
    private readonly TextBlock _ping = Value("—"), _jitter = Value("—"), _source = Small("—");
    private readonly TextBlock _dnsMedian = Value("—"), _dnsSlow = Value("—"), _dnsRate = Value("—"), _dnsVerdict = Small("Подключите Smart DNS, чтобы измерить его скорость");
    private readonly ProgressBar _bar = new() { Height = 4, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _state = Small("Замер скачает и отправит до нескольких сотен мегабайт");
    private readonly Button _run;
    private readonly Button _show;
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

    private SpeedWindow(AppSettings settings, DnsServer? server, NetSnapshot? snapshot, Action<string> log)
    {
        _settings = settings; _server = server; _snap = snapshot; _log = log;
        _w = DnexDialog.CreateWindow("Тест скорости", 900);
        _w.MaxHeight = SystemParameters.WorkArea.Height - 40;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };
        _w.Closed += (_, _) => { _cts?.Cancel(); _open = null; };

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
        foreach (var w in new[] { 1.0, 1, 0.8 }) speed.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
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
        speedBody.Children.Add(speed);
        speedBody.Children.Add(_bar);
        body.Children.Add(Ui.Card(speedBody, bottom: 0).With(c => c.Padding = new Thickness(16, 12, 16, 14)));

        body.Children.Add(Ui.Section("Скорость DNS"));
        var dnsSpeed = new UniformGrid { Columns = 3 };
        foreach (var (value, label) in new[] { (_dnsMedian, "обычный ответ"), (_dnsSlow, "самые медленные"), (_dnsRate, "запросов в секунду") })
        {
            var cell = new StackPanel();
            cell.Children.Add(value);
            cell.Children.Add(Small(label));
            dnsSpeed.Children.Add(cell);
        }
        var dnsBody = new StackPanel();
        dnsBody.Children.Add(dnsSpeed);
        dnsBody.Children.Add(_dnsVerdict.With(t => t.Margin = new Thickness(0, 10, 0, 0)));
        body.Children.Add(Ui.Card(dnsBody, bottom: 0).With(c => c.Padding = new Thickness(16, 12, 16, 12)));

        _run = Ui.Action("Запустить тест", "Primary", () => _ = ToggleAsync());
        var footer = Ui.Footer(_state, _run, Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true));
        footer.Margin = new Thickness(0, 14, 0, 0);

        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        var header = DnexDialog.Header(DialogKind.Info, "Тест скорости", "Ваше подключение, скорость интернета и скорость подключенного DNS", glyph: "zap");
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
    private string Private(string? value) => string.IsNullOrEmpty(value) ? "—" : PrivacyGuard.Enabled && !_reveal ? "••••••" : value;

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

    private async Task ToggleAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        using var cts = _cts = new CancellationTokenSource();
        _run.Content = Loc.T("Остановить");
        _bar.Visibility = Visibility.Visible;
        _down.Set(0, false); _up.Set(0, false);
        _ping.Text = _jitter.Text = _source.Text = "—";
        var labels = new Dictionary<SpeedPhase, string> { [SpeedPhase.Ping] = "Измеряю пинг…", [SpeedPhase.Download] = "Измеряю скачивание…", [SpeedPhase.Upload] = "Измеряю отправку…" };
        var progress = new Progress<SpeedProgress>(p =>
        {
            _state.Text = Loc.T(labels[p.Phase]);
            _bar.Value = p.Fraction;
            if (p.Phase == SpeedPhase.Download) _down.Set(p.Mbit, true);
            else if (p.Phase == SpeedPhase.Upload) { _down.Done(); _up.Set(p.Mbit, true); }
        });
        try
        {
            _state.Text = Loc.T("Выбираю сервер замера…");
            ShowInternet(await InternetSpeed.RunAsync(progress, cts.Token));
            await MeasureDnsAsync(cts.Token);
            _state.Text = Loc.T("Готово");
        }
        catch (OperationCanceledException) { _state.Text = Loc.T("Тест остановлен"); }
        catch (Exception e)
        {
            _state.Text = Loc.T("Тест не удался, подробности в журнале");
            _log("Тест скорости не удался: " + e.Message);
        }
        _cts = null;
        if (_open != this) return;
        _bar.Visibility = Visibility.Collapsed;
        _run.Content = Loc.T("Запустить тест");
    }

    private void ShowInternet(InternetResult r)
    {
        _down.Set(r.DownMbit ?? 0, false);
        _up.Set(r.UpMbit ?? 0, false);
        if (r.DownMbit == null) _down.Fail();
        if (r.UpMbit == null) _up.Fail();
        _ping.Text = r.PingMs is { } p ? Loc.T($"{p} мс") : "—";
        _jitter.Text = r.JitterMs is { } j ? Loc.T($"{j} мс") : "—";
        _source.Text = r.Source ?? Loc.T("не ответил ни один");
        _log($"Тест скорости: скачивание {r.DownMbit:0.#} Мбит/с, отправка {r.UpMbit:0.#} Мбит/с, пинг {r.PingMs} мс, сервер {r.Source}");
    }

    private async Task MeasureDnsAsync(CancellationToken ct)
    {
        if (_server is not { } s) return;
        _state.Text = Loc.T("Измеряю скорость DNS…");
        var checker = new ServerChecker(_settings);
        ShowDns(await SpeedTest.MeasureAsync((host, token) => checker.ResolveViaAsync(s, host, token), ct));
    }

    private void ShowDns(SpeedResult r)
    {
        bool ok = r.Ok > 0;
        _dnsMedian.Text = ok ? Loc.T($"{r.MedianMs} мс") : "—";
        _dnsSlow.Text = ok ? Loc.T($"{r.SlowMs} мс") : "—";
        _dnsRate.Text = ok ? r.QueriesPerSecond.ToString("0.#", CultureInfo.InvariantCulture) : "—";
        _dnsVerdict.Text = Loc.T($"Оценка: {r.Grade}, ответов {r.Ok} из {r.Total}");
    }

    /// <summary>Круговая шкала: дуга на 270 градусов, логарифмическая, потому что скорости отличаются на порядки.</summary>
    private sealed class Gauge
    {
        private const double Size = 170, Radius = 72, Thickness = 11;
        private readonly Path _arc = new() { StrokeThickness = Thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly TextBlock _value = new() { FontSize = 30, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Text = "—" };
        public FrameworkElement View { get; }

        public Gauge(string title)
        {
            _arc.SetResourceReference(Shape.StrokeProperty, "Accent");
            var track = new Path { StrokeThickness = Thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = ArcGeometry(1) };
            track.SetResourceReference(Shape.StrokeProperty, "Border");
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(_value);
            center.Children.Add(new TextBlock { Text = "Мбит/с", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted"));
            var host = new Grid { Width = Size, Height = Size - 14 };
            host.Children.Add(new Canvas { Children = { track, _arc } });
            host.Children.Add(center);
            var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            root.Children.Add(host);
            root.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            View = root;
        }

        private static Geometry ArcGeometry(double share)
        {
            const double start = 135;
            Point At(double deg) => new(Size / 2 + Radius * Math.Cos(deg * Math.PI / 180), Size / 2 + Radius * Math.Sin(deg * Math.PI / 180));
            var figure = new PathFigure { StartPoint = At(start) };
            double sweep = Math.Clamp(share, 0.001, 1) * GaugeSweep;
            figure.Segments.Add(new ArcSegment(At(start + sweep), new Size(Radius, Radius), 0, sweep > 180, SweepDirection.Clockwise, true));
            return new PathGeometry { Figures = { figure } };
        }

        public void Set(double mbit, bool live)
        {
            double share = mbit <= 0 ? 0 : Math.Log10(1 + mbit) / Math.Log10(1 + GaugeMax);
            _arc.Data = mbit <= 0 ? Geometry.Empty : ArcGeometry(share);
            _arc.Opacity = live ? 0.75 : 1;
            _value.Text = mbit <= 0 ? "—" : mbit >= 100 ? mbit.ToString("0", CultureInfo.InvariantCulture) : mbit.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public void Done() => _arc.Opacity = 1;

        public void Fail() => _value.Text = "—";
    }
}
