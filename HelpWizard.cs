using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Мастер «Не открывается…»: человек выбирает ChatGPT, Claude, Gemini или вписывает сайт, программа по шагам
/// проверяет все, что может мешать, и показывает ответ на одну проблему с кнопкой «Исправить», а не общий отчет.
/// Шаги для ИИ: подключение, куда системный DNS ведет сервис, работает ли сервис через сервер, нет ли сбоя у самого
/// сервиса, что в системе мешает (VPN, безопасный DNS браузера, прокси, hosts). Для сайта: открывается ли он здесь и
/// снаружи России, режет ли провайдер, закрыт ли для России.
/// </summary>
internal sealed class HelpWizard
{
    private static HelpWizard? _open;
    private readonly Window _w;
    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<DnsServer>> _servers;
    private readonly Func<string, Task> _fix;
    private readonly Func<string, string, Task> _siteAction;
    private readonly StackPanel _steps = new();
    private readonly TextBlock _verdict = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _site = new() { Tag = "Или адрес сайта, например figma.com" };
    private bool _busy;

    public static void Show(AppSettings settings, Window? owner, Func<IReadOnlyList<DnsServer>> servers, Func<string, Task> fix, Func<string, string, Task> siteAction)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new HelpWizard(settings, owner, servers, fix, siteAction);
        _open._w.Show();
    }

    private HelpWizard(AppSettings settings, Window? owner, Func<IReadOnlyList<DnsServer>> servers, Func<string, Task> fix, Func<string, string, Task> siteAction)
    {
        _settings = settings; _servers = servers; _fix = fix; _siteAction = siteAction;
        _w = DnexDialog.CreateWindow("Не открывается", Ui.Wide);
        if (owner is { IsVisible: true }) { _w.Owner = owner; _w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        _w.Closed += (_, _) => _open = null;

        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Что не открывается?",
            "Выберите сервис или впишите сайт. Программа проверит по шагам все, что может мешать, и подскажет, что сделать", glyph: "?"));
        var pick = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var t in settings.Targets.Where(t => t.Enabled))
        {
            var target = t;
            pick.Children.Add(Ui.Action(t.Name, "Strong", () => _ = RunServiceAsync(target)).With(b => b.Margin = new Thickness(0, 0, 8, 8)));
        }
        root.Children.Add(pick);
        var row = new DockPanel();
        var go = Ui.Action("Проверить сайт", "Ghost", () => _ = RunSiteAsync()).With(b => b.Margin = new Thickness(8, 0, 0, 0));
        DockPanel.SetDock(go, Dock.Right);
        row.Children.Add(go);
        _site.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) _ = RunSiteAsync(); };
        row.Children.Add(_site);
        root.Children.Add(row);

        root.Children.Add(Ui.Section("Что нашлось"));
        var body = new StackPanel();
        body.Children.Add(_verdict);
        body.Children.Add(_steps);
        root.Children.Add(Ui.Card(Ui.Scroll(body, 420), bottom: 0));
        _verdict.Text = "Выберите, что не открывается";
        var close = Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        root.Children.Add(Ui.Footer(null, close));
        _w.Content = Ui.WindowContent(root);
    }

    private enum Lvl { Ok, Warn, Bad, Info }

    private void Step(Lvl lvl, string title, string detail = "", string? action = null, string? actionText = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var color = lvl switch { Lvl.Ok => "Ok", Lvl.Warn => "Warn", Lvl.Bad => "Bad", _ => "Muted" };
        g.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Ui.B(color), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(title), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(detail), Foreground = Ui.B("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (action != null && actionText != null)
        {
            var b = Ui.Action(actionText, lvl == Lvl.Bad ? "Strong" : "Ghost", () => { });
            b.Margin = new Thickness(12, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Top; b.Padding = new Thickness(12, 4, 12, 4); b.MinHeight = 28;
            b.Click += async (_, _) => { b.IsEnabled = false; try { await DoAsync(action); } catch (Exception e) { DnexDialog.Show("Не получилось", e.Message, DialogKind.Error, "Не открывается"); } finally { b.IsEnabled = true; } };
            Grid.SetColumn(b, 2);
            g.Children.Add(b);
        }
        _steps.Children.Add(g);
    }

    private async Task DoAsync(string action)
    {
        if (action == "flush")
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(10000);
            DnexDialog.Show("Кэш DNS очищен", "Кэш DNS Windows очищен. В браузере кэш свой: закройте его полностью и откройте снова, " +
                "или в Chrome и Edge откройте chrome://net-internals/#dns и нажмите «Clear host cache».", DialogKind.Info, "Не открывается");
        }
        else if (action.StartsWith("zapret:") || action.StartsWith("smartdns:"))
        {
            var i = action.IndexOf(':');
            await _siteAction(action[(i + 1)..], action[..i]);
        }
        else if (action.StartsWith("open:")) AutoStart.OpenUrl(action[5..]);
        else await _fix(action);
    }

    private void Begin(string what)
    {
        _steps.Children.Clear();
        _verdict.Text = $"Проверяю {what}…";
        _verdict.Foreground = Ui.B("Text");
    }

    private void End(Lvl lvl, string text)
    {
        _verdict.Text = text;
        _verdict.Foreground = Ui.B(lvl switch { Lvl.Ok => "Ok", Lvl.Warn => "Warn", Lvl.Bad => "Bad", _ => "Text" });
    }

    private async Task RunServiceAsync(CheckTarget t)
    {
        if (_busy) return;
        _busy = true;
        Begin(t.Name);
        try { await ServiceStepsAsync(t); }
        catch (Exception e) { End(Lvl.Bad, "Проверка не удалась: " + e.Message); }
        finally { _busy = false; }
    }

    private async Task ServiceStepsAsync(CheckTarget t)
    {
        var checker = new ServerChecker(_settings);

        // 1. Подключение
        var active = DnsSwitcher.GetActive();
        if (active == null)
        {
            Step(Lvl.Bad, "CHIMERION не подключен", $"Без Smart DNS {t.Name} из России не откроется", "connect", "Подключить лучший");
            End(Lvl.Bad, "Причина: нет подключения. Нажмите «Подключить лучший»");
            return;
        }
        Step(Lvl.Ok, $"Подключено: {active.Name}", active.BackupName != null ? $"запасной сервер: {active.BackupName}" : "");

        // 2. Сбой у самого сервиса
        try
        {
            var st = await Crowd.ServiceStatusAsync();
            if (st.TryGetValue(t.Name, out var s) && s.Indicator is "major" or "critical")
            {
                Step(Lvl.Warn, $"У {t.Name} сбой у всех", $"Страница статуса: {s.Text}. DNS и сервер тут ни при чем", "open:" + (t.Name == "Claude" ? "https://status.claude.com" : "https://status.openai.com"), "Страница статуса");
                End(Lvl.Warn, $"Причина: у {t.Name} сбой у всех. Подождите, пока его починят");
                return;
            }
            if (st.ContainsKey(t.Name)) Step(Lvl.Ok, $"У {t.Name} сбоев нет", st[t.Name].Text);
        }
        catch (Exception) { Step(Lvl.Info, "Страницу статуса проверить не удалось"); }

        // 3. Куда системный DNS ведет сервис
        IPAddress[] sys;
        try { sys = await Dns.GetHostAddressesAsync(t.Host); } catch (Exception) { sys = Array.Empty<IPAddress>(); }
        HashSet<IPAddress> reference;
        try { reference = await checker.ResolveReferenceAsync(t.Host, CancellationToken.None); } catch (Exception) { reference = new(); }
        bool proxied = sys.Length > 0 && reference.Count > 0 && !sys.Any(reference.Contains);
        var issues = SystemCheck.Run(active.OnAdapters);
        if (sys.Length == 0)
            Step(Lvl.Bad, $"DNS системы не находит {t.Host}", "Сервер не ответил или DNS сбросила другая программа", "reconnect", "Переподключить");
        else if (!proxied)
        {
            var hint = issues.Count > 0 ? "Скорее всего, мешает то, что указано ниже" : "DNS сбросила другая программа или браузер использует свой DNS";
            Step(Lvl.Bad, $"Система ведет {t.Name} мимо Smart DNS", $"{t.Host} → {string.Join(", ", sys.Take(2))}, это обычные адреса. {hint}", "reconnect", "Переподключить");
        }
        else Step(Lvl.Ok, $"Система ведет {t.Name} через Smart DNS", $"{t.Host} → {string.Join(", ", sys.Take(2))}");

        // 4. Помехи в системе
        foreach (var i in issues) Step(Lvl.Warn, "Мешает: " + i.Title, i.Fix);

        // 5. Работает ли сервис через сервер
        var server = _servers().FirstOrDefault(x => x.Id == active.ServerId);
        if (server != null)
        {
            await new ServerChecker(_settings, new ServerChecker.CheckOptions(Only: new[] { t.Name })).CheckAsync(server, CancellationToken.None);
            var chip = server.Chips.FirstOrDefault(c => c.Name == t.Name);
            if (chip?.State == "ok") Step(Lvl.Ok, $"Через {server.ShortName} {t.Name} открывается");
            else Step(Lvl.Bad, $"Через {server.ShortName} {t.Name} не открывается", server.LastError ?? "", "switch", "Сменить сервер");
            if (chip?.State != "ok")
            {
                End(Lvl.Bad, $"Причина: сервер перестал открывать {t.Name}. Нажмите «Сменить сервер»");
                return;
            }
        }

        // Итог
        if (sys.Length == 0 || !proxied) { End(Lvl.Bad, issues.Count > 0 ? "Причина: мешает программа или настройка из списка ниже" : "Причина: система идет мимо Smart DNS. Нажмите «Переподключить»"); }
        else if (issues.Count > 0) End(Lvl.Warn, "Сервер работает, но мешает настройка из списка. Уберите ее и обновите страницу");
        else
        {
            Step(Lvl.Info, "Старый кэш в браузере", "Все проверки пройдены. Браузер мог запомнить прежний адрес: очистите кэш DNS и перезапустите браузер", "flush", "Очистить кэш DNS");
            End(Lvl.Ok, $"С подключением все в порядке, {t.Name} должен открываться. Очистите кэш и перезапустите браузер");
        }
    }

    private async Task RunSiteAsync()
    {
        var host = SiteCheck.HostOf(_site.Text);
        if (_busy || host.Length == 0) return;
        _busy = true;
        Begin(host);
        try
        {
            var site = SiteWatch.SiteOf(host) ?? host;
            bool smart = SplitDns.SuffixDomains.Any(d => site.Equals(d, StringComparison.OrdinalIgnoreCase));
            if (smart && DnsSwitcher.GetActive() == null)
            {
                Step(Lvl.Bad, "CHIMERION не подключен", $"{host} открывается из России только через Smart DNS", "connect", "Подключить лучший");
                End(Lvl.Bad, "Причина: нет подключения. Нажмите «Подключить лучший»");
                return;
            }
            var (v, detail) = await SiteWatch.CheckAsync(host, smart, CancellationToken.None);
            var rep = await SiteCheck.CheckAsync(host);
            foreach (var l in rep.Lines) Step(Lvl.Info, l);
            switch (v)
            {
                case SiteWatch.Verdict.Ok when rep.Verdict == SiteVerdict.Throttled:
                    Step(Lvl.Bad, "Загрузка обрывается: провайдер замедляет сайт", rep.Advice, "zapret:" + host, "В zapret");
                    End(Lvl.Bad, "Причина: замедление провайдером. Помогает zapret");
                    break;
                case SiteWatch.Verdict.Ok:
                    Step(Lvl.Ok, "Сайт открывается с этого компьютера", rep.Advice, "flush", "Очистить кэш DNS");
                    End(Lvl.Ok, "Сайт открывается. Если в браузере нет, очистите кэш и проверьте расширения");
                    break;
                case SiteWatch.Verdict.DownForAll:
                    Step(Lvl.Warn, "Сайт лежит у всех", detail ?? "");
                    End(Lvl.Warn, "Причина: сайт не работает и снаружи России. Сеть ни при чем");
                    break;
                case SiteWatch.Verdict.Blocked:
                    Step(Lvl.Bad, "Не открывается у вас, снаружи работает", "Провайдер блокирует сайт. DNS это не исправит", "zapret:" + host, "В zapret");
                    End(Lvl.Bad, "Причина: блокировка провайдером. Добавьте сайт в zapret");
                    break;
                case SiteWatch.Verdict.Refused or SiteWatch.Verdict.NotProxied:
                    Step(Lvl.Bad, "Сайт закрыт для России", "Нужен Smart DNS, который его подменяет", "smartdns:" + host, "В Smart DNS");
                    End(Lvl.Bad, "Причина: сайт сам не пускает из России. Отправьте его в Smart DNS");
                    break;
                default:
                    Step(Lvl.Bad, "DNS не находит адрес сайта", "Проверьте адрес или смените общий DNS");
                    End(Lvl.Bad, "Причина: DNS не нашел адрес");
                    break;
            }
        }
        catch (Exception e) { End(Lvl.Bad, "Проверка не удалась: " + e.Message); }
        finally { _busy = false; }
    }
}
