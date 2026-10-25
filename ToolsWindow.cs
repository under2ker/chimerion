using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>
/// Окно «Telegram и YouTube»: включение, выключение и обновление сторонних программ (TG WS Proxy и zapret).
/// На каждую программу карточка: состояние, версия, кнопки, строка хода работы или ошибки.
/// </summary>
internal sealed class ToolsWindow
{
    private readonly Window _w;
    private readonly IReadOnlyList<Tool> _tools;
    private readonly Action<string> _log;
    private readonly Action _changed;
    private readonly StackPanel _cards = new();
    private readonly Dictionary<string, string> _status = new();   // последняя строка хода работы по инструменту
    private readonly HashSet<string> _errors = new();
    private bool _busy;

    private static ToolsWindow? _open;

    public static void Show(IReadOnlyList<Tool> tools, Action<string> log, Action changed)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new ToolsWindow(tools, log, changed);
        _open._w.Show();
        _ = _open.CheckUpdatesAsync(quiet: true);
    }

    private ToolsWindow(IReadOnlyList<Tool> tools, Action<string> log, Action changed)
    {
        _tools = tools;
        _log = log;
        _changed = changed;
        _w = DnexDialog.CreateWindow("Telegram и YouTube", 540);
        _w.Closed += (_, _) => _open = null;
        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Telegram и YouTube",
            "Сторонние программы с открытым кодом. DNEX ставит их официальные релизы с GitHub после проверки контрольной суммы", glyph: "⚡"));
        root.Children.Add(_cards);

        var foot = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        var check = DnexDialog.MakeButton("Проверить обновления", "Ghost", () => _ = CheckUpdatesAsync(quiet: false));
        check.Margin = new Thickness(0);
        DockPanel.SetDock(check, Dock.Right);
        foot.Children.Add(check);
        foot.Children.Add(new TextBlock
        {
            Text = string.Join("\n", tools.Select(t => t.Authors)),
            Foreground = (Brush)Res["Muted"], FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(foot);
        _w.Content = root;
        Render();
    }

    private static ResourceDictionary Res => Application.Current.Resources;

    private async Task CheckUpdatesAsync(bool quiet)
    {
        foreach (var t in _tools.Where(t => t.IsInstalled))
        {
            try
            {
                var r = await t.CheckAsync();
                if (!quiet) SetStatus(t, t.UpdateAvailable ? $"Доступна версия {r.Version}" : "Установлена последняя версия", false);
            }
            catch (ToolException e) { if (!quiet) SetStatus(t, e.Message, true); }
        }
        Render();
    }

    private void SetStatus(Tool t, string text, bool error)
    {
        _status[t.Id] = text;
        if (error) _errors.Add(t.Id); else _errors.Remove(t.Id);
        Render();
    }

    /// <summary>Действие с инструментом: кнопки блокируются, ход работы пишется в карточку, итог в журнал.</summary>
    private async Task RunAsync(Tool t, string done, Func<IProgress<string>, Task> action)
    {
        if (_busy) return;
        _busy = true;
        var progress = new Progress<string>(s => SetStatus(t, s, false));
        try
        {
            await action(progress);
            SetStatus(t, done, false);
            _log($"{t.Title}: {done.ToLowerInvariant()}");
        }
        catch (ToolException e) { SetStatus(t, e.Message, true); _log($"{t.Title}: {e.Message}"); }
        catch (Exception e) { SetStatus(t, "Ошибка: " + e.Message, true); _log($"{t.Title}: ошибка {e.Message}"); }
        finally
        {
            _busy = false;
            Render();
            _changed();
        }
    }

    private void Render()
    {
        _cards.Children.Clear();
        foreach (var t in _tools) _cards.Children.Add(Card(t));
    }

    private Border Card(Tool t)
    {
        bool installed = t.IsInstalled, running = installed && t.IsRunning();
        // Своя копия прокси у пользователя: DNEX ее не выключает, но ссылку для Telegram дает (настройки общие)
        var body = new StackPanel();

        var top = new DockPanel { LastChildFill = true };
        var foreign = t is TgProxyTool tg && tg.ForeignCopyRunning();
        if (foreign) running = true;
        var pill = new TextBlock
        {
            Text = foreign ? "работает ваша копия" : running ? "работает" : installed ? "выключено" : "не установлено",
            Foreground = (Brush)Res[running ? "Ok" : "Muted"], FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(pill, Dock.Right);
        top.Children.Add(pill);
        top.Children.Add(new TextBlock { Text = t.Title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        body.Children.Add(top);

        body.Children.Add(Muted(t is TgProxyTool
            ? "Локальный прокси для Telegram Desktop: трафик к серверам Telegram идет через WebSocket. У прокси свой значок в трее и свои настройки"
            : "zapret: обход замедления YouTube и Discord. Нужны права администратора, антивирус может ругаться на драйвер WinDivert", 4));

        var ver = foreign ? "Запущена ваша копия, не из DNEX: выключается через ее значок в трее. Ссылка для Telegram берется из ее настроек"
                : installed ? $"Версия {t.InstalledVersion}" : "Скачается при первом включении";
        if (t.UpdateAvailable) ver += $" · доступна {t.Latest!.Version}";
        body.Children.Add(Muted(ver, 6, t.UpdateAvailable ? "Accent" : "Muted"));

        if (t is ZapretTool z && installed) body.Children.Add(ZapretOptions(z, running));

        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (!foreign)
            row.Children.Add(running
                ? Btn("Выключить", "Ghost", () => _ = RunAsync(t, "Выключено", _ => t.DisableAsync()))
                : Btn("Включить", "Primary", () => _ = RunAsync(t, "Включено", p => t.EnableAsync(p, CancellationToken.None))));
        if (t.UpdateAvailable)
            row.Children.Add(Btn($"Обновить до {t.Latest!.Version}", "Strong",
                () => _ = RunAsync(t, $"Обновлено до {t.Latest!.Version}", p => t.InstallAsync(p, CancellationToken.None))));
        if (t is TgProxyTool && running)
            row.Children.Add(Btn("Подключить Telegram", "Strong", ConnectTelegram));
        foreach (Button b in row.Children) b.IsEnabled = !_busy;
        body.Children.Add(row);

        if (_status.TryGetValue(t.Id, out var st))
            body.Children.Add(Muted(st, 8, _errors.Contains(t.Id) ? "Bad" : "Muted"));

        return new Border { Style = (Style)Res["Card"], Margin = new Thickness(0, 0, 0, 10), Child = body };
    }

    /// <summary>zapret: выбор стратегии (меню, как у «Перепроверить») и обход для игр. Если обход работает, он перезапускается.</summary>
    private FrameworkElement ZapretOptions(ZapretTool z, bool running)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var current = z.State.Strategy ?? ZapretTool.DefaultStrategy;
        var pick = Btn($"Стратегия: {ZapretTool.StrategyTitle(current)} ▾", "Ghost", () => { });
        pick.ToolTip = "Разные стратегии по-разному обходят блокировку. Если YouTube или Discord не работают, попробуйте другую";
        pick.Margin = new Thickness(0, 0, 12, 0);
        pick.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = pick, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var s in z.Strategies())
            {
                var mi = new MenuItem { Header = ZapretTool.StrategyTitle(s), IsCheckable = true, IsChecked = s == current };
                mi.Click += (_, _) => _ = RunAsync(z, $"Стратегия {ZapretTool.StrategyTitle(s)}", async _ =>
                {
                    z.State.Strategy = s;
                    z.SaveState();
                    if (running) { await z.StopAsync(); await z.StartAsync(); }
                });
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        };
        panel.Children.Add(pick);

        var games = new CheckBox
        {
            Content = "Обход и для игр", IsChecked = z.State.Games, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_busy,
            ToolTip = "Порты выше 1023 по TCP и UDP. Нужен, если тормозят игры; в остальных случаях лучше выключить",
        };
        games.Click += (_, _) => _ = RunAsync(z, games.IsChecked == true ? "Обход для игр включен" : "Обход для игр выключен", async _ =>
        {
            z.State.Games = games.IsChecked == true;
            z.SaveState();
            if (running) { await z.StopAsync(); await z.StartAsync(); }
        });
        panel.Children.Add(games);
        return panel;
    }

    private void ConnectTelegram()
    {
        var tg = _tools.OfType<TgProxyTool>().First();
        if (TgProxyTool.ProxyLink() is not { } link)
        {
            SetStatus(tg, "Настройки прокси еще не созданы. Подождите пару секунд после включения и повторите", true);
            return;
        }
        AutoStart.OpenUrl(link);
        SetStatus(tg, "Ссылка отправлена в Telegram: подтвердите подключение к прокси в его окне", false);
        _log("Telegram: открыта ссылка подключения к прокси");
    }

    private static Button Btn(string text, string style, Action click)
    {
        var b = DnexDialog.MakeButton(text, style, click);
        b.Margin = new Thickness(0, 0, 8, 0);
        b.MinWidth = 0;
        return b;
    }

    private static TextBlock Muted(string text, double top, string brush = "Muted") => new()
    {
        Text = text, Foreground = (Brush)Res[brush], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };
}
