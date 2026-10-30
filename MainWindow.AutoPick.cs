using Dnex.Models;
using Dnex.Services;

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
            DnsServer? best = null; int bestScore = int.MinValue, tried = 0;
            foreach (var s in candidates)
            {
                if (_uacDeclined) { Log("Автоподбор остановлен: права администратора не выданы"); return; }
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
            }
            else
            {
                if (DnsSwitcher.GetActive() != null) await DisconnectAsync("автоподбор: ни один сервер не открыл нейросети");
                DashboardMessage("Подобрать режим не удалось: ни один из проверенных серверов не открыл нейросети. Подробности в «Диагностике»", true);
                Log("Автоподбор: ни один сервер не открыл нейросети, DNS возвращены");
            }
        }
        finally { _autoPicking = false; RefreshDashboard(); }
    }
}
