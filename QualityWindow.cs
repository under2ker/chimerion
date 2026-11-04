using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «График качества»: сравнение серверов таблицей (строка выбирает сервер), карточки выбранного и две дорожки с общей осью времени.
/// Верхняя: медиана ответа по столбцам и разброс. Нижняя: доля проверок без ответа. Столбец без проверок серый и рвет линию,
/// значения между измерениями не придумываются. Данные из истории проверок, которую программа уже хранит: до 14 дней и 336 точек.
/// </summary>
internal sealed class QualityWindow
{
    private const double ChartHeight = 290, Left = 62, Right = 8, PlotTop = 10, PlotH = 168, FailTop = 196, FailH = 56, LabelTop = 266;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static QualityWindow? _open;
    private readonly Window _w;
    private readonly List<DnsServer> _candidates = new();
    private readonly StackPanel _rows = new();
    private readonly Grid _tiles = new();
    private readonly TextBlock _period = Ui.Hint("", 0);
    private readonly Canvas _chart = new() { Height = ChartHeight, ClipToBounds = true };
    private readonly CheckBox _uniform = new() { Content = "Единый масштаб", VerticalAlignment = VerticalAlignment.Center, ToolTip = "Одна шкала для всех серверов: так их удобно сравнивать" };
    private DnsServer? _current;
    private int _days = 7;

    public static void Show(Window? owner, IReadOnlyList<DnsServer> servers, DnsServer? first)
    {
        if (_open != null)
        {
            if (first != null)
            {
                if (!_open._candidates.Contains(first)) _open._candidates.Insert(0, first);
                _open._current = first;
                _open.Render();
            }
            _open._w.Activate();
            return;
        }
        _open = new QualityWindow(servers, first);
        _open._w.Show();
    }

    private QualityWindow(IReadOnlyList<DnsServer> servers, DnsServer? first)
    {
        _w = DnexDialog.CreateWindow("График качества", 920);
        _w.Closed += (_, _) => _open = null;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };
        _w.MaxHeight = Ui.WorkAreaOf(null).Height - 40;

        // Кандидаты: выбранный сервер, подключенный, избранные, затем с самой длинной историей
        void Add(DnsServer? s) { if (s != null && !s.IsJunk && !_candidates.Contains(s)) _candidates.Add(s); }
        // Сервер, который человек выбрал сам, показывается всегда, даже если программа считает его мусором
        if (first != null) _candidates.Add(first);
        Add(servers.FirstOrDefault(s => s.IsConnected));
        foreach (var s in servers.Where(s => s.IsFavorite)) Add(s);
        foreach (var s in servers.Where(s => s.HistoryCount >= 3).OrderByDescending(s => s.HistoryCount).Take(30)) Add(s);
        _current = _candidates.FirstOrDefault();

