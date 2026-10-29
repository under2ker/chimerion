using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Сайты»: сайты, которыми вы пользуетесь, и открываются ли они сейчас. Сверху неработающие: короткий статус,
/// совет в одну строку и действие (в zapret, в Smart DNS); сделанное действие показывается отметкой вместо кнопки.
/// Ниже рабочие сайты одной строкой. Список собирается из кэша DNS Windows и не уходит с компьютера.
/// </summary>
internal sealed class SitesWindow
{
    private static SitesWindow? _open;
    /// <summary>Содержимое вкладки «Под наблюдением» окна «Помощь».</summary>
    public FrameworkElement View { get; }
    private readonly SiteWatch _watch;
    private readonly Func<bool, Task> _round;
    private readonly Func<string, string, Task> _action;
    private readonly Func<string, string?> _handled;
    private readonly StackPanel _list = new();
    private readonly TextBlock _state = Ui.Hint("", 0);
    private readonly Button _checkAll;

    /// <summary>Страница для окна «Помощь» (одна на окно).</summary>
    public static FrameworkElement Create(SiteWatch watch, Func<bool, Task> round, Func<string, string, Task> action, Func<string, string?> handled)
    {
        _open = new SitesWindow(watch, round, action, handled);
        _open.Render();
        return _open.View;
    }

    /// <summary>Окно «Помощь» закрыто: фоновый круг больше не перерисовывает страницу.</summary>
    public static void Forget() => _open = null;

    /// <summary>После фонового круга: открытое окно перерисовывается.</summary>
    public static void Refresh() => _open?.Render();

    private SitesWindow(SiteWatch watch, Func<bool, Task> round, Func<string, string, Task> action, Func<string, string?> handled)
    {
        _watch = watch; _round = round; _action = action; _handled = handled;
        var root = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _checkAll = Ui.Action("Проверить все", "Primary", () => _ = CheckAllAsync()).With(b => { b.Margin = new Thickness(0); b.VerticalAlignment = VerticalAlignment.Center; });
        DockPanel.SetDock(_checkAll, Dock.Right); head.Children.Add(_checkAll);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Открываются ли ваши сайты", FontSize = 16, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(_state.With(t => t.Margin = new Thickness(0, 2, 0, 0)));
        head.Children.Add(titles);
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(_list);
        root.Children.Add(Ui.Card(body, bottom: 0).With(c => c.Padding = new Thickness(18, 16, 12, 12)));
        root.Children.Add(Ui.Hint("Программа сама замечает сайты, которыми вы пользуетесь, и проверяет их в фоне. Список хранится только у вас, " +
                                  "наружу уходит лишь имя сайта, который не открылся", 10).With(t => t.FontSize = 11));
        View = root;
    }

    private async Task CheckAllAsync()
    {
        _checkAll.IsEnabled = false;
        _state.Text = "Проверяю все сайты…";
        try { await _round(true); }
        finally { _checkAll.IsEnabled = true; Render(); }
    }

    private static bool IsBad(SiteWatch.Site s) => s.State is not (SiteWatch.Verdict.Ok or SiteWatch.Verdict.Unknown);

    private void Render()
    {
        _list.Children.Clear();
        var sites = _watch.Sites.Values.Where(s => s.Seen >= SiteWatch.SeenToWatch && !_watch.Ignored.Contains(s.Host) && !SiteWatch.IsNotSite(s))
            .OrderByDescending(s => s.State == SiteWatch.Verdict.Blocked || s.State == SiteWatch.Verdict.Refused).ThenByDescending(s => s.Down)
            .ThenByDescending(s => s.LastSeen).ToList();
        var bad = sites.Where(IsBad).ToList();
        var good = sites.Where(s => !IsBad(s)).ToList();
        _state.Text = sites.Count == 0
            ? "Пока пусто: сайты появятся, когда вы ими попользуетесь"
            : bad.Count == 0 ? $"Сайтов: {sites.Count}, все открываются" : $"Сайтов: {sites.Count}, не открываются: {bad.Count}";
        if (bad.Count > 0)
        {
            _list.Children.Add(GroupTitle("Не открываются", bad.Count, first: true));
            foreach (var s in bad) _list.Children.Add(BadRow(s));
        }
        if (good.Count > 0)
        {
            _list.Children.Add(GroupTitle("Открываются", good.Count, first: bad.Count == 0));
            foreach (var s in good) _list.Children.Add(GoodRow(s));
        }
    }

    private static TextBlock GroupTitle(string text, int count, bool first) =>
        Ui.Section($"{text} · {count}", first).With(t => t.Margin = new Thickness(2, first ? 2 : 14, 0, 6));

    private static FrameworkElement Dot(string brush) => new System.Windows.Shapes.Ellipse
    {
        Width = 8, Height = 8, Fill = Ui.B(brush), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
    };

