using System.Windows.Threading;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>Общая карта серверов: отчеты о проверках с согласия пользователя и рейтинг для его провайдера (Crowd)</summary>
public partial class MainWindow
{
    private readonly Crowd.Queue _crowdQueue = Crowd.LoadQueue();
    private Crowd.Stats? _crowd = Crowd.LoadStats();
    private readonly DispatcherTimer _crowdTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    /// <summary>При запуске: таймер обмена с общей картой и, один раз, вопрос о согласии.</summary>
    private void StartCrowd()
    {
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
        if (_settings.CrowdConsent != null || !IsVisible) return;
        bool yes = DnexDialog.Ask("Помогать общей карте серверов?",
            "Раз в час программа будет отправлять итоги проверок: адрес сервера и открываются ли через него нейросети. Ваш IP и сайты не уходят.\n\n" +
            "Взамен она подскажет, какой сервер лучше работает у вашего провайдера. Изменить можно в настройках, во вкладке «Приватность».",
            "Помогать", "Не сейчас", DialogKind.Info, windowTitle: "Общая карта серверов");
        _settings.CrowdConsent = yes;
        try { Storage.Save("settings.json", _settings); } catch (Exception) { }
        Log(yes ? "Общая карта серверов: отчеты о проверках включены" : "Общая карта серверов: отчеты не отправляются");
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
            if (_crowd == null || DateTime.Now - _crowd.Fetched > Crowd.FetchEvery)
            {
                _crowd = await Crowd.FetchAsync();
                _topSig = null;
                UpdateTopList();
                await CrowdDiscoverAsync();
            }
        }
        catch (Exception) { }
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
