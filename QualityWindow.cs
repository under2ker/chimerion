using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «График качества»: как отвечал выбранный сервер за последние 14 дней. По истории проверок, которую программа
/// и так хранит. Линия показывает время ответа, красные отметки внизу: проверки без ответа. Под графиком выводы словами.
/// </summary>
internal sealed class QualityWindow
{
    private static QualityWindow? _open;
    private readonly Window _w;
    private readonly Canvas _canvas = new() { Height = 260, ClipToBounds = true };
    private readonly TextBlock _summary = Ui.Hint("", 0);
    private readonly WrapPanel _pick = new();
    private DnsServer? _current;

    public static void Show(Window? owner, IReadOnlyList<DnsServer> servers, DnsServer? first)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new QualityWindow(servers, first);
        _open._w.Show();
    }

    private QualityWindow(IReadOnlyList<DnsServer> servers, DnsServer? first)
    {
        _w = DnexDialog.CreateWindow("График качества", 820);
        _w.Closed += (_, _) => _open = null;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };

        var root = new Grid { Margin = new Thickness(24, 22, 24, 20) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "График качества",
            "Как отвечал сервер за последние 14 дней: время ответа и проверки без ответа", glyph: "activity"));

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
        _pick.Margin = new Thickness(0, 12, 0, 4);
        Grid.SetRow(_pick, 1); root.Children.Add(_pick);

        var body = new StackPanel();
        body.Children.Add(_canvas);
        body.Children.Add(_summary.With(t => { t.Margin = new Thickness(0, 12, 0, 0); t.FontSize = 13; }));
        var card = Ui.Card(body, bottom: 0);
        Grid.SetRow(card, 2); root.Children.Add(card);
        _canvas.SizeChanged += (_, _) => Render();

        var footer = Ui.Footer(new StackPanel(), Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true));
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        _w.Content = root;
        _w.Loaded += (_, _) => Render();
    }

    private static Brush Pal(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private void Render()
    {
        _canvas.Children.Clear();
        if (_current == null) { _summary.Text = "Пока нет серверов с историей проверок. Запустите проверку списка"; return; }
        var now = DateTime.Now;
        var points = _current.History.Where(p => now - p.T <= TimeSpan.FromDays(14)).OrderBy(p => p.T).ToList();
        _summary.Text = QualityStats.Summarize(_current.History, now).Text;
        double w = _canvas.ActualWidth, h = _canvas.Height;
        if (w < 60 || points.Count == 0) return;

        const double left = 52, bottom = 28, top = 8, right = 10;
        double pw = w - left - right, ph = h - bottom - top;
        var start = now.AddDays(-14);
        double X(DateTime t) => left + pw * Math.Clamp((t - start).TotalSeconds / TimeSpan.FromDays(14).TotalSeconds, 0, 1);
        long maxMs = Math.Max(100, points.Where(p => p.Ok && p.Ms is > 0).Select(p => p.Ms!.Value).DefaultIfEmpty(100).Max());
        maxMs = (maxMs + 99) / 100 * 100;
        double Y(long ms) => top + ph * (1 - Math.Min(1.0, (double)ms / maxMs));

        // Сетка и подписи по вертикали
        for (int i = 0; i <= 4; i++)
        {
            double y = top + ph * i / 4;
            _canvas.Children.Add(new Line { X1 = left, X2 = left + pw, Y1 = y, Y2 = y, Stroke = Pal("Border"), StrokeThickness = 1 });
            var label = new TextBlock { Text = $"{maxMs - maxMs * i / 4} мс", FontSize = 11, Foreground = Pal("Muted") };
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 8);
            _canvas.Children.Add(label);
        }
        // Подписи по горизонтали: начало, середина и конец периода
        foreach (var (t, align) in new[] { (start, 0), (start.AddDays(7), 1), (now, 2) })
        {
            var label = new TextBlock { Text = t.ToString("d MMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), FontSize = 11, Foreground = Pal("Muted") };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = X(t) - (align == 0 ? 0 : align == 1 ? label.DesiredSize.Width / 2 : label.DesiredSize.Width);
            Canvas.SetLeft(label, x); Canvas.SetTop(label, h - bottom + 8);
            _canvas.Children.Add(label);
        }
        // Линия времени ответа между рабочими проверками
        var line = new Polyline { Stroke = Pal("Accent"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        foreach (var p in points.Where(p => p.Ok && p.Ms is > 0)) line.Points.Add(new Point(X(p.T), Y(p.Ms!.Value)));
        if (line.Points.Count > 1) _canvas.Children.Add(line);
        foreach (var p in points)
        {
            if (p.Ok)
            {
                if (p.Ms is not > 0) continue;
                var dot = new Ellipse { Width = 5, Height = 5, Fill = Pal("Accent") };
                Canvas.SetLeft(dot, X(p.T) - 2.5); Canvas.SetTop(dot, Y(p.Ms.Value) - 2.5);
                _canvas.Children.Add(dot);
            }
            else
            {
                // Проверка без ответа: красная черта внизу графика
                _canvas.Children.Add(new Line { X1 = X(p.T), X2 = X(p.T), Y1 = top + ph - 26, Y2 = top + ph, Stroke = Pal("Bad"), StrokeThickness = 2.5, Opacity = .9 });
            }
        }
    }
}
