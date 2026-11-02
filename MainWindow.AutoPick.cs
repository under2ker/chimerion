using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>«Подобрать автоматически» на главном экране: программа сама пробует лучшие серверы, пока нейросети не откроются.</summary>
public partial class MainWindow
{
    private bool _autoPicking;

    private async Task AutoPickAsync()
    {
        if (_autoPicking || _busyConn || _dashboardChecking) return;
        if (_dashboardToolBusy) { ShowToast("Сначала дождитесь, пока закончится включение Telegram или обхода", "Warn"); return; }
        _autoPicking = true;
        using var cause = NetHistory.Because("автоподбор режима");
        try
        {
            var names = _settings.Targets.Where(t => t.Enabled).Select(t => t.Name).ToHashSet();
            var candidates = AutoPick.Candidates(_servers.Where(Usable), SafetyFit);
            if (candidates.Count == 0)
            {
                Log("Автоподбор: рабочих проверенных серверов нет");
                ShowToast("Рабочих серверов пока нет. Нажмите «Найти новые серверы» или «Проверить»", "Warn");
                return;
            }
            Log($"Автоподбор: пробую до {candidates.Count} серверов по очереди, страну выхода не меняю");
            var before = DnsSwitcher.GetActive() is { } was ? _servers.FirstOrDefault(x => x.Id == was.ServerId) : null;
            DnsServer? best = null; int bestScore = int.MinValue, tried = 0;
            foreach (var s in candidates)
            {
                if (_uacDeclined) { Log("Автоподбор остановлен: права администратора не выданы. Подключение могло остаться на последнем опробованном сервере"); return; }
                tried++;
                DashboardMessage($"Подбираю режим: пробую «{s.DisplayName}» ({tried} из {candidates.Count})…");
                if (!await ConnectToAsync(s, auto: true)) { Log($"Автоподбор: «{s.DisplayName}» не подключился, иду дальше"); continue; }
                var snap = await ConnectionProbe.TakeAsync(_settings);
                var tally = AutoPick.Tally(snap.Services.Where(x => names.Contains(x.Name)));
                Log($"Автоподбор: «{s.DisplayName}»: нейросети открываются {tally.Open}, закрыты {tally.Closed}, не подтверждены {tally.Unknown}");
                if (AutoPick.IsGood(tally))
                {
                    DashboardMessage(null);
                    var mode = _settings.SplitDns.Enabled ? "с режимом «Два DNS»" : "обычный режим";
                    ShowToast($"Режим подобран: {s.DisplayName}, {mode}. Открываются {tally.Open} нейросетей" +
                              (tally.Unknown > 0 ? $", {tally.Unknown} без подтверждения" : "") + $" (попыток: {tried})");
                    Log($"Автоподбор: найден рабочий режим на попытке {tried}: «{s.DisplayName}»");
                    await OfferToolsAsync();
                    return;
                }
                if (AutoPick.Score(tally) > bestScore) { best = s; bestScore = AutoPick.Score(tally); }
            }
            // Идеального не нашлось: остаемся на лучшем из того, что открылось
            if (best != null && bestScore > 0)
            {
                if (DnsSwitcher.GetActive()?.ServerId != best.Id) await ConnectToAsync(best, auto: true);
                DashboardMessage(null);
                ShowToast($"Полностью рабочего режима не нашлось. Оставил «{best.DisplayName}»: часть нейросетей открывается", "Warn");
                Log($"Автоподбор: полностью рабочего режима нет, выбран «{best.DisplayName}»");
                await OfferToolsAsync();
            }
            else
            {
                // Ничего не подошло: возвращаем то, что было до подбора, а не оставляем человека без Smart DNS
                if (before != null) { if (DnsSwitcher.GetActive()?.ServerId != before.Id) await ConnectToAsync(before, auto: true); }
                else if (DnsSwitcher.GetActive() != null) await DisconnectAsync("автоподбор: ни один сервер не открыл нейросети");
                DashboardMessage("Подобрать режим не удалось: ни один из проверенных серверов не открыл нейросети. Подробности в «Диагностике»", true);
                Log("Автоподбор: ни один сервер не открыл нейросети, DNS возвращены");
            }
        }
        finally { _autoPicking = false; RefreshDashboard(); }
    }

    /// <summary>
    /// Вторая часть подбора: обход YouTube и Discord и ускорение Telegram. Установленные, но выключенные программа не включает
    /// молча (их могли выключить намеренно), а спрашивает один раз за подбор и включает выбранное по очереди.
    /// </summary>
    private async Task OfferToolsAsync()
    {
        var off = _tools.Where(t => t is ZapretTool or TgProxyTool && t.IsInstalled && !t.IsRunning() && !t.State.Enabled).ToList();
        if (off.Count == 0) return;
        var names = string.Join(" и ", off.Select(t => t is ZapretTool ? "обход замедления YouTube и Discord" : "ускорение Telegram"));
        if (!DnexDialog.Ask("Включить остальное?", $"Нейросети подобраны. Сейчас выключено: {names}. Включить, чтобы работало все сразу?",
                "Включить", "Не сейчас", DialogKind.Info, windowTitle: "Автоподбор")) { Log("Автоподбор: обход и ускорение оставлены как были"); return; }
        foreach (var t in off) await DashboardToolAsync(t);
    }
}