        var body = new StackPanel();
        var controls = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_uniform, Dock.Right);
        _uniform.Click += (_, _) => DrawChart();
        controls.Children.Add(_uniform);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, days) in new[] { ("24 часа", 1), ("7 дней", 7), ("14 дней", 14) })
        {
            var rb = new RadioButton { Content = label, GroupName = "quality-days", IsChecked = days == _days, Style = (Style)Application.Current.Resources["Tab"], Margin = new Thickness(0, 0, 6, 0) };
            rb.Checked += (_, _) => { _days = days; Render(); };
            tabs.Children.Add(rb);
        }
        controls.Children.Add(tabs);
        body.Children.Add(controls);

        body.Children.Add(Ui.Section("Серверы"));
        var list = new StackPanel();
        list.Children.Add(ListHeader());
        list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) }.Ref(Border.BackgroundProperty, "Border"));
        list.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 196, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(Ui.Card(list, bottom: 0).With(c => c.Padding = new Thickness(12)));

        body.Children.Add(Ui.Section("Выбранный сервер"));
        foreach (var w in new[] { 3.0, 3, 2, 2 }) _tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        _tiles.Margin = new Thickness(0, 0, -8, 12);
        body.Children.Add(_tiles);
        var card = new StackPanel();
        card.Children.Add(_period.With(t => { t.FontSize = 12; t.TextWrapping = TextWrapping.Wrap; }));
        card.Children.Add(_chart.With(c => c.Margin = new Thickness(0, 8, 0, 0)));
        card.Children.Add(Legend());
        body.Children.Add(Ui.Card(card, bottom: 0).With(c => c.Padding = new Thickness(16, 14, 16, 12)));

        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        var header = DnexDialog.Header(DialogKind.Info, "График качества",
            "Как серверы отвечали при проверках программы: время ответа и сбои", glyph: "activity");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var footer = Ui.Footer(new StackPanel(), Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true));
        footer.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        _w.Content = root;

        _chart.SizeChanged += (_, _) => DrawChart();
        Render();
    }

    private static Brush Pal(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Color Tint(string key) => (Application.Current?.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;

    private static CultureInfo Culture => Loc.En ? CultureInfo.InvariantCulture : Ru;

    private static string D(DateTime t) => t.ToString("d MMM", Culture);

    private static string Stamp(DateTime t) => t.ToString("dd.MM HH:mm", Culture);

    private static string Zone()
    {
        var o = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        return "UTC" + (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(o.Minutes == 0 ? "%h" : @"h\:mm");
    }

    private static string Percent(QualitySummary s) => QualityStats.RoundRate(s).ToString("0.0", Culture) + "%";

    private static string Ms(long? v) => v is { } x ? Loc.T($"{x} мс") : "—";

    /// <summary>Чем различаются серверы с одним названием: адрес и тип.</summary>
    private static string Address(DnsServer s) =>
        Uri.TryCreate(s.DohUrl?.Split('{')[0], UriKind.Absolute, out var u) ? u.Host : s.Primary;

    private static string Kind(DnsServer s) => string.IsNullOrEmpty(s.DohUrl) ? "DNS" : "DoH";

    private QualitySummary Sum(DnsServer s) => QualityStats.Summarize(s.History, DateTime.Now, _days);

    private static Grid Columns(params double[] widths)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private static readonly double[] ListColumns = { 0, 54, 96, 76, 76, 110 };

    private static TextBlock Cell(string text, int column, string color = "Text", double size = 13)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.Ref(TextBlock.ForegroundProperty, color);
        Grid.SetColumn(t, column);
        return t;
    }

    private static Grid ListHeader()
    {
        var g = Columns(ListColumns);
        g.Margin = new Thickness(8, 0, 8, 0);
        var names = new[] { "Сервер", "Тип", "Доступность", "Медиана", "P95", "Последняя" };
        for (int i = 0; i < names.Length; i++) g.Children.Add(Cell(names[i], i, "Muted", 12));
        return g;
    }

    private FrameworkElement ServerRow(DnsServer s)
    {
        var sum = Sum(s);
        var g = Columns(ListColumns);
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = DnsServer.Clip(s.DisplayName, 34), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        name.Children.Add(new TextBlock { Text = Address(s), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }.Ref(TextBlock.ForegroundProperty, "Muted"));
        g.Children.Add(name);
        g.Children.Add(Cell(Kind(s), 1, "Muted", 12));
        g.Children.Add(Cell(sum.Count == 0 ? "—" : Percent(sum), 2, sum.Count == 0 ? "Muted" : QualityStats.Grade(QualityStats.RoundRate(sum))));
        g.Children.Add(Cell(Ms(sum.MedianMs), 3));
        g.Children.Add(Cell(Ms(sum.P95Ms), 4));
        g.Children.Add(Cell(sum.Last is { } last ? Stamp(last) : "нет проверок", 5, "Muted", 12));
        var row = new Border { Child = g, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2),
            ToolTip = s.DisplayName + " · " + Address(s), Background = Brushes.Transparent };
        if (s == _current) row.Ref(Border.BackgroundProperty, "AccentSoft");
        row.MouseLeftButtonUp += (_, _) => { _current = s; Render(); };
        return row;
    }

    private void Render()
    {
        _rows.Children.Clear();
        _tiles.Children.Clear();
        _chart.Children.Clear();
        foreach (var s in _candidates) _rows.Children.Add(ServerRow(s));
        if (_current == null)
        {
            _period.Text = "Пока нет серверов с историей проверок. Запустите проверку списка";
            return;
        }
        var sum = Sum(_current);
        RenderTiles(sum);
        int hours = QualityStats.BucketHours(_days);
        _period.Text = sum.Count == 0
            ? "За этот период проверок нет: это отсутствие данных, а не сбой"
            : Loc.T($"Проверок {sum.Count}, первая {Stamp(sum.First!.Value)}, последняя {Stamp(sum.Last!.Value)} ({Zone()}). Столбец {hours} ч: проверки внутри него сведены в одну точку");
        DrawChart();
    }

    private void RenderTiles(QualitySummary sum)
    {
        void Add(int col, string label, string value, string sub, string color, double size)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = label, FontSize = 12 }.Ref(TextBlock.ForegroundProperty, "Muted"));
            body.Children.Add(new TextBlock { Text = value, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Ref(TextBlock.ForegroundProperty, color));
            body.Children.Add(new TextBlock { Text = sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }.Ref(TextBlock.ForegroundProperty, "Muted"));
            var tile = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 8, 0), Child = body }
                .Ref(Border.BackgroundProperty, "Surface2");
            Grid.SetColumn(tile, col);
            _tiles.Children.Add(tile);
        }

        if (sum.Count == 0)
        {
            Add(0, "Доступность", "—", "нет проверок за период", "Muted", 26);
            Add(1, "Обычный ответ", "—", "нет проверок за период", "Muted", 26);
            Add(2, "Самая длинная серия", "—", "сбоев подряд", "Muted", 18);
            Add(3, "Когда сбоит", "—", "нет данных", "Muted", 18);
            return;
        }
        Add(0, "Доступность", Percent(sum), Loc.T($"{sum.OkCount} из {sum.Count} проверок, сбоев {sum.Failed}"), QualityStats.Grade(QualityStats.RoundRate(sum)), 26);
        Add(1, "Обычный ответ", Ms(sum.MedianMs),
            sum.P95Ms is { } p ? Loc.T($"медиана, 95% ответов быстрее {p} мс") + (sum.HasSpikes ? ". " + Loc.T("Есть всплески") : "") : "медиана, для P95 мало проверок", sum.MedianMs == null ? "Muted" : "Text", 26);
        Add(2, "Самая длинная серия", sum.MaxRun.ToString(CultureInfo.InvariantCulture),
            sum.MaxRun == 0 ? "сбоев не было" : Loc.T($"сбоев подряд, сейчас {sum.CurrentRun}"), sum.CurrentRun > 0 ? "Bad" : sum.MaxRun >= 2 ? "Warn" : "Ok", 18);
        if (sum.Failed == 0) Add(3, "Когда сбоит", "сбоев нет", "за выбранный период", "Ok", 18);
        else if (sum.WorstPart is { } part) Add(3, "Когда сбоит", part.Split(' ')[0], Loc.T($"{sum.WorstPercent}% проверок не прошли"), "Bad", 18);
        else Add(3, "Когда сбоит", "в разное время", "закономерности нет", "Text", 18);
    }

    private static FrameworkElement Legend()
    {
        var p = new WrapPanel { Margin = new Thickness(Left, 6, 0, 0) };
        void Item(string text, Action<Border> tune)
        {
            var mark = new Border { Width = 14, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            tune(mark);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 16, 2) };
            row.Children.Add(mark);
            row.Children.Add(new TextBlock { Text = text, FontSize = 12 }.Ref(TextBlock.ForegroundProperty, "Muted"));
            p.Children.Add(row);
        }
        Item("медиана ответа", m => { m.Height = 3; m.Ref(Border.BackgroundProperty, "Accent"); });
        Item("от быстрого до медленного", m => m.Background = new SolidColorBrush(Color.FromArgb(120, Tint("Accent").R, Tint("Accent").G, Tint("Accent").B)));
        Item("сбои: менее 20%", m => m.Ref(Border.BackgroundProperty, "Warn"));
        Item("сбои: от 20%", m => m.Ref(Border.BackgroundProperty, "Bad"));
        Item("нет проверок", m => m.Ref(Border.BackgroundProperty, "Surface2"));
        return p;
    }

    /// <summary>Подсказка столбца: когда, сколько проверок, разброс и точное время сбоев.</summary>
    private static string Tip(QualityBucket b)
    {
        var head = $"{D(b.Start)}, {b.Start:HH:mm}–{b.End:HH:mm} ({Zone()})";
        if (b.Count == 0) return head + "\n" + Loc.T("проверок не было");
        var lines = new List<string> { head, Loc.T($"проверок {b.Count}, без ответа {b.Failed}") };
        if (b.MedianMs is { } m) lines.Add(Loc.T($"медиана {m} мс, от {b.MinMs} до {b.MaxMs} мс"));
        if (b.Failed > 0)
            lines.Add(Loc.T("сбои:") + " " + string.Join(", ", b.FailTimes.Take(6).Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture))) + (b.Failed > 6 ? "…" : ""));
        return string.Join("\n", lines);
    }

    private TextBlock Text(string text, double x, double y)
    {
        var t = new TextBlock { Text = text, FontSize = 12 }.Ref(TextBlock.ForegroundProperty, "Muted");
        Canvas.SetLeft(t, x); Canvas.SetTop(t, y);
        _chart.Children.Add(t);
        return t;
    }

    private void Rect(double x, double y, double w, double h, Brush fill, double radius = 2)
    {
        var r = new Rectangle { Width = w, Height = Math.Max(1, h), RadiusX = radius, RadiusY = radius, Fill = fill };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        _chart.Children.Add(r);
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        if (_current == null || _chart.ActualWidth < 200) return;
        var now = DateTime.Now;
        double w = _chart.ActualWidth;
        int hours = QualityStats.BucketHours(_days);
        var buckets = QualityStats.Buckets(_current.History, now, hours, now.AddDays(-_days));
        if (buckets.Count == 0) return;
        double colW = (w - Left - Right) / buckets.Count;
        double X(int i) => Left + colW * (i + 0.5);

        // Шкала по P95, а не по максимуму: один выброс не должен сплющивать график. Выбросы отмечены треугольником сверху
        var scope = _uniform.IsChecked == true ? _candidates : new List<DnsServer> { _current };
        long top = QualityStats.NiceTop((long)(scope.Select(Sum).Select(x => Math.Max(x.P95Ms ?? 0, x.MedianMs ?? 0)).DefaultIfEmpty(0).Max() * 1.15));
        double Y(long ms) => PlotTop + PlotH * (1 - Math.Clamp(ms / (double)top, 0, 1));
        double baseY = PlotTop + PlotH;
        var accent = Tint("Accent");

        // Столбцы без проверок на обеих дорожках: отсутствие данных не похоже на сбой
        for (int i = 0; i < buckets.Count; i++)
            if (buckets[i].Count == 0)
            {
                Rect(Left + colW * i, PlotTop, colW, PlotH, Pal("Surface2"), 0);
                Rect(Left + colW * i, FailTop, colW, FailH, Pal("Surface2"), 0);
            }

        // Сетка и подписи шкал
        foreach (var v in new[] { 0L, top / 2, top })
        {
            _chart.Children.Add(new Line { X1 = Left, X2 = w - Right, Y1 = Y(v), Y2 = Y(v), Stroke = Pal("Border"), StrokeThickness = 1 });
            Text(Loc.T($"{v} мс"), 0, Y(v) - 8);
        }
        foreach (var (pct, y) in new[] { (100, FailTop), (0, FailTop + FailH) })
        {
            _chart.Children.Add(new Line { X1 = Left, X2 = w - Right, Y1 = y, Y2 = y, Stroke = Pal("Border"), StrokeThickness = 1 });
            Text($"{pct}%", 0, y - 8);
        }
        Text("Доля сбоев", Left, FailTop - 17);

        // Подписи времени без наложения
        double lastRight = -1;
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            if (b.Start.Hour == 0 && hours < 24)
                _chart.Children.Add(new Line { X1 = Left + colW * i, X2 = Left + colW * i, Y1 = PlotTop, Y2 = FailTop + FailH, Stroke = Pal("Border"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            var label = b.Start.Hour == 0 ? D(b.Start) : hours == 1 && b.Start.Hour % 4 == 0 ? b.Start.ToString("HH:mm", CultureInfo.InvariantCulture) : null;
            if (label == null) continue;
            var t = Text(label, 0, LabelTop);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lx = Math.Max(Left, Left + colW * i - t.DesiredSize.Width / 2);
            if (lx < lastRight + 8) { _chart.Children.Remove(t); continue; }
            Canvas.SetLeft(t, lx);
            lastRight = lx + t.DesiredSize.Width;
        }

        // Разброс внутри столбца
        double barW = Math.Max(3, colW * 0.34);
        var spread = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B));
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            if (b.MinMs is not > 0 || b.MaxMs is not > 0) continue;
            double y1 = Y(b.MaxMs!.Value), y2 = Y(b.MinMs!.Value);
            Rect(X(i) - barW / 2, y1, barW, Math.Max(2, y2 - y1), spread);
            if (b.MaxMs > top)
                _chart.Children.Add(new Polygon { Fill = Pal("Warn"), Points = new PointCollection { new(X(i), PlotTop - 6), new(X(i) - 4, PlotTop + 1), new(X(i) + 4, PlotTop + 1) } });
        }

        // Линия медианы: прямые отрезки между соседними столбцами с данными, разрыв там, где измерений нет
        foreach (var seg in QualityStats.Segments(buckets))
        {
            var pts = seg.Select(i => new Point(X(i), Y(buckets[i].MedianMs!.Value))).ToList();
            if (pts.Count >= 2)
            {
                var fill = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(70, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90) };
                foreach (var p in pts) fill.Points.Add(p);
                fill.Points.Add(new Point(pts[^1].X, baseY));
                fill.Points.Add(new Point(pts[0].X, baseY));
                _chart.Children.Add(fill);
                _chart.Children.Add(new Polyline { Points = new PointCollection(pts), Stroke = Pal("Accent"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
            }
            foreach (var p in pts)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = Pal("Accent") };
                Canvas.SetLeft(dot, p.X - 3); Canvas.SetTop(dot, p.Y - 3);
                _chart.Children.Add(dot);
            }
        }

        // Дорожка сбоев
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            if (b.Failed == 0) continue;
            double h = Math.Max(3, FailH * b.Failed / b.Count);
            Rect(X(i) - barW / 2, FailTop + FailH - h, barW, h, Pal(b.FailPercent >= 20 ? "Bad" : "Warn"));
        }

        // Невидимые столбцы для подсказок
        for (int i = 0; i < buckets.Count; i++)
        {
            var hit = new Rectangle { Width = colW, Height = FailTop + FailH - PlotTop, Fill = Brushes.Transparent, ToolTip = Tip(buckets[i]) };
            ToolTipService.SetInitialShowDelay(hit, 100);
            Canvas.SetLeft(hit, Left + colW * i); Canvas.SetTop(hit, PlotTop);
            _chart.Children.Add(hit);
        }
    }
}
