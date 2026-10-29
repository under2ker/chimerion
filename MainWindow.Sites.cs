using System.Windows.Threading;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Наблюдение за сайтами, которыми пользуется человек (SiteWatch), и сбои самих ИИ-сервисов</summary>
public partial class MainWindow
{
    private readonly SiteWatch _sites = SiteWatch.Load();
    private readonly DispatcherTimer _sitesTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _sitesBusy;
    /// <summary>Уведомлений о сайтах за последний час: больше трех подряд не показываем, только журнал.</summary>
    private readonly Queue<DateTime> _siteAlerts = new();
    /// <summary>Как часто перепроверять сайт, который открывается.</summary>
    private static readonly TimeSpan SiteEvery = TimeSpan.FromMinutes(30);

    private void StartSiteWatch()
    {
        _sitesTimer.Tick += async (_, _) => { CheckZapretHealth(); await SitesRoundAsync(); };
        _sitesTimer.Start();
        _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(TimeSpan.FromSeconds(45)); await SitesRoundAsync(); });
    }

    private string? _zapretWarned;

    /// <summary>
    /// Раз в 5 минут: обход включен, но winws пропал, или рядом работает служба zapret из service.bat (две копии
    /// мешают друг другу, обход «работает минут 20 и перестает»). Об одной и той же проблеме сообщаем один раз.
    /// </summary>
    private void CheckZapretHealth()
    {
        if (_tools.OfType<ZapretTool>().FirstOrDefault() is not { IsInstalled: true, State.Enabled: true } z) { _zapretWarned = null; return; }
        var problem = !z.IsRunning() ? "YouTube и Discord: обход zapret остановился. Включите его в трее или в «Telegram и YouTube»" : ZapretTool.Conflict();
        if (problem == _zapretWarned) return;
        _zapretWarned = problem;
        UpdateToolsTray();
        if (problem == null) return;
        Notify(problem, "Warn", logLine: problem, feature: FeatureBypass);
        if (_tray != null && !IsVisible) _balloonClick = () => { ShowFromTray(); ToolsToggle_Click(this, new System.Windows.RoutedEventArgs()); };
    }

    /// <summary>Домены, которые должны идти через Smart DNS: на них отказ по региону значит, что сервер перестал подменять.</summary>
    private bool ViaSmartDns(string site) =>
        SplitDns.SuffixDomains.Any(d => site.Equals(d, StringComparison.OrdinalIgnoreCase)) ||
        (_settings.SplitDns.Enabled && SplitDns.Namespaces(_settings).Any(n => n.TrimStart('.').Equals(site, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Круг наблюдения: имена из кэша DNS в список, проверка тех, кого пора. Без сети, при стороннем VPN и в момент
    /// подключения не проверяем: результат был бы не про сайты.
    /// </summary>
    private async Task SitesRoundAsync(bool force = false)
    {
        if (!_settings.SiteWatch || _sitesBusy || _busyConn) return;
        if (!await SystemCheck.NetworkUpAsync() || SystemCheck.ActiveVpns().Count > 0) return;
        _sitesBusy = true;
        try
        {
            var now = DateTime.Now;
            _sites.Observe(await Task.Run(SiteWatch.CachedNames), now);
            var due = force ? _sites.Sites.Values.Where(s => s.Seen >= SiteWatch.SeenToWatch && !_sites.Ignored.Contains(s.Host) && !SiteWatch.IsNotSite(s)).ToList()
                            : _sites.Due(now, SiteEvery);
            bool connected = DnsSwitcher.GetActive() != null;
            var changes = new List<SiteWatch.Change>();
            foreach (var s in due)
            {
                // Домены ИИ без подключения закрыты для России и должны быть закрыты: это не сбой, а отключенный Smart DNS.
                // Пока подключение есть, за ними следит проверка сервисов, а здесь отказ по региону значит, что сервер их не подменяет
                bool smart = ViaSmartDns(s.Host);
                // Домены нейросетей проверяет мониторинг сервисов; второй поток запросов к ним с вашего адреса не нужен
                if (SplitDns.SuffixDomains.Any(d => s.Host.Equals(d, StringComparison.OrdinalIgnoreCase))) continue;
                if (smart && !connected) continue;
                var (v, detail) = await SiteWatch.CheckAsync(s.Host, smart, CancellationToken.None);
                if (_sites.Apply(s, v, detail, DateTime.Now) is { } ch) changes.Add(ch);
            }
            // Несколько сайтов упали в одном круге: это скорее сбой сети, чем блокировка каждого. Не тревожим, а перепроверим
            var downs = changes.Where(c => c.Down).ToList();
            if (downs.Count >= 3)
            {
                Log($"Проверка сайтов: сразу {downs.Count} перестали открываться, похоже на сбой сети. Перепроверю через 5 минут");
                foreach (var c in downs) { c.Site.Down = false; c.Site.Fails = SiteWatch.FailsToAlert - 1; }
                changes.RemoveAll(c => c.Down);
            }
            foreach (var ch in changes) ReportSite(ch);
            _sites.Save();
            SitesWindow.Refresh();
        }
        catch (Exception ex) { Log("Наблюдение за сайтами: " + ex.Message); }
        finally { _sitesBusy = false; }
    }

    private void ReportSite(SiteWatch.Change ch)
    {
        var s = ch.Site;
        var text = ch.Down
            ? $"{s.Host}: {SiteWatch.Describe(s.State)}. {SiteWatch.Advice(s.State)}"
            : $"{s.Host} снова открывается";
        Log("Проверка сайтов: " + text);
        while (_siteAlerts.Count > 0 && DateTime.Now - _siteAlerts.Peek() > TimeSpan.FromHours(1)) _siteAlerts.Dequeue();
        if (_siteAlerts.Count >= 3) return;
        _siteAlerts.Enqueue(DateTime.Now);
        if (!ch.Down) { Notify(text, "Ok", logLine: null, feature: FeatureSites); return; }
        Notify(text, s.State == SiteWatch.Verdict.DownForAll ? "Warn" : "Bad", logLine: null, feature: FeatureSites);
        if (_tray != null && !IsVisible) _balloonClick = () => { ShowFromTray(); OpenSites(); };
    }

    /// <summary>Кнопка «Помощь» внизу: меню из мастера «Не открывается», списка сайтов и настройки телефона.</summary>
    private void HelpToggle_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        HelpToggle.IsChecked = false;
        OpenHelp(HelpWindow.Tab.NotOpening);
    }

    /// <summary>Окно «Помощь» на нужной вкладке. Страницы создаются при первом выборе вкладки.</summary>
    private void OpenHelp(HelpWindow.Tab tab) => HelpWindow.Show(this, tab, new Func<System.Windows.FrameworkElement>[]
    {
        () => HelpWizard.Create(_settings, () => _servers.ToList(), DiagFixAsync, SiteAction),
        () => SitesWindow.Create(_sites, async force => await SitesRoundAsync(force), SiteAction, SiteHandled),
        () => PhoneWindow.Create(_servers.ToList(), Log),
    });

    private void OpenHelpWizard() => OpenHelp(HelpWindow.Tab.NotOpening);
    private void OpenPhone() => OpenHelp(HelpWindow.Tab.Phone);
    private void OpenSites() => OpenHelp(HelpWindow.Tab.Watched);

    /// <summary>Что уже сделано с сайтом из окна «Сайты»: чтобы вместо кнопки показать отметку. Null — ничего.</summary>
    private string? SiteHandled(string host)
    {
        if (_tools.OfType<ZapretTool>().FirstOrDefault() is { } z && z.UserDomains().Contains(host, StringComparer.OrdinalIgnoreCase))
            return z.IsRunning() ? "Добавлен в zapret" : "Добавлен в zapret, включите обход";
        if (_settings.SplitDns.ExtraAiDomains.Contains(host, StringComparer.OrdinalIgnoreCase))
            return _settings.SplitDns.Enabled ? "Добавлен в Smart DNS" : "Добавлен в Smart DNS, включите «Два DNS»";
        return null;
    }

    /// <summary>Действия из окна «Сайты»: в zapret, в Smart DNS, не следить.</summary>
    private async Task SiteAction(string host, string action)
    {
        switch (action)
        {
            case "zapret" when _tools.OfType<ZapretTool>().FirstOrDefault() is { } z:
                await z.SaveSitesAsync(z.UserDomains().Append(host), z.State.Groups, z.State.BigLists, BypassCatalog.LoadCached());
                Log($"{host}: добавлен в свои сайты zapret");
                ShowToast(z.IsRunning() ? $"{host} добавлен в обход zapret" : $"{host} добавлен в свои сайты zapret. Включите обход в «Telegram и YouTube»",
                          z.IsRunning() ? "Ok" : "Warn");
                break;
            case "smartdns":
                SendToSmartDns(host);
                break;
            case "ignore":
                _sites.Ignored.Add(host);
                _sites.Sites.Remove(host);
                _sites.Save();
                break;
        }
    }

    /// <summary>
    /// Сбой у самого сервиса (страница статуса OpenAI или Anthropic): тогда сервер ни при чем, менять его бесполезно.
    /// Возвращает текст для уведомления или null, если у сервисов все в порядке или статус узнать не удалось.
    /// </summary>
    private static async Task<string?> ServiceOutageAsync(IEnumerable<string> services)
    {
        try
        {
            var st = await Crowd.ServiceStatusAsync();
            var bad = services.Where(n => st.TryGetValue(n, out var s) && s.Indicator is "major" or "critical").ToList();
            if (bad.Count == 0) return null;
            return string.Join("; ", bad.Select(n => $"у {n} сбой у всех: {st[n].Text}"));
        }
        catch (Exception) { return null; }
    }
}
