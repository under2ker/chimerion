using System.Windows;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>
/// Палитра команд (Ctrl+K): любое место программы и любое действие за три нажатия. Здесь собирается список строк;
/// показ и выбор делает CommandPaletteWindow, поиск по словам и раскладке (PaletteSearch).
/// Настройки из палитры меняются сразу, без окна настроек и кнопки «Сохранить»; внизу появляется «Отменить» (Ctrl+Z).
/// </summary>
public partial class MainWindow
{
    private void Palette_Click(object sender, RoutedEventArgs e) => OpenPalette();

    /// <summary>Открыть палитру. Из трея или по сочетанию клавиш окно программы не показывается: палитра встает по центру экрана.</summary>
    internal void OpenPalette() =>
        CommandPaletteWindow.Show(IsVisible && WindowState != WindowState.Minimized ? this : null, BuildPaletteCommands());

    /// <summary>Окно программы на экран, если оно было спрятано в трей: страницы и окна открываются поверх него.</summary>
    private void EnsureShown()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) ShowFromTray();
    }

    /// <summary>Действие, которому нужно окно программы: показать его и выполнить.</summary>
    private Func<Task> InWindow(Action action) => () => { EnsureShown(); action(); return Task.CompletedTask; };

    private const string GroupGo = "Перейти", GroupDo = "Действие", GroupSet = "Настройка", GroupSplit = "Два DNS", GroupMode = "Режим";

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var list = new List<PaletteCommand>();
        void Add(string title, string group, string icon, string keywords, Func<Task> run, string? note = null) =>
            list.Add(new PaletteCommand(title, group, icon, keywords, run, note));

        bool on = DnsSwitcher.GetActive() != null;
        var tg = _tools.OfType<TgProxyTool>().First();
        var zapret = _tools.OfType<ZapretTool>().First();

        // ----- Самое частое: защита, программы, режимы -----
        Add(on ? "Выключить защиту" : "Включить защиту", GroupDo, "shield-check", "smart dns подключить отключить vpn переключатель", ToggleProtectionAsync,
            on ? "Сейчас включена" : "Сейчас выключена");
        Add(tg.IsRunning() ? "Выключить ускорение Telegram" : "Включить ускорение Telegram", GroupDo, "send", "телеграм tg proxy прокси", () => ToggleToolFromTrayAsync(tg));
        Add(zapret.IsRunning() ? "Выключить обход YouTube и Discord" : "Включить обход YouTube и Discord", GroupDo, "zap", "ютуб дискорд youtube discord zapret запрет замедление",
            () => ToggleToolFromTrayAsync(zapret));

        int? currentMode = ModeWindow.Current(ModeInfos, ModeState());
        for (int i = 0; i < Modes.Length; i++)
        {
            int id = i;
            var m = Modes[i];
            Add("Режим: " + m.Title, GroupMode, m.Icon, "режим набор", () => DashboardModeAsync(id), currentMode == id ? "Включен сейчас" : m.Hint);
        }

        // ----- Два DNS: путь «Настройки → Два DNS → пресет → галочка» в одну строку -----
        var split = _settings.SplitDns;
        Add(split.Enabled ? "Выключить «Два DNS»" : "Включить «Два DNS»", GroupSplit, "arrow-left-right", "split два днс общий dns nrpt",
            () => SetSplitDnsAsync(!split.Enabled, null), split.Enabled ? $"Сейчас включено, общий DNS «{split.GeneralName}»" : "Сейчас выключено");
        foreach (var p in SplitDns.Presets)
        {
            var preset = p;
            Add($"Два DNS: общий DNS «{p.Name}»", GroupSplit, "arrow-left-right", "split два днс пресет выбор общий dns включить",
                () => SetSplitDnsAsync(true, preset), p.Note + (split.Enabled && split.GeneralName == p.Name ? " · выбрано сейчас" : ""));
        }
        Add("Два DNS: настроить подробно", GroupSplit, "sliders-horizontal", "split два днс свои домены наборы", InWindow(() => OpenSettingsTab("Split")));

        // ----- Перейти -----
        Add("Главная", GroupGo, "shield-check", "домой старт", InWindow(() => DashboardHome_Click(this, new RoutedEventArgs())));
        Add("Серверы", GroupGo, "layout-dashboard", "список dns таблица подключение панель", InWindow(() => ControlPanel_Click(this, new RoutedEventArgs())));
        Add("Добавить сервер", GroupGo, "plus", "новый свой адрес", InWindow(() => AddServer_Click(this, new RoutedEventArgs())));
        Add("Режимы: что меняет каждый", GroupGo, "sliders-horizontal", "окно режимов", InWindow(OpenModeWindow));
        Add("Telegram и YouTube", GroupGo, "zap", "окно программ телеграм ютуб дискорд обход", InWindow(() => ToolsToggle_Click(this, new RoutedEventArgs())));
        Add("Google Flow", GroupGo, "film", "гугл флоу видео", InWindow(() => GoogleFlow_Click(this, new RoutedEventArgs())));
        Add("Диагностика", GroupGo, "stethoscope", "проверки отчет проблема сообщить", InWindow(() => DiagToggle_Click(this, new RoutedEventArgs())));
        Add("Тест скорости", GroupGo, "gauge", "speed скорость интернет", InWindow(() => SpeedTest_Click(this, new RoutedEventArgs())));
        Add("Аккаунты", GroupGo, "shield-user", "риск защита аккаунтов страна", InWindow(() => AccountRisk_Click(this, new RoutedEventArgs())));
        Add("Журнал", GroupGo, "scroll-text", "лог что делала программа поддержка", InWindow(OpenLog));
        Add("Настройки", GroupGo, "settings", "параметры опции", InWindow(() => Settings_Click(this, new RoutedEventArgs())));
        foreach (var (tab, title, words) in SettingsTabs)
        {
            var t = tab;
            Add("Настройки: " + title, GroupGo, "settings", words, InWindow(() => OpenSettingsTab(t)));
        }
        Add("CHIMERION DNS", GroupGo, "star", "личный платный smart dns цены купить", InWindow(() => ChimerionDns_Click(this, new RoutedEventArgs())));
        Add("Помощь", GroupGo, "circle-help", "справка вопрос", InWindow(() => HelpToggle_Click(this, new RoutedEventArgs())));
        Add("Не открывается нейросеть или сайт", GroupGo, "circle-help", "помощь мастер не работает ошибка", InWindow(OpenHelpWizard));
        Add("Сайты под наблюдением", GroupGo, "globe", "watch следить мониторинг", InWindow(OpenSites));
        Add("Smart DNS на телефоне", GroupGo, "smartphone", "телефон андроид айфон phone", InWindow(OpenPhone));
        Add(_compact ? "Обычный вид окна" : "Компактный вид окна", GroupGo, _compact ? "expand" : "shrink", "маленькое окно компактно мини",
            InWindow(() => Mode_Click(this, new RoutedEventArgs())));
        Add("Открыть папку с данными", GroupGo, "folder", "файлы settings json данные проводник", () => { OpenData_Click(this, new RoutedEventArgs()); return Task.CompletedTask; });

        // ----- Действия над серверами и проверками -----
        Add("Проверить подключение и сервисы", GroupDo, "activity", "проверка probe нейросети claude chatgpt gemini", () => { EnsureShown(); return DashboardProbeAsync(); });
        Add("Проверить YouTube, Discord и Telegram", GroupDo, "globe", "сайты доступность", () => { EnsureShown(); return DashboardSitesAsync(); });
        Add("Подобрать сервер автоматически", GroupDo, "check", "автоподбор лучший", () => { EnsureShown(); return AutoPickAsync(); });
        Add("Переключить на лучший сервер", GroupDo, "arrow-left-right", "смена сервера лучший", () => { EnsureShown(); return SwitchToBestAsync(); });
        Add("Найти новые серверы", GroupDo, "download", "поиск источники скачать список", InWindow(() => { ShowControlPanel(); Fetch_Click(this, new RoutedEventArgs()); }));
        Add("Перепроверить все серверы", GroupDo, "refresh-cw", "проверка список обновить", InWindow(() => { ShowControlPanel(); CheckAll_Click(this, new RoutedEventArgs()); }));
        if (_undo != null) Add("Отменить последнее действие", GroupDo, "x", "undo назад вернуть ctrl z", RunUndoAsync);

        // ----- Настройки, которые меняются сразу -----
        bool auto = AutoStart.IsEnabled();
        Add(auto ? "Выключить автозапуск с Windows" : "Включить автозапуск с Windows", GroupSet, "settings", "автозагрузка запуск при входе старт windows", ToggleAutoStartAsync,
            auto ? "Сейчас включен" : "Сейчас выключен");
        AddFlag("сворачивание в трей при закрытии", "log-out", "закрыть крестик трей значок", () => _settings.CloseToTray, v => _settings.CloseToTray = v);
        AddFlag("запуск в компактном виде", "shrink", "маленькое окно старт", () => _settings.StartCompact, v => _settings.StartCompact = v);
        AddFlag("проверка обновлений программы", "circle-arrow-up", "обновления версия update", () => _settings.CheckUpdates, v => _settings.CheckUpdates = v);
        AddFlag("режим скрытия данных", "eye-off", "приватность запись экрана стрим скрыть", () => _settings.PrivacyMode, v => _settings.PrivacyMode = v);
        AddFlag("автосмена сервера, если сервис перестал открываться", "arrow-left-right", "сторож авто переключение сбой",
            () => _settings.AutoSwitchOnServiceDown, v => _settings.AutoSwitchOnServiceDown = v);
        Add(SessionOn ? "Выключить защищенный сеанс" : "Включить защищенный сеанс", GroupSet, "lock", "сеанс не менять сервер аккаунт claude",
            () => { SetSession(!SessionOn); return Task.CompletedTask; }, SessionOn ? "Сейчас включен" : "Сейчас выключен");
        bool light = _settings.Theme == "light";
        Add(light ? "Тема: темная" : "Тема: светлая", GroupSet, "settings", "оформление цвет внешний вид dark light ночная",
            () => ChangeSettingAsync(light ? "Тема: темная" : "Тема: светлая", () => _settings.Theme = light ? "dark" : "light"));

        Add("Выйти из программы", GroupDo, "log-out", "выход закрыть завершить exit", () => { ExitFromTray(); return Task.CompletedTask; });
        return list;

        void AddFlag(string name, string icon, string keywords, Func<bool> get, Action<bool> set)
        {
            bool now = get();
            Add((now ? "Выключить: " : "Включить: ") + name, GroupSet, icon, keywords,
                () => ChangeSettingAsync((now ? "Выключено: " : "Включено: ") + name, () => set(!now)),
                now ? "Сейчас включено" : "Сейчас выключено");
        }
    }

    /// <summary>Разделы окна настроек: тег страницы, название, слова для поиска.</summary>
    private static readonly (string Tab, string Title, string Words)[] SettingsTabs =
    {
        ("General", "общие", "основные язык обновления"),
        ("Privacy", "приватность", "скрытие данных запись экрана"),
        ("Check", "проверки", "серверы источники сервисы нейросети таймаут"),
        ("Watch", "сторож и трей", "watchdog слежение значок интервалы"),
        ("Look", "оформление", "тема цвета внешний вид светлая темная"),
        ("Data", "данные", "резервная копия экспорт импорт очистить"),
        ("About", "о программе", "версия лицензия вернуть прежнюю версию"),
    };

    /// <summary>Настройки сразу на нужной вкладке: вместо «Настройки → вкладка» одна строка палитры.</summary>
    private void OpenSettingsTab(string tab)
    {
        Settings_Click(this, new RoutedEventArgs());
        _settingsOpen?.ShowTab(tab);
    }

    /// <summary>Те же действия, что после закрытия окна настроек: таймеры, цвета, скрытие данных, язык.</summary>
    private void ApplySettingsEffects()
    {
        ApplyTimerSettings();
        ThemeColors.Apply(_settings.Colors, _settings.Theme);
        PrivacyGuard.Set(_settings.PrivacyMode, _settings.PrivacyHideWindows);
        Loc.SetLanguage(_settings.Language);
    }

    /// <summary>
    /// Одна настройка из палитры: снимок файла до правки, правка, сохранение, применение и «Отменить» на 8 секунд.
    /// after: что еще сделать после правки и после отмены (например, переподключить DNS).
    /// </summary>
    private async Task ChangeSettingAsync(string done, Action change, Func<Task>? after = null)
    {
        var before = System.Text.Json.JsonSerializer.Serialize(_settings, Storage.Options);
        change();
        try { Storage.Save(SettingsFile, _settings); }
        catch (Exception ex)
        {
            _settings = LoadSettings();
            ShowToast("Не удалось сохранить настройку: " + ex.Message, "Bad");
            return;
        }
        ApplySettingsEffects();
        Log(done);
        OfferUndo(done, async () =>
        {
            Storage.SaveJson(SettingsFile, before);
            _settings = LoadSettings();
            ApplySettingsEffects();
            if (after != null) await after();
            Log(done + ": отменено");
        });
        if (after != null) await after();
    }

    /// <summary>«Два DNS» из палитры: включить или выключить, при необходимости сразу с выбранным общим DNS.</summary>
    private async Task SetSplitDnsAsync(bool enable, GeneralDnsPreset? preset)
    {
        var s = _settings.SplitDns;
        if (enable && !s.Enabled && SplitDns.PolicyWarning(SplitDns.PolicyRuleCount()) is { } gpo)
        {
            EnsureShown();
            if (!DnexDialog.Ask("«Два DNS» здесь не сработают", gpo, "Все равно включить", windowTitle: "Два DNS")) return;
        }
        var text = !enable ? "Два DNS выключены" : preset == null ? "Два DNS включены" : $"Два DNS включены, общий DNS «{preset.Name}»";
        await ChangeSettingAsync(text, () =>
        {
            s.Enabled = enable;
            if (preset == null) return;
            s.GeneralName = preset.Name;
            s.GeneralAddresses = preset.Addresses.ToList();
            s.GeneralDoh = preset.Doh;
        }, ApplySplitChangeAsync);
    }

    /// <summary>Автозапуск лежит не в settings.json, а в Windows: меняется через AutoStart, с тем же «Отменить».</summary>
    private async Task ToggleAutoStartAsync()
    {
        bool was = AutoStart.IsEnabled();
        if (!await AutoStart.SetAsync(!was))
        {
            ShowToast("Автозапуск изменить не удалось, подробности в журнале", "Warn");
            return;
        }
        var text = was ? "Автозапуск с Windows выключен" : "Автозапуск с Windows включен";
        Log(text);
        OfferUndo(text, async () =>
        {
            if (!await AutoStart.SetAsync(was)) Log("Автозапуск вернуть не удалось");
        });
    }

    /// <summary>Переключатель «Защита» с главной, но без самой главной: так же показывает «Включаю защиту…» и не пускает вторую смену.</summary>
    internal async Task ToggleProtectionAsync()
    {
        if (_busyConn || _dashboardChecking || _dashboardPowerTarget != null)
        {
            ShowToast("Подождите: предыдущее действие еще идет", "Warn");
            return;
        }
        _dashboardPowerTarget = DnsSwitcher.GetActive() == null;
        RefreshDashboard();
        try { await DashboardConnectAsync(); }
        finally { _dashboardPowerTarget = null; RefreshDashboard(); }
    }
}
