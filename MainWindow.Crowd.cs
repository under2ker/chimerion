using System.Windows;
using System.Windows.Threading;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Общая карта серверов: отчеты о проверках с согласия пользователя и рейтинг для его провайдера (Crowd)</summary>
public partial class MainWindow
{
    private readonly Crowd.Queue _crowdQueue = Crowd.LoadQueue();
    private Crowd.Stats? _crowd = Crowd.LoadStats();
    private readonly DispatcherTimer _crowdTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// ID устройства в заголовке окна: человек видит ровно то, что уходит в счетчик. В узком компактном виде первые 8 знаков.
    /// Показывается всегда, в том числе в режиме скрытия данных: личных данных в ID нет. На копии данных для снимков выдуманный ID.
    /// </summary>
    private void UpdateTitleId()
    {
        var id = NoAutoDns ? "a1b2c3d4e5f60718293a4b5c6d7e8f90" : UsageCounter.DeviceId(_settings);
        TitleId.Text = "ID " + (_compact ? id[..8] + "…" : id);
        TitleId.ToolTip = $"ID устройства: {id}. В счетчик пользователей уходят он, версия и время работы, данных о вас там нет. Щелчок копирует";
    }

    private void TitleId_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Ui.CopyText(UsageCounter.DeviceId(_settings))) ShowToast("ID скопирован");
        else ShowToast("Не удалось скопировать: буфер обмена занят другой программой", "Warn");
    }

    /// <summary>При запуске: таймер обмена с общей картой и, один раз, вопрос о согласии.</summary>
    private DateTime _usageTick = DateTime.UtcNow;
    private bool _usageAtStart = true;

    private void StartCrowd()
    {
        _settings.UsageRuns++;
        _crowdTimer.Tick += async (_, _) => await CrowdSyncAsync();
        _crowdTimer.Start();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            AskCrowdConsent();
            await CrowdSyncAsync();
        });
    }

    /// <summary>
    /// Один раз спросить, помогать ли общей карте. Не при запуске в трей вместе с Windows: окна никто не видит,
    /// спросим, когда пользователь откроет программу.
    /// </summary>
    private void AskCrowdConsent()
    {
        if (!IsVisible) return;
        if (_settings.CrowdConsent == null)
        {
            bool yes = DnexDialog.Ask("Помогать общей карте серверов?",
                "Раз в час программа будет отправлять итоги проверок: адрес сервера, открываются ли через него нейросети " +
                "и открываются ли YouTube, Discord, Telegram и еще несколько популярных сервисов. Ваш IP и ваши сайты не уходят.\n\n" +
                "Взамен она подскажет, какой сервер лучше работает у вашего провайдера, и предупредит, если провайдер начал что-то блокировать. " +
                "Изменить можно в настройках, во вкладке «Приватность».",
                "Помогать", "Не сейчас", DialogKind.Info, windowTitle: "Общая карта серверов");
            _settings.CrowdConsent = yes;
            _settings.CrowdSites = yes;
            Log(yes ? "Общая карта серверов: отчеты о проверках включены" : "Общая карта серверов: отчеты не отправляются");
        }
        else if (_settings.CrowdConsent == true && _settings.CrowdSites == null)
        {
            // Согласие давали на отчеты о серверах: о сервисах спрашиваем отдельно, молча расширять его нельзя
            bool yes = DnexDialog.Ask("Предупреждать о новых блокировках?",
                "Программа может сообщать общей карте, открываются ли у вас YouTube, Discord, Telegram и еще несколько популярных сервисов. " +
                "Ваш IP и ваши сайты не уходят.\n\nКогда у пользователей вашего провайдера сервис перестанет открываться, программа скажет об этом и подскажет, что включить.",
                "Сообщать", "Не нужно", DialogKind.Info, windowTitle: "Общая карта серверов");
            _settings.CrowdSites = yes;
            Log(yes ? "Общая карта: отчеты о популярных сервисах включены" : "Общая карта: отчеты о популярных сервисах не отправляются");
        }
        else return;
        try { Storage.Save("settings.json", _settings); } catch (Exception) { }
    }

    /// <summary>После круга проверки сайтов: итоги по популярным сервисам в очередь общей карты (если человек согласился).</summary>
    private void CrowdCollectSites(IEnumerable<SiteWatch.Site> checkedSites)
    {
        if (_settings.CrowdConsent != true || _settings.CrowdSites != true) return;
        var reports = Incidents.Collect(checkedSites, BypassOn());
        if (reports.Count == 0) return;
        Crowd.EnqueueSites(_crowdQueue, reports);
        Crowd.SaveQueue(_crowdQueue);
    }

    /// <summary>Работает ли сейчас обход zapret: от этого зависит, о какой блокировке речь.</summary>
    private bool BypassOn()
    {
        var z = _tools.OfType<ZapretTool>().FirstOrDefault();
        return z != null && z.IsInstalled && z.IsRunning();
    }

    /// <summary>
    /// Новые блокировки у провайдера из свежего рейтинга. Говорим только о сервисах, которыми человек пользуется
    /// (они есть в наблюдении за сайтами), и о каждой не чаще раза в сутки. Без наблюдения за сайтами молчим:
    /// иначе пришлось бы тревожить всех обо всем.
    /// </summary>
    private void NotifyIncidents()
    {
        if (_crowd is not { Incidents.Count: > 0 } st || !_settings.SiteWatch) return;
        var used = _sites.Sites.Values.Where(s => s.Seen >= SiteWatch.SeenToWatch).Select(s => s.Host);
        var fresh = Incidents.ToNotify(st.Incidents, st.Asn, used, BypassOn(), _settings.IncidentsNotified, DateTime.Today);
        if (fresh.Count == 0) return;
        foreach (var i in fresh)
        {
            _settings.IncidentsNotified.Add(Incidents.Key(st.Asn, i, DateTime.Today));
            var text = Incidents.Describe(i, Crowd.NetName(st));
            Notify(text, "Warn", logLine: $"Общая карта: {text} (неудачных {i.Fail}% из {i.Checks})", feature: FeatureSites);
        }
        if (_settings.IncidentsNotified.Count > 50) _settings.IncidentsNotified.RemoveRange(0, _settings.IncidentsNotified.Count - 50);
        try { Storage.Save("settings.json", _settings); } catch (Exception) { }
    }

    /// <summary>После проверки серверов: итоги в очередь. При VPN и сбое сети результат не про сервер, не отправляем.</summary>
    private void CrowdCollect(IEnumerable<DnsServer> checkedServers)
    {
        if (_settings.CrowdConsent != true) return;
        foreach (var s in checkedServers) Crowd.Enqueue(_crowdQueue, s);
        Crowd.SaveQueue(_crowdQueue);
    }

    /// <summary>Отправить накопленное (раз в час) и обновить рейтинг (раз в час). Рейтинг смотрят и без согласия: это только чтение.</summary>
    private async Task CrowdSyncAsync()
    {
        try
        {
            if (_settings.CrowdConsent == true && await Crowd.SendAsync(_crowdQueue)) Crowd.SaveQueue(_crowdQueue);
        }
        catch (Exception) { /* сервер недоступен: отправим в следующий раз */ }
        try
        {
            UsageCounter.AddSession(_settings, DateTime.UtcNow - _usageTick);
            _usageTick = DateTime.UtcNow;
            if (await UsageCounter.PingAsync(_settings, DateTime.Now, _usageAtStart)) _usageAtStart = false;
            Storage.Save("settings.json", _settings);
        }
        catch (Exception) { /* отметимся при следующей попытке */ }
        try
        {
            if (_crowd == null || DateTime.Now - _crowd.Fetched > Crowd.FetchEvery)
            {
                _crowd = await Crowd.FetchAsync();
                _topSig = null;
                UpdateTopList();
                NotifyIncidents();
                await CrowdDiscoverAsync();
            }
        }
        catch (Exception) { }
        await SupportSyncAsync();
    }

    /// <summary>
    /// Серверы, которые нашли и проверили другие пользователи: новые для этого списка добавляются и сразу
    /// проверяются здесь. Сервер из мусора не возвращается, а в рабочие попадает только после своей проверки.
    /// </summary>
    private async Task CrowdDiscoverAsync()
    {
        List<Crowd.Found> found;
        try { found = await Crowd.DiscoverAsync(); }
        catch (Exception) { return; }
        var keys = _servers.Select(Crowd.KeyOf).Where(k => k != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<DnsServer>();
        foreach (var f in found.Where(f => !keys.Contains(f.Server)).Take(10))
        {
            if (Crowd.ToServer(f) is not { } s || _junk.ContainsKey(s.Key) || !_known.Add(s.Key)) continue;
            _servers.Add(s);
            added.Add(s);
        }
        if (added.Count == 0) return;
        Log($"Общая карта: добавлено серверов {added.Count}, которые работают у других пользователей. Проверяю");
        SaveServers();
        foreach (var s in added)
        {
            if (_busyConn) break;
            await CheckOneAsync(s, null);
        }
    }

    /// <summary>Строка «У вашего провайдера» для блока лучших: сервер, доля удачных проверок в той же сети и их число.</summary>
    private ServerRanking.TopPick? ProviderPick()
    {
        if (_crowd == null || Crowd.BestForProvider(_crowd, _servers) is not { } best) return null;
        return new ServerRanking.TopPick($"У вашего провайдера · {Crowd.NetName(_crowd)}", "net", best.Server, best.Rate,
            $"{best.Total} пров. в вашей сети");
    }
}
