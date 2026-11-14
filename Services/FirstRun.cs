using Dnex.Models;

namespace Dnex.Services;

/// <summary>Что человек выбрал в окне первого запуска.</summary>
public sealed record FirstRunChoice(int? ModeId, bool AutoStart, bool CloseToTray, bool Hotkeys);

/// <summary>
/// Первый запуск: когда показывать приветствие, какие варианты в нем и что из выбранного попадает в настройки.
/// Без интерфейса, чтобы проверяться тестами. Само окно: FirstRunWindow, запуск и применение: MainWindow.FirstRun.cs.
/// </summary>
public static class FirstRun
{
    /// <summary>Вариант «Что вам нужно». ModeId: номер режима в списке MainWindow.Modes; null: ничего не включать.</summary>
    public sealed record Option(string Key, string Title, string Sub, string Icon, int? ModeId);

    /// <summary>Номера режимов совпадают с MainWindow.Modes: 0 «Только ИИ», 1 «Все сразу», 2 «Игры» (проверяет тест).</summary>
    public static readonly IReadOnlyList<Option> Options = new[]
    {
        new Option("ai", "Нейросети", "ChatGPT, Claude, Gemini и другие", "shield-check", 0),
        new Option("all", "Нейросети, Telegram, YouTube и Discord", "ИИ, быстрый Telegram и обход замедления", "zap", 1),
        new Option("games", "Игры", "Обход для игр, DNS не меняется", "activity", 2),
        new Option("none", "Пока просто посмотреть", "Ничего не включать, выбрать позже на Главной", "search", null),
    };

    /// <summary>Рекомендуемый вариант: нейросети (режим «Только ИИ»), как в окне «Режим».</summary>
    public static Option Default => Options[0];

    /// <summary>
    /// Показывать ли приветствие. Только при самом первом запуске (файла настроек еще не было) и один раз. В тестовых копиях
    /// данных не показывается, чтобы не мешать проверкам и снимкам окон; принудительно открывается переменной DNEX_FIRST_RUN=1
    /// и командой палитры «Показать приветствие».
    /// </summary>
    public static bool ShouldShow(bool freshInstall, bool alreadyDone, bool testCopy, bool forced) =>
        forced || (freshInstall && !alreadyDone && !testCopy);

    /// <summary>Переносит выбор в настройки (кроме автозапуска: он хранится в Windows, а не в файле настроек).</summary>
    public static void Apply(AppSettings s, FirstRunChoice c)
    {
        s.CloseToTray = c.CloseToTray;
        s.GlobalHotkeys = c.Hotkeys;
        s.FirstRunDone = true;
    }
}