    private static string Cap(string t) => t.Length == 0 ? t : char.ToUpper(t[0]) + t[1..];

    private Button Small(string label, string style, Func<Task> act)
    {
        var b = Ui.Action(label, style, () => { });
        b.Padding = new Thickness(10, 2, 10, 3); b.MinHeight = 24; b.FontSize = 12; b.Margin = new Thickness(0, 0, 6, 0);
        b.Click += async (_, _) => { b.IsEnabled = false; try { await act(); } finally { b.IsEnabled = true; Render(); } };
        return b;
    }

    private Task Recheck(SiteWatch.Site s) => RecheckAsync(s);

    private async Task RecheckAsync(SiteWatch.Site s)
    {
        // Как в фоновом круге: домен ИИ при подключении должен идти через Smart DNS
        bool smart = DnsSwitcher.GetActive() != null && SplitDns.SuffixDomains.Any(x => s.Host.Equals(x, StringComparison.OrdinalIgnoreCase));
        var (v, d) = await SiteWatch.CheckAsync(s.Host, smart, CancellationToken.None);
        _watch.Apply(s, v, d, DateTime.Now);
        _watch.Save();
    }

    /// <summary>Неработающий сайт: имя и короткий статус цветом, строка совета, действия. Сделанное действие видно отметкой.</summary>
    private FrameworkElement BadRow(SiteWatch.Site s)
    {
        string color = s.State == SiteWatch.Verdict.DownForAll ? "Warn" : "Bad";
        var box = new StackPanel { Margin = new Thickness(0, 0, 6, 8) };

        var head = new DockPanel();
        if (s.LastChecked is { } t)
        {
            var time = new TextBlock { Text = $"{t:HH:mm}", FontSize = 11, Foreground = Ui.B("Muted"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Когда проверен" };
            DockPanel.SetDock(time, Dock.Right);
            head.Children.Add(time);
        }
        head.Children.Add(Dot(color));
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Inlines.Add(new System.Windows.Documents.Run(s.Host) { FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new System.Windows.Documents.Run("  " + Cap(SiteWatch.Describe(s.State))) { Foreground = Ui.B(color), FontSize = 12 });
        head.Children.Add(title);
        box.Children.Add(head);

        if (SiteWatch.Advice(s.State) is { Length: > 0 } a)
            box.Children.Add(new TextBlock { Text = a, FontSize = 12, Foreground = Ui.B("Muted"), Margin = new Thickness(18, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

        var buttons = new WrapPanel { Margin = new Thickness(18, 6, 0, 0) };
        var done = _handled(s.Host);
        if (done != null)
            buttons.Children.Add(new TextBlock
            {
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                Foreground = Ui.B(done.Contains("включите") ? "Warn" : "Ok"),
            }.With(t => Ui.SetTextWithIcon(t, done, "check", before: true, size: 13)));
        else if (s.State == SiteWatch.Verdict.Blocked) buttons.Children.Add(Small("В zapret", "Strong", () => _action(s.Host, "zapret")));
        else if (s.State == SiteWatch.Verdict.Refused) buttons.Children.Add(Small("В Smart DNS", "Strong", () => _action(s.Host, "smartdns")));
        // У прозрачной кнопки текст отстоит от края на поле кнопки: первую такую сдвигаем, чтобы текст встал под совет
        if (buttons.Children.Count == 0) buttons.Margin = new Thickness(8, 4, 0, 0);
        buttons.Children.Add(Small("Проверить", "Ghost", () => Recheck(s)));
        buttons.Children.Add(Small("Не следить", "Ghost", () => _action(s.Host, "ignore")));
        box.Children.Add(buttons);
        return box;
    }

    /// <summary>Рабочий сайт: одна строка. «Не следить» появляется при наведении, чтобы не рябило.</summary>
    private FrameworkElement GoodRow(SiteWatch.Site s)
    {
        bool unknown = s.State == SiteWatch.Verdict.Unknown;
        var row = new DockPanel { Margin = new Thickness(0, 0, 6, 2), Background = Brushes.Transparent, MinHeight = 26 };
        var ignore = Small("Не следить", "Ghost", () => _action(s.Host, "ignore"));
        ignore.Margin = new Thickness(6, 0, 0, 0);
        ignore.Visibility = Visibility.Hidden;
        DockPanel.SetDock(ignore, Dock.Right);
        row.Children.Add(ignore);
        var when = new TextBlock
        {
            Text = unknown ? "еще не проверен" : s.LastChecked is { } t ? $"{t:HH:mm}" : "", FontSize = 11, Foreground = Ui.B("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(when, Dock.Right);
        row.Children.Add(when);
        row.Children.Add(Dot(unknown ? "Muted" : "Ok"));
        row.Children.Add(new TextBlock { Text = s.Host, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        row.MouseEnter += (_, _) => ignore.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => ignore.Visibility = Visibility.Hidden;
        return row;
    }
}
