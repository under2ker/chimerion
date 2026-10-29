using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>
/// Окно «Диагностика»: все проверки одним списком (зеленые, желтые, красные строки), копирование отчета
/// и «Сообщить о проблеме» — черновик issue на GitHub с отчетом без личных данных.
/// </summary>
internal sealed class DiagnosticsWindow
{
    private readonly Window _w;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<Tool> _tools;
    private readonly Func<IEnumerable<string>> _logTail;
    private readonly Action<string> _log;
    private readonly StackPanel _list = new();
    private TextBlock _state = new();
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 0, 2, 10) };
    private readonly ProgressBar _siteBar = new() { IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 6) };
    private readonly List<Button> _buttons = new();
    private DiagReport? _report;
    private readonly TextBox _site = new();
    private readonly StackPanel _siteResult = new() { Margin = new Thickness(0, 0, 0, 6) };
    private bool _siteBusy;
    private Func<string, Task>? _fix;

    private async Task CheckSiteAsync()
    {
        if (_siteBusy || _site.Text.Trim().Length == 0) return;
        _siteBusy = true;
        _siteResult.Children.Clear();
        if (_siteBar.Parent is Panel old) old.Children.Remove(_siteBar);
        _siteResult.Children.Add(_siteBar);
        _siteResult.Children.Add(Ui.Hint($"Проверяю {SiteCheck.HostOf(_site.Text)}: DNS, соединение, страницу и ее файлы…", 0));
        try
        {
            var r = await SiteCheck.CheckAsync(_site.Text);
            _siteResult.Children.Clear();
            var level = r.Verdict switch { SiteVerdict.Ok => DiagLevel.Ok, SiteVerdict.Throttled or SiteVerdict.Protected => DiagLevel.Warn, _ => DiagLevel.Bad };
            var title = r.Verdict switch
            {
                SiteVerdict.Ok => "загружается",
                SiteVerdict.Throttled => "загрузка обрывается",
                SiteVerdict.Blocked => "не отвечает",
                SiteVerdict.NoDns => "DNS не нашел адрес",
                SiteVerdict.Refused => "не пускает из вашей страны",
                SiteVerdict.Protected => "закрыт проверкой Cloudflare",
                _ => "не проверен",
            };
            _siteResult.Children.Add(Row(new DiagItem(level, $"{r.Host}: {title}", string.Join("\n", r.Lines.Append(r.Advice)))));
            _log($"Проверка сайта {r.Host}: {title}");
        }
        catch (Exception e) { _siteResult.Children.Clear(); _siteResult.Children.Add(Row(new DiagItem(DiagLevel.Bad, "Проверка не удалась", e.Message))); }
        _siteBusy = false;
    }

    private static DiagnosticsWindow? _open;

    public static void Show(AppSettings settings, IReadOnlyList<Tool> tools, Func<IEnumerable<string>> logTail, Action<string> log,
                            Func<string, Task>? fix = null)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new DiagnosticsWindow(settings, tools, logTail, log) { _fix = fix };
        _open._w.Show();
        _ = _open.RunAsync();
    }

    private static ResourceDictionary Res => Application.Current.Resources;

    private DiagnosticsWindow(AppSettings settings, IReadOnlyList<Tool> tools, Func<IEnumerable<string>> logTail, Action<string> log)
    {
        _settings = settings;
        _tools = tools;
        _logTail = logTail;
        _log = log;
        _w = DnexDialog.CreateWindow("Диагностика", 680);
        _w.Closed += (_, _) => _open = null;
        // Кнопки внизу закреплены, прокручивается только список: на маленьком экране они не уезжают
        _w.MaxHeight = Math.Min(960, SystemParameters.WorkArea.Height - 40); // на большом экране не во всю высоту: длинный список прокручивается внутри

        var root = new Grid { Margin = new Thickness(24, 22, 24, 20) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Диагностика",
            "Подключение, доступ к сервисам, помехи в системе, Telegram и zapret, обновления", glyph: "stethoscope"));

        // Проверка конкретного сайта: DNS, соединение, обрыв загрузки (замедление Cloudflare) и что делать
        var siteBody = new StackPanel();
        var siteRow = new DockPanel();
        var siteBtn = Ui.Action("Проверить", "Strong", () => _ = CheckSiteAsync()).With(b => b.Margin = new Thickness(8, 0, 0, 0));
        DockPanel.SetDock(siteBtn, Dock.Right);
        _buttons.Add(siteBtn);
        siteRow.Children.Add(siteBtn);
        _site.Tag = "Адрес сайта";
        _site.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _ = CheckSiteAsync(); };
        siteRow.Children.Add(_site);
        siteBody.Children.Add(siteRow);
        siteBody.Children.Add(Ui.Hint("Не открывается сайт: впишите его, например uiverse.io. Программа проверит DNS, соединение и загрузку", 6));
        _siteResult.Margin = new Thickness(0, 10, 0, 0);
        siteBody.Children.Add(_siteResult);
        var siteBlock = new StackPanel();
        siteBlock.Children.Add(Ui.Section("Проверить сайт", first: true));
        siteBlock.Children.Add(Ui.Card(siteBody, bottom: 0));
        Grid.SetRow(siteBlock, 1); root.Children.Add(siteBlock);

        var stateBlock = new StackPanel();
        stateBlock.Children.Add(Ui.Section("Состояние"));
        _state = Ui.Hint("", 0).With(t => { t.Margin = new Thickness(2, 0, 0, 8); t.FontSize = 13; });
        stateBlock.Children.Add(_bar);
        stateBlock.Children.Add(_state);
        Grid.SetRow(stateBlock, 2); root.Children.Add(stateBlock);
        var listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        var listCard = Ui.Card(listScroll, bottom: 0);
        Grid.SetRow(listCard, 3); root.Children.Add(listCard);

        Button Add(string text, string style, Action click) { var b = Ui.Action(text, style, click); _buttons.Add(b); return b; }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Add("Скопировать отчет", "Strong", Copy).With(b => b.Margin = new Thickness(0, 0, 8, 0)));
        left.Children.Add(Add("Сохранить отчет", "Strong", Save).With(b => { b.Margin = new Thickness(0, 0, 8, 0); b.ToolTip = "Сохранить отчет текстовым файлом, например на рабочий стол"; }));
        left.Children.Add(Add("Повторить", "Ghost", () => _ = RunAsync()));
        var close = Add("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        var footer = Ui.Footer(left, Add("Сообщить о проблеме", "Primary", Report), close);
        Grid.SetRow(footer, 4); root.Children.Add(footer);
        _w.Content = root;
    }

    private async Task RunAsync()
    {
        foreach (var b in _buttons) b.IsEnabled = b.IsCancel;
        _list.Children.Clear();
        _state.Text = "Проверяю подключение, сервисы, помехи, программы и обновления. Это до 20 секунд…";
        _bar.Visibility = Visibility.Visible;
        try
        {
            _report = await Diagnostics.RunAsync(_settings, _tools);
            int bad = _report.Items.Count(i => i.Level == DiagLevel.Bad), warn = _report.Items.Count(i => i.Level == DiagLevel.Warn);
            _state.Text = bad + warn == 0 ? "Проблем не найдено" : $"Проблем: {bad}, предупреждений: {warn}. Они собраны сверху";
            Render(_report);
            _log($"Диагностика: проблем {bad}, предупреждений {warn}");
            // Сами проблемы тоже в журнал: по присланному журналу должно быть видно, что сломалось
            foreach (var i in _report.Items.Where(i => i.Level is DiagLevel.Bad or DiagLevel.Warn))
                _log($"  {(i.Level == DiagLevel.Bad ? "[x]" : "[!]")} {i.Title}{(i.Detail.Length > 0 ? ": " + i.Detail : "")}");
        }
        catch (Exception e) { _state.Text = "Диагностика не завершилась: " + e.Message; }
        _bar.Visibility = Visibility.Collapsed;
        foreach (var b in _buttons) b.IsEnabled = true;
    }

    /// <summary>
    /// Включить DNS-туннель WSL: строка в .wslconfig (прежний файл в .wslconfig.chimerion.bak) и, с согласия,
    /// перезапуск WSL, без которого настройка не подействует.
    /// </summary>
    public static void EnableWslTunnel(Action<string> log)
    {
        if (!DnexDialog.Ask("Включить DNS-туннель WSL?",
                "В .wslconfig добавится dnsTunneling=true (прежний файл сохранится рядом), и запросы из WSL пойдут через «Два DNS».",
                "Включить", windowTitle: "WSL")) return;
        DevTools.EnableTunnel();
        log("WSL: DNS-туннель включен в .wslconfig");
        if (DnexDialog.Ask("Перезапустить WSL сейчас?", "Настройка подействует после перезапуска WSL. Открытые терминалы WSL и запущенные в нем программы закроются.",
                "Перезапустить", windowTitle: "WSL"))
        {
            using var p = Process.Start(new ProcessStartInfo("wsl.exe", "--shutdown") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(15000);
            log("WSL перезапущен");
        }
    }

    /// <summary>Исправление по кнопке в строке. Действия главного окна (подключение, обновление) идут через _fix.</summary>
    private async Task FixAsync(DiagItem i, Button b)
    {
        b.IsEnabled = false;
        try
        {
            var a = i.Action!;
            if (a.StartsWith("open:"))
                Process.Start(new ProcessStartInfo(a[5..]) { UseShellExecute = true });
            else if (a == "hosts")
                // Блокнот с правами администратора: без них hosts не сохранить
                Process.Start(new ProcessStartInfo("notepad.exe", Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\drivers\etc\hosts"))
                              { UseShellExecute = true, Verb = "runas" });
            else if (a == "wsl-tunnel") EnableWslTunnel(_log);
            else if (a.StartsWith("tool:") && _tools.FirstOrDefault(t => t.Id == a[5..]) is { } tool)
            {
                await tool.EnableAsync(new Progress<string>(_ => { }), CancellationToken.None);
                _log($"{tool.Title}: запущено из диагностики");
            }
            else if (_fix != null) await _fix(a);
            // Открытие настроек ничего само не меняет; остальное проверяем заново
            if (!a.StartsWith("open:") && a != "hosts" && a != "tools" && a != "update") await RunAsync(); // и после «Два DNS»: отчет пересчитывается
        }
        catch (System.ComponentModel.Win32Exception) { /* отказ в окне прав Windows */ }
        catch (Exception e) { _log("Исправление не удалось: " + e.Message); DnexDialog.Show("Не получилось", e.Message, DialogKind.Error, "Диагностика"); }
        b.IsEnabled = true;
    }

    /// <summary>
    /// Сначала «Требует внимания» (проблемы и предупреждения), потом остальные строки по разделам: подключение,
    /// нейросети, аккаунты, система, программы, обновления. Иначе важное терялось в одном длинном списке.
    /// </summary>
    private void Render(DiagReport report)
    {
        _list.Children.Clear();
        var problems = report.Items.Where(i => i.Level is DiagLevel.Bad or DiagLevel.Warn).OrderBy(i => i.Level == DiagLevel.Bad ? 0 : 1).ToList();
        bool first = true;
        if (problems.Count > 0)
        {
            _list.Children.Add(GroupTitle("Требует внимания", problems.Count, true, "Warn"));
            foreach (var i in problems) _list.Children.Add(Row(i, showGroup: true));
            first = false;
        }
        foreach (var g in report.Items.Where(i => i.Level is not (DiagLevel.Bad or DiagLevel.Warn)).GroupBy(i => i.Group.Length > 0 ? i.Group : "Другое"))
        {
            _list.Children.Add(GroupTitle(g.Key, g.Count(), first, "Muted"));
            foreach (var i in g) _list.Children.Add(Row(i));
            first = false;
        }
    }

    private static TextBlock GroupTitle(string text, int count, bool first, string color) =>
        Ui.Section($"{text} · {count}", first).With(t => { t.Margin = new Thickness(2, first ? 2 : 16, 0, 8); t.SetResourceReference(TextBlock.ForegroundProperty, color); });

    private FrameworkElement Row(DiagItem i, bool showGroup = false)
    {
        var color = i.Level switch { DiagLevel.Ok => "Ok", DiagLevel.Warn => "Warn", DiagLevel.Bad => "Bad", _ => "Muted" };
        var (kind, word) = i.Level switch
        {
            DiagLevel.Ok => ("circle-check", "В порядке"), DiagLevel.Warn => ("triangle-alert", "Предупреждение"),
            DiagLevel.Bad => ("circle-x", "Проблема"), _ => ("info", "Сведения"),
        };
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Значок, а не только цвет точки: состояние понятно и без различения цветов, слово в подсказке
        g.Children.Add(new Controls.Icon { Kind = kind, Size = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), ToolTip = word }
            .Ref(Controls.Icon.ForegroundProperty, color));
        var text = new StackPanel();
        if (showGroup && i.Group.Length > 0) text.Children.Add(Ui.Hint(i.Group, 0).With(t => t.FontSize = 11.5));
        text.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(i.Title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (i.Detail.Length > 0)
            text.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(i.Detail), Foreground = (Brush)Res["Text"], Opacity = 0.72, FontSize = 12.5, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }); // ярче и крупнее серого: подробности на темном фоне читались с трудом
        if (i.More.Length > 0)
        {
            // Подробности (история стран, технические данные) по запросу: главная мысль строкой выше
            var more = new TextBlock { Text = PrivacyGuard.Mask(i.More), FontSize = 12.5, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Opacity = .72, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed }
                .Ref(TextBlock.ForegroundProperty, "Text");
            var toggle = new TextBlock { Text = "Подробнее", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left }
                .Ref(TextBlock.ForegroundProperty, "Accent");
            toggle.MouseLeftButtonUp += (_, _) =>
            {
                bool open = more.Visibility != Visibility.Visible;
                more.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                toggle.Text = open ? "Скрыть подробности" : "Подробнее";
            };
            text.Children.Add(toggle);
            text.Children.Add(more);
        }
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (i.Action != null && i.ActionText != null)
        {
            // Кнопка исправления под пояснением, а не справа: справа она сжимала текст в узкую колонку.
            // CHIMERION делает это сама, но только по нажатию. Все действия одной заметной кнопкой
            Button? fix = null;
            fix = Ui.Action(i.ActionText, "Strong", () => _ = FixAsync(i, fix!))
                    .With(b => { b.Margin = new Thickness(0, 8, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Left; b.Padding = new Thickness(12, 4, 12, 4); b.MinHeight = 28; });
            text.Children.Add(fix);
        }
        return g;
    }

    private string? ReportText() => _report == null ? null : Diagnostics.ToText(_report, _logTail());

    private void Copy()
    {
        if (ReportText() is not { } text) return;
        _state.Text = Ui.CopyText(text)
            ? "Отчет скопирован. Ваш IP, локальные адреса, имя пользователя и почта из него убраны"
            : "Буфер обмена занят другой программой. Сохраните отчет в файл кнопкой «Сохранить отчет»";
    }

    /// <summary>Отчет файлом: когда буфер обмена занят или отчет нужно приложить к сообщению.</summary>
    private void Save()
    {
        if (ReportText() is not { } text) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"chimerion-report-{DateTime.Now:yyyy-MM-dd-HHmm}.txt", Filter = "Текст (*.txt)|*.txt", DefaultExt = ".txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(_w) != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(false));
            _state.Text = $"Отчет сохранен: {System.IO.Path.GetFileName(dlg.FileName)}. Ваш IP, локальные адреса, имя пользователя и почта из него убраны";
            _log("Отчет диагностики сохранен в файл");
        }
        catch (Exception e) { _state.Text = "Не удалось сохранить отчет: " + e.Message; }
    }

    private void Report()
    {
        if (ReportText() is not { } text) return;
        if (!DnexDialog.Ask("Открыть черновик на GitHub?",
                "Отчет будет виден всем на GitHub. Ваш внешний IP, адреса локальной сети, имя пользователя Windows и почта из него убраны, " +
                "адреса DNS-серверов остаются. Перед отправкой отчет можно поправить. Нужен аккаунт GitHub.",
                "Открыть", kind: DialogKind.Info, windowTitle: "Сообщить о проблеме"))
            return;
        bool copied = Ui.CopyText(text);
        var problems = _report!.Items.Where(i => i.Level == DiagLevel.Bad).Select(i => i.Title).FirstOrDefault();
        var title = $"CHIMERION {UpdateChecker.CurrentVersion}: " + (problems ?? "проблема");
        var body = "Что случилось (опишите своими словами):\n\n\n" + "```\n" + text + "```";
        AutoStart.OpenUrl(Diagnostics.IssueUrl(title, body));
        _state.Text = copied ? "Черновик открыт в браузере, полный отчет также в буфере обмена"
                             : "Черновик открыт в браузере. Буфер обмена занят: полный отчет можно сохранить кнопкой «Сохранить отчет»";
        _log("Открыт черновик сообщения о проблеме на GitHub");
    }
}
