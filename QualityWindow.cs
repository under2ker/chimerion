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
/// Окно «График качества»: как отвечал сервер за последние 14 дней. Итоги карточками, затем график по интервалам времени:
/// линия показывает обычное время ответа, полоска внутри столбца разброс, полоса снизу доля проверок без ответа.
/// Наведите на столбец, чтобы увидеть цифры. Данные берутся из истории проверок, которую программа уже хранит.
/// </summary>
internal sealed class QualityWindow
{
    private const double ChartHeight = 250;
    // Разметка графика: слева подписи мс, сверху линия времени ответа, ниже полоса сбоев, внизу подписи времени
    private const double Left = 56, Right = 8, PlotTop = 10, PlotH = 150, FailTop = 176, FailH = 44, LabelTop = 232;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static QualityWindow? _open;
    private readonly Window _w;
    private readonly WrapPanel _pick = new();
    private readonly UniformGrid _tiles = new() { Columns = 4 };
    private readonly TextBlock _period = Ui.Hint("", 0);
    private readonly Canvas _chart = new() { Height = ChartHeight, ClipToBounds = true };
    private readonly TextBlock _conclusion = Ui.Hint("", 0);
    private DnsServer? _current;

    public static void Show(Window? owner, IReadOnlyList<DnsServer> servers, DnsServer? first)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new QualityWindow(servers, first);
        _open._w.Show();
    }

    private QualityWindow(IReadOnlyList<DnsServer> servers, DnsServer? first)
    {
        _w = DnexDialog.CreateWindow("График качества", 860);
        _w.Closed += (_, _) => _open = null;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };
        _w.MaxHeight = Math.Min(900, SystemParameters.WorkArea.Height - 40);

        // Кандидаты: выбранный сервер, подключенный, избранные, затем с самой длинной историей
        var candidates = new List<DnsServer>();
        void Add(DnsServer? s) { if (s != null && !s.IsJunk && !candidates.Contains(s)) candidates.Add(s); }
        Add(first);
        Add(servers.FirstOrDefault(s => s.IsConnected));
        foreach (var s in servers.Where(s => s.IsFavorite)) Add(s);
        foreach (var s in servers.Where(s => s.History.Count >= 3).OrderByDescending(s => s.History.Count)) Add(s);
        _current = candidates.FirstOrDefault();
        foreach (var s in candidates.Take(8))
        {
            var server = s;
            var rb = new RadioButton { Content = DnsServer.Clip(s.DisplayName, 26), GroupName = "quality", IsChecked = s == _current,
                Style = (Style)Application.Current.Resources["Tab"], Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _current = server; Render(); };
            _pick.Children.Add(rb);
        }

        var body = new StackPanel();
        body.Children.Add(Ui.Section("Сервер", first: true));
        body.Children.Add(_pick);
        body.Children.Add(Ui.Section("Итоги за период"));
        _tiles.Margin = new Thickness(0, 0, -8, 0);
        body.Children.Add(_tiles);
        body.Children.Add(Ui.Section("Ответы по времени"));
        var card = new StackPanel();
        card.Children.Add(_period);
        card.Children.Add(_chart.With(c => c.Margin = new Thickness(0, 10, 0, 0)));
        card.Children.Add(Ui.Hint("Линия: обычное время ответа. Полоска внутри столбца: разброс. Внизу: доля проверок без ответа. " +
                                  "Наведите на столбец, чтобы увидеть цифры", 8).With(t => t.FontSize = 11));
        card.Children.Add(_conclusion.With(t => { t.Margin = new Thickness(2, 12, 0, 0); t.FontSize = 13; }));
        body.Children.Add(Ui.Card(card, bottom: 0).With(c => c.Padding = new Thickness(18, 16, 18, 16)));

        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20) };
        var header = DnexDialog.Header(DialogKind.Info, "График качества",
            "Как отвечал сервер за последние 14 дней: время ответа и проверки без ответа", glyph: "activity");
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

    private static Color AccentColor() => (Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color ?? Colors.GreenYellow;

    private static string D(DateTime t) => t.ToString("d MMM", Loc.En ? CultureInfo.InvariantCulture : Ru);

    private void Render()
    {
        _tiles.Children.Clear();
        _chart.Children.Clear();
        if (_current == null)
        {
            _period.Text = "Пока нет серверов с историей проверок. Запустите проверку списка";
            _conclusion.Text = "";
            return;
        }
        var now = DateTime.Now;
        var points = _current.History.Where(p => now - p.T <= TimeSpan.FromDays(14)).OrderBy(p => p.T).ToList();
        var sum = QualityStats.Summarize(points, now);
        _conclusion.Text = sum.Text;
        if (points.Count == 0)
        {
            _period.Text = "За последние 14 дней проверок нет";
            return;
        }
        int hours = QualityStats.BucketHours(points[0].T, now);
        _period.Text = $"С {D(points[0].T)} по {D(now)}, столбец {hours} ч, проверок {points.Count}";
        RenderTiles(sum);
        DrawChart();
    }

    private void RenderTiles(QualitySummary sum)
    {
        _tiles.Children.Add(Tile("Отвечал", $"{sum.OkPercent}%", $"{sum.OkCount} из {sum.Count} проверок",
            sum.OkPercent >= 95 ? "Ok" : sum.OkPercent >= 80 ? "Warn" : "Bad"));
        _tiles.Children.Add(Tile("Обычный ответ", sum.MedianMs is { } m ? $"{m} мс" : "нет",
            sum.SlowMs is { } s ? $"в плохие моменты до {s} мс" : "медиана по успешным проверкам", "Text"));
        _tiles.Children.Add(Tile("Провалы подряд", sum.Outages.ToString(CultureInfo.InvariantCulture),
            "две проверки без ответа подряд", sum.Outages == 0 ? "Ok" : "Warn"));
        _tiles.Children.Add(sum.WorstPart is { } part
            ? Tile("Чаще всего сбои", part.Split(' ')[0], $"{sum.WorstPercent}% проверок не прошли", "Bad")
            : Tile("Чаще всего сбои", "нет", "заметного времени суток нет", "Ok"));
    }

    private static Border Tile(string label, string value, string sub, string color)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = label, FontSize = 12 }.Ref(TextBlock.ForegroundProperty, "Muted"));
        body.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) }
            .Ref(TextBlock.ForegroundProperty, color));
        body.Children.Add(new TextBlock { Text = sub, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }
            .Ref(TextBlock.ForegroundProperty, "Muted"));
        return new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 8, 8), Child = body }
            .Ref(Border.BackgroundProperty, "Surface2");
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        if (_current == null || _chart.ActualWidth < 200) return;
        var now = DateTime.Now;
        var points = _current.History.Where(p => now - p.T <= TimeSpan.FromDays(14)).ToList();
        if (points.Count == 0) return;

        double w = _chart.ActualWidth;
        int hours = QualityStats.BucketHours(points.Min(p => p.T), now);
        var buckets = QualityStats.Buckets(points, now, hours);
        if (buckets.Count == 0) return;
        double colW = (w - Left - Right) / buckets.Count;
        double X(int i) => Left + colW * (i + 0.5);

        // Верхняя граница шкалы: максимум успешных ответов, округленный до 50 мс
        long top = Math.Max(60, buckets.Select(b => b.MaxMs ?? 0).DefaultIfEmpty(0).Max());
        top = (top + 49) / 50 * 50;
        double Y(long ms) => PlotTop + PlotH * (1 - Math.Clamp(ms / (double)top, 0, 1));
        double baseY = PlotTop + PlotH;
        var accent = AccentColor();

        // Сетка и подписи шкалы
        foreach (var v in new[] { 0L, top / 2, top })
        {
            double y = Y(v);
            _chart.Children.Add(new Line { X1 = Left, X2 = w - Right, Y1 = y, Y2 = y, Stroke = Pal("Border"), StrokeThickness = 1 });
            Text($"{v} мс", 0, y - 8, "Muted");
        }
        Text("сбои", 0, FailTop + FailH / 2 - 8, "Muted");

        // Разделители суток и подписи времени (без наложения)
        double lastRight = -1;
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            if (b.Start.Hour == 0)
            {
                double x = Left + colW * i;
                _chart.Children.Add(new Line { X1 = x, X2 = x, Y1 = PlotTop, Y2 = LabelTop - 4, Stroke = Pal("Border"), StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 } });
            }
            string? label = b.Start.Hour == 0 || hours >= 24 ? D(b.Start)
                : hours == 6 && b.Start.Hour == 12 ? "12:00"
                : hours == 2 && b.Start.Hour % 6 == 0 ? b.Start.ToString("HH:mm", CultureInfo.InvariantCulture)
                : null;
            if (label == null) continue;
            var t = Text(label, 0, LabelTop, "Muted");
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lx = Math.Max(Left, X(i) - t.DesiredSize.Width / 2);
            if (lx < lastRight + 6) continue;
            Canvas.SetLeft(t, lx);
            lastRight = lx + t.DesiredSize.Width;
        }

        // Полоска разброса внутри каждого столбца
        double barW = Math.Max(3, colW * 0.24);
        foreach (var (b, i) in buckets.Select((b, i) => (b, i)))
        {
            if (b.MinMs is not > 0 || b.MaxMs is not > 0) continue;
            double y1 = Y(b.MaxMs!.Value), y2 = Y(b.MinMs!.Value);
            var rect = new Rectangle { Width = barW, Height = Math.Max(2, y2 - y1), RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)) };
            Canvas.SetLeft(rect, X(i) - barW / 2); Canvas.SetTop(rect, y1);
            _chart.Children.Add(rect);
        }

        // Линия обычного времени ответа с заливкой под ней
        var pts = buckets.Select((b, i) => (b, i)).Where(t => t.b.MedianMs is > 0)
            .Select(t => new Point(X(t.i), Y(t.b.MedianMs!.Value))).ToList();
        if (pts.Count >= 2)
        {
            var area = Figure(pts);
            area.Segments.Add(new LineSegment(new Point(pts[^1].X, baseY), true));
            area.Segments.Add(new LineSegment(new Point(pts[0].X, baseY), true));
            area.IsClosed = true;
            var fill = new LinearGradientBrush(Color.FromArgb(90, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90);
            _chart.Children.Add(new Path { Data = new PathGeometry { Figures = { area } }, Fill = fill });
            _chart.Children.Add(new Path { Data = new PathGeometry { Figures = { Figure(pts) } }, Stroke = Pal("Accent"), StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round });
        }
        foreach (var p in pts)
            _chart.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Pal("Accent"), Stroke = Pal("Surface2"), StrokeThickness = 1.5 }
                .With(e => { Canvas.SetLeft(e, p.X - 3); Canvas.SetTop(e, p.Y - 3); }));

        // Полоса сбоев: высота — доля проверок без ответа
        foreach (var (b, i) in buckets.Select((b, i) => (b, i)))
        {
            if (b.Count == 0) continue;
            double h = b.Failed == 0 ? 2 : Math.Max(3, FailH * b.FailPercent / 100.0);
            var color = b.FailPercent >= 20 ? "Bad" : b.Failed > 0 ? "Warn" : "Border";
            var rect = new Rectangle { Width = barW, Height = h, RadiusX = 2, RadiusY = 2, Fill = Pal(color) };
            Canvas.SetLeft(rect, X(i) - barW / 2); Canvas.SetTop(rect, FailTop + FailH - h);
            _chart.Children.Add(rect);
        }

        // Невидимые столбцы для подсказок: наведите на время, чтобы увидеть цифры
        for (int i = 0; i < buckets.Count; i++)
        {
            var hit = new Rectangle { Width = colW, Height = LabelTop - PlotTop, Fill = Brushes.Transparent, ToolTip = Tip(buckets[i]) };
            Canvas.SetLeft(hit, Left + colW * i); Canvas.SetTop(hit, PlotTop);
            _chart.Children.Add(hit);
        }
    }

    /// <summary>Гладкая кривая через точки (Catmull-Rom в кубические Безье): без изломов, как у хороших графиков.</summary>
    private static PathFigure Figure(IReadOnlyList<Point> p)
    {
        var fig = new PathFigure { StartPoint = p[0], IsClosed = false };
        for (int i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(0, i - 1)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(p.Count - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
        return fig;
    }

    private static string Tip(QualityBucket b)
    {
        var head = $"{D(b.Start)}, {b.Start:HH:mm}–{b.End:HH:mm}";
        if (b.Count == 0) return head + ": " + Loc.T("проверок не было");
        var stat = Loc.T($"проверок {b.Count}, без ответа {b.Failed}");
        return head + "\n" + (b.MedianMs is { } m ? Loc.T($"обычно {m} мс") + ", " : "") + stat;
    }

    private TextBlock Text(string text, double x, double y, string color)
    {
        var t = new TextBlock { Text = text, FontSize = 11 }.Ref(TextBlock.ForegroundProperty, color);
        Canvas.SetLeft(t, x); Canvas.SetTop(t, y);
        _chart.Children.Add(t);
        return t;
    }
}
