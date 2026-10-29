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
        _w = DnexDialog.CreateWindow("Диагностика", Ui.Wide);
        _w.Closed += (_, _) => _open = null;

        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Диагностика",
            "Подключение, доступ к сервисам, помехи в системе, Telegram и zapret, обновления", glyph: "stethoscope"));

        // Проверка конкретного сайта: DNS, соединение, обрыв загрузки (замедление Cloudflare) и что делать
        root.Children.Add(Ui.Section("Проверить сайт", first: true));
        var siteBody = new StackPanel();
        var siteRow = new DockPanel();
        var siteBtn = Ui.Action("Проверить", "Strong", () => _ = CheckSiteAsync()).With(b => b.Margin = new Thickness(8, 0, 0, 0));
        DockPanel.SetDock(siteBtn, Dock.Right);
        _buttons.Add(siteBtn);
        siteRow.Children.Add(siteBtn);
        _site.Tag = "Адрес сайта, который не открывается, например uiverse.io";
        _site.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _ = CheckSiteAsync(); };
        siteRow.Children.Add(_site);
        siteBody.Children.Add(siteRow);
        _siteResult.Margin = new Thickness(0, 10, 0, 0);
        siteBody.Children.Add(_siteResult);
        root.Children.Add(Ui.Card(siteBody, bottom: 0));

        root.Children.Add(Ui.Section("Состояние"));
        _state = Ui.Hint("", 0).With(t => t.Margin = new Thickness(2, 0, 0, 8));
        root.Children.Add(_bar);
        root.Children.Add(_state);
        root.Children.Add(Ui.Card(Ui.Scroll(_list, 400), bottom: 0));

        Button Add(string text, string style, Action click) { var b = Ui.Action(text, style, click); _buttons.Add(b); return b; }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Add("Скопировать отчет", "Strong", Copy).With(b => b.Margin = new Thickness(0, 0, 8, 0)));
        left.Children.Add(Add("Повторить", "Ghost", () => _ = RunAsync()));
        var close = Add("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        root.Children.Add(Ui.Footer(left, Add("Сообщить о проблеме", "Primary", Report), close));
        _w.Content = Ui.WindowContent(root);
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
            _state.Text = bad + warn == 0 ? "Проблем не найдено" : $"Проблем: {bad}, предупреждений: {warn}";
            foreach (var i in _report.Items) _list.Children.Add(Row(i));
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

    private FrameworkElement Row(DiagItem i)
    {
        var color = i.Level switch { DiagLevel.Ok => "Ok", DiagLevel.Warn => "Warn", DiagLevel.Bad => "Bad", _ => "Muted" };
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8, Fill = (Brush)Res[color], VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0),
        });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(i.Title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (i.Detail.Length > 0)
            text.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(i.Detail), Foreground = (Brush)Res["Muted"], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (i.Action != null && i.ActionText != null)
        {
            // Кнопка исправления справа: CHIMERION делает это сама, но только по нажатию
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button? fix = null;
            fix = Ui.Action(i.ActionText, i.Level == DiagLevel.Bad ? "Strong" : "Ghost", () => _ = FixAsync(i, fix!))
                    .With(b => { b.Margin = new Thickness(12, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Top; b.Padding = new Thickness(12, 4, 12, 4); b.MinHeight = 28; });
            Grid.SetColumn(fix, 2);
            g.Children.Add(fix);
        }
        return g;
    }

    private string? ReportText() => _report == null ? null : Diagnostics.ToText(_report, _logTail());

    private void Copy()
    {
        if (ReportText() is not { } text) return;
        try { Clipboard.SetText(text); _state.Text = "Отчет скопирован. Ваш IP, локальные адреса, имя пользователя и почта из него убраны"; }
        catch (Exception) { _state.Text = "Не удалось скопировать: буфер обмена занят другой программой"; }
    }

    private void Report()
    {
        if (ReportText() is not { } text) return;
        if (!DnexDialog.Ask("Открыть черновик на GitHub?",
                "Отчет будет виден всем на GitHub. Ваш внешний IP, адреса локальной сети, имя пользователя Windows и почта из него убраны, " +
                "адреса DNS-серверов остаются. Перед отправкой отчет можно поправить. Нужен аккаунт GitHub.",
                "Открыть", kind: DialogKind.Info, windowTitle: "Сообщить о проблеме"))
            return;
        try { Clipboard.SetText(text); } catch (Exception) { }
        var problems = _report!.Items.Where(i => i.Level == DiagLevel.Bad).Select(i => i.Title).FirstOrDefault();
        var title = $"CHIMERION {UpdateChecker.CurrentVersion}: " + (problems ?? "проблема");
        var body = "Что случилось (опишите своими словами):\n\n\n" + "```\n" + text + "```";
        AutoStart.OpenUrl(Diagnostics.IssueUrl(title, body));
        _state.Text = "Черновик открыт в браузере, полный отчет также в буфере обмена";
        _log("Открыт черновик сообщения о проблеме на GitHub");
    }
}
