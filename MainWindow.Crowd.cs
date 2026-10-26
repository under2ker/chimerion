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
            "Программа будет раз в час отправлять итоги своих проверок: адрес Smart DNS, его название и открываются ли через него ChatGPT, Claude и Gemini.\n\n" +
            "О вас ничего не уходит: ни IP, ни имени, ни списка сайтов. Сервер видит только сеть провайдера, чтобы считать рейтинг для нее.\n\n" +
            "Взамен программа узнает, какие серверы работают у пользователей вашего провайдера прямо сейчас, и подскажет лучший. Изменить можно в настройках, вкладка «Приватность».",
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
            }
        }
        catch (Exception) { }
    }

    /// <summary>Строка «У вашего провайдера» для блока лучших: сервер, доля удачных проверок в той же сети и их число.</summary>
    private ServerRanking.TopPick? ProviderPick()
    {
        if (_crowd == null || Crowd.BestForProvider(_crowd, _servers) is not { } best) return null;
        return new ServerRanking.TopPick($"У вашего провайдера · {Crowd.NetName(_crowd)}", "net", best.Server, best.Rate,
            $"{best.Total} пров. в вашей сети");
    }
}
