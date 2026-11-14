using Dnex.Services;

namespace Dnex;

/// <summary>
/// Первый запуск (Services/FirstRun.cs, FirstRunWindow.cs): приветствие с вопросом «что вам нужно» и тремя галочками.
/// Выбор сразу применяется; вопрос об общей карте серверов задается после него, а не поверх. ОТКАТ: весь блок снимается
/// откатом коммита с пометкой «ОТКАТ: окно первого запуска» (git revert).
/// </summary>
public partial class MainWindow
{
    /// <summary>Файла настроек еще не было: это самый первый запуск. Определяется до загрузки настроек, потом файл уже есть.</summary>
    private bool _freshInstall;
    private bool _firstRunOpen;

    private async Task MaybeShowFirstRunAsync(bool forced = false)
    {
        forced |= Environment.GetEnvironmentVariable("DNEX_FIRST_RUN") == "1";
        if (_firstRunOpen || !FirstRun.ShouldShow(_freshInstall, _settings.FirstRunDone, Storage.TestCopy, forced)) return;
        _firstRunOpen = true;
        try
        {
            await Task.Delay(forced ? 0 : 1500); // окно успевает нарисоваться, приветствие не выскакивает до него
            if (!IsVisible) return; // запуск без окна: спрашивать некому, приветствие дождется следующего показа
            Log("Первый запуск: показываю приветствие");
            var choice = FirstRunWindow.Ask(AutoStart.IsEnabled() || !forced, _settings.CloseToTray, _settings.GlobalHotkeys);
            _settings.FirstRunDone = true;
            if (choice == null)
            {
                Log("Первый запуск: пропущен");
                try { Storage.Save(SettingsFile, _settings); } catch (Exception) { }
                _firstRunOpen = false;
                AskCrowdConsent();
                return;
            }
            FirstRun.Apply(_settings, choice);
            try { Storage.Save(SettingsFile, _settings); } catch (Exception ex) { Log("Первый запуск: настройки не сохранились: " + ex.Message); }
            ApplyGlobalHotkeys();
            if (choice.AutoStart != AutoStart.IsEnabled() && !await AutoStart.SetAsync(choice.AutoStart))
                Log("Первый запуск: автозапуск изменить не удалось");
            Log($"Первый запуск: выбор «{FirstRun.Options.FirstOrDefault(o => o.ModeId == choice.ModeId)?.Title ?? "посмотреть"}», " +
                $"автозапуск={choice.AutoStart}, трей={choice.CloseToTray}, сочетания={choice.Hotkeys}");
            _firstRunOpen = false;
            AskCrowdConsent(); // свой вопрос после приветствия, а не поверх него
            if (choice.ModeId is { } id) await StartModeWhenReadyAsync(id);
        }
        finally { _firstRunOpen = false; }
    }

    /// <summary>Режим из приветствия. Серверы при первом запуске еще не проверены: ждем первых рабочих (до полутора минут).</summary>
    private async Task StartModeWhenReadyAsync(int id)
    {
        if (id < 0 || id >= Modes.Length) return;
        bool needServers = Modes[id].Mode != Mode.Games; // «Игры» работают без Smart DNS
        if (needServers && !ModeState().HasWorkingServers)
        {
            ShowToast("Проверяю серверы, потом включу «" + Modes[id].Title + "»");
            for (int i = 0; i < 60 && !ModeState().HasWorkingServers; i++) await Task.Delay(1500);
            if (!ModeState().HasWorkingServers)
            {
                ShowToast("Рабочих серверов пока нет. Режим можно включить на Главной, когда проверка закончится", "Warn");
                return;
            }
        }
        ShowFromTrayIfHidden();
        await DashboardModeAsync(id);
    }

    private void ShowFromTrayIfHidden()
    {
        if (!IsVisible) ShowFromTray();
    }
}
