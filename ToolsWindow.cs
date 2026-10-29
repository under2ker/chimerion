using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>
/// Окно «Telegram и YouTube»: включение, выключение и обновление сторонних программ (TG WS Proxy и zapret).
/// На каждую программу карточка: название и состояние, описание, настройки строками, кнопки действий,
/// строка хода работы или ошибки. Оформление общее с остальными окнами (Ui).
/// </summary>
internal sealed class ToolsWindow
{
    private readonly Window _w;
    private readonly IReadOnlyList<Tool> _tools;
    private readonly Action<string> _log;
    private readonly Action _changed;
    private readonly Action _relaunchElevated;
    private List<ZapretTool.StrategyResult>? _pick;     // итоги последнего подбора стратегии
    private CancellationTokenSource? _pickCts;          // идет подбор: можно остановить
    private readonly StackPanel _cards = new();
    private readonly Dictionary<string, string> _status = new();   // последняя строка хода работы по инструменту
    private readonly HashSet<string> _errors = new();
    private bool _busy;
    // Ход работы в карточке: строка этапа и полоса прогресса (одна на окно, переносится в карточку занятой программы)
    private readonly TextBlock _stage = Ui.Status();
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 0) };
    private Tool? _busyTool;
    private string _verb = "Загрузка";

    private static ToolsWindow? _open;

    /// <summary>Отправить домен в Smart DNS («Два DNS» → свои домены). Задает главное окно.</summary>
    private static Func<string, string>? _toSmartDns;

    public static void Show(IReadOnlyList<Tool> tools, Action<string> log, Action changed, Action relaunchElevated, Func<string, string>? toSmartDns = null)
    {
        _toSmartDns = toSmartDns;
        if (_open != null) { _open._w.Activate(); return; }
        _open = new ToolsWindow(tools, log, changed, relaunchElevated);
        _open._w.Show();
        _ = _open.CheckUpdatesAsync(quiet: true);
    }

    private ToolsWindow(IReadOnlyList<Tool> tools, Action<string> log, Action changed, Action relaunchElevated)
    {
        _relaunchElevated = relaunchElevated;
        _tools = tools;
        _log = log;
        _changed = changed;
        _w = DnexDialog.CreateWindow("Telegram и YouTube", Ui.Wide);
        _w.Closed += (_, _) => { _pickCts?.Cancel(); _open = null; };
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Telegram и YouTube",
            "Сторонние программы с открытым кодом. CHIMERION ставит их официальные релизы с GitHub после проверки контрольной суммы", glyph: "zap"));
        root.Children.Add(_cards);
        root.Children.Add(Ui.Footer(Ui.Hint(string.Join("\n", tools.Select(t => t.Authors)), 0).With(t => t.FontSize = 11),
            Ui.Action("Проверить обновления", "Ghost", () => _ = CheckUpdatesAsync(quiet: false))));
        _w.Content = Ui.WindowContent(root);
        Render();
    }

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
    private async Task RunAsync(Tool t, string done, Func<IProgress<string>, Task> action, string verb = "Загрузка")
    {
        if (_busy) return;
        _busy = true;
        _busyTool = t;
        _verb = verb;
        _status.Remove(t.Id);
        Render();
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
            _busyTool = null;
            Render();
            _changed();
        }
    }

    /// <summary>
    /// Ход работы в карточке: полоса прогресса (с процентами, если они есть в этапе, например при скачивании,
    /// иначе бегущая) и строка этапа под ней. Элементы общие на окно: перед вставкой отцепляются от старой карточки.
    /// </summary>
    private void AddProgress(StackPanel body, string stage)
    {
        if (_stage.Parent is Panel p1) p1.Children.Remove(_stage);
        if (_bar.Parent is Panel p2) p2.Children.Remove(_bar);
        var pct = System.Text.RegularExpressions.Regex.Match(stage, @"(\d{1,3})%");
        if (pct.Success) { _bar.IsIndeterminate = false; Ui.SetProgress(_bar, int.Parse(pct.Groups[1].Value)); }
        else _bar.IsIndeterminate = true;
        body.Children.Add(_bar);
        Ui.SetStatus(_stage, stage.Length > 0 ? stage : $"{_verb}…");
        _stage.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_stage);
    }

    /// <summary>Изменение настройки zapret: сохранить и, если обход работает, перезапустить его с новой настройкой.</summary>
    private void ChangeZapret(ZapretTool z, string done, Action change, Func<IProgress<string>, Task>? before = null) =>
        _ = RunAsync(z, done, async p =>
        {
            var running = z.IsRunning();
            change();
            z.SaveState();
            if (before != null) await before(p);
            if (running) { p.Report("Перезапускаю обход…"); await z.StopAsync(); await z.StartAsync(); }
        }, "Настройка");

    private void Render()
    {
        _cards.Children.Clear();
        foreach (var t in _tools) _cards.Children.Add(Card(t));
    }

    private Border Card(Tool t)
    {
        bool installed = t.IsInstalled, running = installed && t.IsRunning();
        // Своя копия прокси у пользователя: CHIMERION ее не выключает, но ссылку для Telegram дает (настройки общие)
        var foreign = t is TgProxyTool tg && tg.ForeignCopyRunning();
        if (foreign) running = true;
        var body = new StackPanel();
        body.Children.Add(Ui.CardTitle(t.Title, foreign ? "работает ваша копия" : running ? "работает" : installed ? "выключено" : "не установлено",
            running ? "Ok" : "Muted"));
        body.Children.Add(Ui.Hint(t is TgProxyTool
            ? "Прокси для Telegram Desktop через WebSocket. У него свой значок в трее"
            : "Обход замедления YouTube, Discord и сайтов из списка. Нужны права администратора, антивирус может ругаться на WinDivert"));

        var ver = foreign ? "Запущена ваша копия, не из CHIMERION: выключается через ее значок в трее. Ссылка для Telegram берется из ее настроек"
                : installed ? $"Версия {t.InstalledVersion}" : "Скачается при первом включении";
        if (t.UpdateAvailable) ver += $" · доступна {t.Latest!.Version}";
        body.Children.Add(Ui.Hint(ver, 4, t.UpdateAvailable ? "Accent" : "Muted"));

        if (t is ZapretTool z && installed)
        {
            body.Children.Add(Ui.Separator());
            ZapretRows(z, body);
        }
        // Что нажать: короткий порядок действий для того, кто открыл окно впервые
        if (!foreign && !running)
            body.Children.Add(Ui.Steps(t is TgProxyTool
                ? "Что нажать: «Включить», потом «Подключить Telegram» и подтвердить прокси в окне Telegram"
                : "Что нажать: «Включить». Программа скачает zapret и запустит обход"));
        else if (t is ZapretTool && running && _pick == null)
            body.Children.Add(Ui.Steps("Если какой-то сайт не открывается: добавьте его в «Сайты для обхода», затем нажмите «Подобрать стратегию» и выберите лучшую"));
        else if (t is TgProxyTool && running)
            body.Children.Add(Ui.Steps("Telegram не подключился сам: нажмите «Подключить Telegram» и подтвердите прокси в окне Telegram"));

        var actions = new List<Button>();
        if (!foreign)
            actions.Add(running
                ? Ui.Action("Выключить", "Strong", () => _ = RunAsync(t, "Выключено", _ => t.DisableAsync(), "Выключение"))
                : Ui.Action("Включить", "Primary", () => _ = RunAsync(t, "Включено", p => t.EnableAsync(p, CancellationToken.None), "Включение")));
        if (t.UpdateAvailable)
            actions.Add(Ui.Action($"Обновить до {t.Latest!.Version}", "Strong",
                () => _ = RunAsync(t, $"Обновлено до {t.Latest!.Version}", p => t.InstallAsync(p, CancellationToken.None), "Обновление")));
        if (t is TgProxyTool && running)
            actions.Add(Ui.Action("Подключить Telegram", "Strong", ConnectTelegram));
        if (t is ZapretTool && installed && _pickCts == null)
            actions.Add(Ui.Action("Подобрать стратегию", "Ghost", () => _ = PickAsync((ZapretTool)t)));
        foreach (var b in actions) b.IsEnabled = !_busy;
        if (t is ZapretTool && _pickCts != null)
            actions.Add(Ui.Action("Остановить подбор", "Ghost", () => _pickCts?.Cancel()));
        if (actions.Count > 0) body.Children.Add(Ui.Actions(actions.ToArray()));

        if (t is ZapretTool zr && _pick != null) { body.Children.Add(Ui.Separator()); body.Children.Add(PickResults(zr)); }
        if (_busy && _busyTool == t) AddProgress(body, _status.GetValueOrDefault(t.Id, ""));
        else if (_status.TryGetValue(t.Id, out var st))
            body.Children.Add(Ui.Status().With(s => Ui.SetStatus(s, st, _errors.Contains(t.Id))));
        return Ui.Card(body);
    }

    /// <summary>
    /// Настройки zapret строками: стратегия, сайты для обхода, обход по адресам и его источник, обход для игр.
    /// Любое изменение сохраняется сразу и перезапускает работающий обход.
    /// </summary>
    private void ZapretRows(ZapretTool z, StackPanel body)
    {
        var current = z.State.Strategy ?? ZapretTool.DefaultStrategy;
        var strategy = Ui.Picker(ZapretTool.StrategyTitle(current), b => Menu(b, z.Strategies().Select(s =>
            (ZapretTool.StrategyTitle(s), (string?)null, s == current, (Action)(() => ChangeZapret(z, $"Стратегия {ZapretTool.StrategyTitle(s)}", () => z.State.Strategy = s))))));
        var best = _pick?.FirstOrDefault(r => r.File != null && r.Score > 0);
        body.Children.Add(Ui.Row("Стратегия", best != null
            ? $"Рекомендуем {best.Title}: по подбору она открыла больше всего ({best.Score} из {best.MaxScore})"
            : "Рекомендуем подобрать: «Подобрать стратегию» проверит каждую на YouTube, Discord и ваших сайтах", strategy));
        var auto = Ui.Toggle(z.State.AutoApplyBest);
        auto.Click += (_, _) => { z.State.AutoApplyBest = auto.IsChecked == true; z.SaveState(); };
        body.Children.Add(Ui.Row("Сразу включать лучшую после подбора", "Иначе подбор только покажет итоги, а стратегию выберете вы", auto)
                            .With(g => g.Margin = new Thickness(0, 8, 0, 0)));

        body.Children.Add(Ui.Separator());
        var sites = new Button { Content = $"{z.SitesCount(BypassCatalog.LoadCached())} доменов…", Padding = new Thickness(12, 6, 12, 6), MinHeight = 32, Margin = new Thickness(0) };
        sites.Click += (_, _) => _ = EditSitesAsync(z);
        body.Children.Add(Ui.Row("Сайты для обхода", "Готовые наборы (Meta, X, Discord, Telegram...), большие списки и свои сайты", sites));

        body.Children.Add(Ui.Separator());
        var ipset = Ui.Toggle(z.State.IpSet);
        ipset.Click += (_, _) => ChangeZapret(z, ipset.IsChecked == true ? "Обход по адресам включен" : "Обход по адресам выключен",
            () => z.State.IpSet = ipset.IsChecked == true,
            async p => { if (z.State.IpSet && z.State.IpSetSource is { } id && RemoteLists.Get(id) is { } rl) { p.Report("Скачиваю список адресов…"); await RemoteLists.EnsureAsync(rl); } });
        body.Children.Add(Ui.Row("Обход по адресам (IPSet)", "Рекомендуем выключено. Включайте, если сайты на Cloudflare или хостингах не догружаются (обрываются на 15–30 КБ)", ipset));
        if (z.State.IpSet)
        {
            string SourceTitle(string? id) => id is { } x && RemoteLists.Get(x) is { } rl ? rl.Title : "Сборка zapret";
            IEnumerable<(string? Id, string Title, string Note)> sources = new (string? Id, string Title, string Note)[] { (null, "Сборка zapret", "Около 33 тысяч адресов, обновляется вместе со сборкой") }
                .Concat(RemoteLists.All.Where(l => l.IsIp).Select(l => ((string?)l.Id, l.Title, l.Note)));
            var src = Ui.Picker(SourceTitle(z.State.IpSetSource), b => Menu(b, sources.Select(s => (s.Title, (string?)s.Note, z.State.IpSetSource == s.Id,
                (Action)(() => ChangeZapret(z, $"Адреса для обхода: {s.Title}", () => z.State.IpSetSource = s.Id,
                    async p => { if (s.Id != null && RemoteLists.Get(s.Id) is { } rl) { p.Report("Скачиваю список адресов…"); await RemoteLists.EnsureAsync(rl); } }))))));
            src.IsEnabled = !_busy; // во время другого действия выбор молча не применился бы
            body.Children.Add(Ui.Row("Источник адресов", "Рекомендуем «CDN и хостинги»: он уже других и меньше задевает лишнее", src).With(g => g.Margin = new Thickness(0, 8, 0, 0)));
        }

        body.Children.Add(Ui.Separator());
        var games = Ui.Toggle(z.State.Games);
        games.Click += (_, _) => ChangeZapret(z, games.IsChecked == true ? "Обход для игр включен" : "Обход для игр выключен", () => z.State.Games = games.IsChecked == true);
        body.Children.Add(Ui.Row("Обход и для игр", "Рекомендуем выключено. Включайте, только если тормозят игры (порты выше 1023)", games));

        foreach (var c in new UIElement[] { strategy, sites, ipset, games }) c.IsEnabled = !_busy;
    }

    /// <summary>Меню вариантов под кнопкой выбора (темный стиль меню как у «Перепроверить»).</summary>
    private static void Menu(Button anchor, IEnumerable<(string Title, string? Hint, bool Checked, Action Click)> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (title, hint, on, click) in items)
        {
            var mi = new MenuItem { Header = title, ToolTip = hint, IsCheckable = true, IsChecked = on };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// Подбор стратегии: каждая запускается на пару секунд и проверяется на YouTube и Discord. Без прав администратора
    /// каждая смена спрашивала бы UAC, поэтому сначала предлагаем перезапустить CHIMERION с правами.
    /// </summary>
    private async Task PickAsync(ZapretTool z)
    {
        if (_busy) return;
        if (!DnsSwitcher.IsAdmin())
        {
            if (DnexDialog.Ask("Нужны права администратора",
                    "Подбор перебирает около 20 стратегий, и без прав Windows спрашивала бы разрешение на каждую. " +
                    "Перезапустить CHIMERION с правами администратора? Подключение DNS сохранится.", "Перезапустить", kind: DialogKind.Info, windowTitle: "Подбор стратегии"))
                _relaunchElevated();
            return;
        }
        _busy = true;
        _busyTool = z;
        _verb = "Подбор";
        _status.Remove(z.Id);
        _pickCts = new CancellationTokenSource();
        _pick = null;
        _log("zapret: подбор стратегии");
        try
        {
            _pick = await z.PickAsync(new Progress<string>(s => SetStatus(z, s, false)), _pickCts.Token);
            var best = _pick.FirstOrDefault(r => r.File != null && r.Score > 0);
            var current = z.State.Strategy ?? ZapretTool.DefaultStrategy;
            var apply = best != null && z.State.AutoApplyBest && best.File != current;
            if (apply)
            {
                // Включено «сразу включать лучшую»: перезапуск с ней, выбор в списке ниже остается
                SetStatus(z, $"Включаю лучшую: {best!.Title}…", false);
                z.State.Strategy = best.File;
                z.SaveState();
                if (z.IsRunning()) await z.StopAsync();
                await z.EnableAsync(new Progress<string>(s => SetStatus(z, s, false)), CancellationToken.None);
            }
            var text = best == null ? "Ни одна стратегия не открыла ни YouTube, ни Discord, ни ваши сайты"
                : apply ? $"Включена лучшая: {best.Title}, открылось {best.Score} из {best.MaxScore}. Другую можно выбрать в списке ниже"
                : best.File == current ? $"Лучшая: {best.Title}, открылось {best.Score} из {best.MaxScore}. Она уже включена"
                : $"Лучшая: {best.Title}, открылось {best.Score} из {best.MaxScore}. Включите ее кнопкой ниже";
            SetStatus(z, text, best == null);
            _log("zapret: подбор завершен. " + text);
        }
        catch (OperationCanceledException) { SetStatus(z, "Подбор остановлен, прежняя стратегия возвращена", false); }
        catch (ToolException e) { SetStatus(z, e.Message, true); }
        finally
        {
            _busy = false;
            _busyTool = null;
            _pickCts = null;
            Render();
            _changed();
        }
    }

    /// <summary>Итоги подбора: стратегии с открытыми сервисами первыми, у каждой кнопка «Выбрать».</summary>
    private FrameworkElement PickResults(ZapretTool z)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Section("Итоги подбора", first: true));
        if (_pick!.FirstOrDefault(r => r.File != null && r.Score > 0) is { } top && top.File != (z.State.Strategy ?? ZapretTool.DefaultStrategy))
            panel.Children.Add(Ui.Action($"Включить лучшую: {top.Title}", "Primary", () => _ = RunAsync(z, $"Стратегия {top.Title}", async p =>
            {
                z.State.Strategy = top.File;
                z.SaveState();
                if (z.IsRunning()) await z.StopAsync();
                await z.EnableAsync(p, CancellationToken.None);
            })).With(b => { b.IsEnabled = !_busy; b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 0, 0, 10); }));
        if (_pick!.FirstOrDefault(r => r.File == null) is { } none && none.Score == none.MaxScore)
            panel.Children.Add(Ui.Hint("YouTube, Discord и ваши сайты открываются и без обхода: похоже, zapret вам сейчас не нужен", 0, "Ok"));
        foreach (var r in _pick!.Where(r => r.File != null).Take(8))
        {
            string Mark(bool ok) => ok ? "да" : "нет";
            var sites = r.SitesTotal > 0 ? $" · ваши сайты: {r.SitesOk!.Count} из {r.SitesTotal}" : "";
            var failed = r.SitesFailed is { Count: > 0 } f && r.SitesTotal > 0 ? $" (не догрузились: {string.Join(", ", f.Take(3))}{(f.Count > 3 ? "…" : "")})" : "";
            var info = Ui.Hint($"YouTube: {Mark(r.YouTube)} · Discord: {Mark(r.Discord)}{sites}{failed}" + (r.Score > 0 ? $" · {r.Ms} мс" : ""), 2,
                r.Score == r.MaxScore ? "Ok" : r.Score > 0 ? "Warn" : "Muted");
            UIElement control;
            if (r.Score > 0)
            {
                var file = r.File!;
                var chosen = file == (z.State.Strategy ?? ZapretTool.DefaultStrategy);
                control = Ui.Action(chosen ? "Выбрана" : "Выбрать", chosen ? "Ghost" : "Strong", () => _ = RunAsync(z, $"Стратегия {r.Title}", async p =>
                {
                    z.State.Strategy = file;
                    z.SaveState();
                    if (z.IsRunning()) await z.StopAsync();
                    await z.EnableAsync(p, CancellationToken.None);
                })).With(b => { b.IsEnabled = !_busy && !chosen; b.Margin = new Thickness(0); });
            }
            else control = new TextBlock();
            var row = Ui.Row(r.Title, null, control);
            if (ReferenceEquals(r, _pick!.FirstOrDefault(x => x.File != null && x.Score > 0)))
            {
                // Лучшая по подбору: метка «рекомендуем» рядом с названием
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                var name = (TextBlock)((StackPanel)row.Children[0]).Children[0];
                ((StackPanel)row.Children[0]).Children.Remove(name);
                head.Children.Add(name);
                head.Children.Add(Ui.Badge());
                ((StackPanel)row.Children[0]).Children.Insert(0, head);
            }
            ((StackPanel)row.Children[0]).Children.Add(info);
            row.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>
    /// Окно «Сайты для обхода»: готовые наборы и большие списки галочками (с числом доменов), свои сайты строками.
    /// Сохранение пересобирает список zapret и сразу применяет его.
    /// </summary>
    private async Task EditSitesAsync(ZapretTool z)
    {
        var catalog = await BypassCatalog.LoadAsync();
        var w = DnexDialog.CreateWindow("Сайты для обхода", Ui.Wide);
        w.Owner = _w;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.ShowInTaskbar = false;
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Сайты для обхода",
            "Обход действует на эти сайты и на YouTube с Discord. Сайтам, закрытым для России, нужен Smart DNS, а не обход"));

        // Готовые наборы: две колонки галочек, пояснение в подсказке
        root.Children.Add(Ui.Section("Готовые наборы", first: true));
        var checks = new List<(CheckBox Box, string Id)>();
        var grid = new StackPanel();
        if (catalog == null)
            root.Children.Add(Ui.Card(Ui.Hint("Наборы не загрузились: нет сети и нет встроенной копии. Свои сайты можно задать ниже", 0, "Warn")));
        else
        {
            var chosen = BypassCatalog.Normalize(z.State.Groups);
            var groupsPanel = new StackPanel();
            foreach (var cat in catalog.Groups.GroupBy(g => g.Category.Length > 0 ? g.Category : "Другое"))
            {
                var wrap = new WrapPanel { ItemWidth = 245 }; // две колонки, у каждой строки своя высота
                var inCat = new List<CheckBox>();
                foreach (var g in cat)
                {
                    var cb = Ui.SetCheck(g.Title, g.Domains.Count, g.Note, g.Recommend == "zapret", chosen.Contains(g.Id));
                    cb.Tag = g.Id;
                    checks.Add((cb, g.Id));
                    inCat.Add(cb);
                    wrap.Children.Add(cb);
                }
                groupsPanel.Children.Add(Ui.CategoryHeader(cat.Key, inCat, groupsPanel.Children.Count == 0));
                groupsPanel.Children.Add(wrap);
            }
            grid.Children.Add(groupsPanel);
            var groupsBody = new StackPanel();
            var recIds = catalog.Groups.Where(g => g.Recommend == "zapret").Select(g => g.Id).ToHashSet();
            groupsBody.Children.Add(Ui.Steps("Не знаете, что выбрать: нажмите «Рекомендуемые», затем «Сохранить». Это сайты, которые режут провайдеры. " +
                                             "У каждого раздела есть «выбрать все»", 0).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
            groupsBody.Children.Add(Ui.ChoiceBar(checks.Select(c => c.Box).ToList(), c => recIds.Contains((string)c.Tag)));
            groupsBody.Children.Add(Ui.Scroll(grid, 320));
            groupsBody.Children.Add(Ui.Hint($"Наборы от {catalog.Updated}, источник: {catalog.Source}", 8).With(t => t.FontSize = 11));
            root.Children.Add(Ui.Card(groupsBody));
        }

        // Большие списки от сторонних проектов: качаются у авторов при сохранении и перед каждым запуском обхода
        root.Children.Add(Ui.Section("Большие списки"));
        var bigChecks = new List<(CheckBox Box, string Id)>();
        var bigBody = new StackPanel();
        foreach (var l in RemoteLists.All.Where(l => !l.IsIp))
        {
            var n = RemoteLists.Cached(l).Count;
            var cb = Ui.Toggle(z.State.BigLists.Contains(l.Id)).With(c => c.ToolTip = l.Url);
            bigChecks.Add((cb, l.Id));
            bigBody.Children.Add(Ui.Row(l.Title + (n > 0 ? $" ({n})" : ""), l.Note, cb));
        }
        root.Children.Add(Ui.Card(bigBody));

        root.Children.Add(Ui.Section("Свои сайты"));
        var box = Ui.MultiLine(string.Join(Environment.NewLine, z.UserDomains()), 110);
        var ownBody = new StackPanel();
        ownBody.Children.Add(box);
        ownBody.Children.Add(Ui.Hint("По одному в строке, поддомены включаются. Новые сайты CHIMERION проверит сама и подскажет, " +
                                     "поможет ли им zapret или их лучше отправить в Smart DNS", 8).With(t => t.FontSize = 11));
        // Проверка своих сайтов: сама для новых (когда поле теряет фокус) и по кнопке для всех
        var checkedHosts = new HashSet<string>(z.UserDomains(), StringComparer.OrdinalIgnoreCase);
        var results = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var checkBtn = Ui.Action("Проверить сайты", "Ghost", () => { }).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(-10, 6, 0, 0); });
        List<string> Hosts() => box.Text.Split('\n').Select(SplitDns.Clean).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        async Task CheckSitesAsync(IReadOnlyList<string> hosts)
        {
            if (hosts.Count == 0) return;
            checkBtn.IsEnabled = false;
            results.Children.Clear();
            results.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 6) });
            results.Children.Add(Ui.Hint($"Проверяю {string.Join(", ", hosts.Take(8))}…", 0));
            var checks2 = await Task.WhenAll(hosts.Take(8).Select(async h => { try { return await SiteCheck.CheckAsync(h); } catch (Exception) { return null; } }));
            results.Children.Clear();
            foreach (var r in checks2.Where(r => r != null))
            {
                var (text, brush, smart) = r!.Verdict switch
                {
                    // С работающим обходом «открывается» может быть его заслугой: убрать не советуем
                    SiteVerdict.Ok => (z.IsRunning() ? "открывается" : "открывается и без обхода, его можно убрать из списка", "Ok", false),
                    SiteVerdict.Refused => ("сам закрыт для России: zapret не поможет, отправьте его в Smart DNS", "Warn", true),
                    SiteVerdict.Throttled => ("загрузка обрывается: zapret поможет, если не поможет, включите обход по адресам (IPSet)", "Warn", false),
                    SiteVerdict.Blocked or SiteVerdict.NoDns => ("заблокирован: оставьте в списке, zapret его откроет", "Warn", false),
                    SiteVerdict.Protected => ("закрыт проверкой Cloudflare: откройте в браузере, CHIMERION тут ни при чем", "Muted", false),
                    _ => ("проверить не удалось", "Muted", false),
                };
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                if (smart && _toSmartDns != null)
                {
                    var host = r.Host;
                    Button? to = null;
                    to = Ui.Action("В Smart DNS", "Strong", () =>
                    {
                        var msg = _toSmartDns(host);
                        // Убираем из обхода: этот сайт пойдет через Smart DNS
                        box.Text = string.Join(Environment.NewLine, box.Text.Split('\n').Select(l => l.Trim())
                                       .Where(l => l.Length > 0 && !string.Equals(SplitDns.Clean(l), host, StringComparison.OrdinalIgnoreCase)));
                        to!.IsEnabled = false;
                        to.Content = "Отправлен";
                        to.ToolTip = msg;
                    }).With(b => { b.Padding = new Thickness(10, 3, 10, 3); b.MinHeight = 26; b.Margin = new Thickness(8, 0, 0, 0); });
                    DockPanel.SetDock(to, Dock.Right);
                    row.Children.Add(to);
                }
                row.Children.Add(Ui.Hint($"{r.Host}: {text}", 0, brush).With(t => t.VerticalAlignment = VerticalAlignment.Center));
                results.Children.Add(row);
            }
            foreach (var h in hosts) checkedHosts.Add(h);
            checkBtn.IsEnabled = true;
        }
        checkBtn.Click += (_, _) => _ = CheckSitesAsync(Hosts());
        box.LostKeyboardFocus += (_, _) => _ = CheckSitesAsync(Hosts().Where(h => !checkedHosts.Contains(h)).ToList());
        ownBody.Children.Add(checkBtn);
        ownBody.Children.Add(results);
        root.Children.Add(Ui.Card(ownBody, bottom: 0));

        var err = Ui.Hint("", 0, "Bad");
        var save = Ui.Action("Сохранить", "Primary", () =>
        {
            var lines = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var bad = lines.Where(l => SplitDns.Clean(l).Length == 0).ToList();
            if (bad.Count > 0) { err.Text = "Не похоже на домен: " + string.Join(", ", bad.Take(3)); return; }
            var groups = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList();
            var big = bigChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList();
            // Идет другое действие (подбор стратегии, обновление): RunAsync его не прервет и молча пропустит сохранение.
            // Раньше окно при этом закрывалось, и введенные сайты терялись
            if (_busy) { err.Text = "Подождите: идет другое действие с zapret. Сохраните, когда оно закончится"; return; }
            w.Close();
            _ = RunAsync(z, $"Сайты для обхода сохранены: наборов {groups.Count}, больших списков {big.Count}, своих {lines.Count}", async p =>
            {
                if (big.Count > 0) p.Report("Скачиваю большие списки…");
                await z.SaveSitesAsync(lines, groups, big, catalog);
            });
        });
        var cancel = Ui.Action("Отмена", "Ghost", () => w.Close()).With(b => b.IsCancel = true);
        root.Children.Add(Ui.Footer(err, save, cancel));
        w.Content = Ui.WindowContent(root);
        w.ShowDialog();
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
}
