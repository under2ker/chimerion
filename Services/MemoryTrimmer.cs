using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Dnex.Services;

/// <summary>
/// Пока окно скрыто в трей или свернуто, возвращает системе память, которую программа сейчас не использует.
/// WPF держит в рабочем наборе страницы видеодрайвера и отрисовки (в замере на тестовой копии около 230 МБ, из них нужны
/// в покое 40–60). Страницы, которые понадобятся снова, Windows вернет сама при следующем обращении. Данные программы
/// и состояние не затрагиваются.
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>Через сколько после сворачивания в трей сжимать: даем закончиться анимации и ближайшим операциям.</summary>
    internal static readonly TimeSpan AfterHide = TimeSpan.FromSeconds(5);

    /// <summary>Пока окно скрыто, повторять: фоновые проверки понемногу снова набирают память.</summary>
    internal static readonly TimeSpan WhileHidden = TimeSpan.FromMinutes(5);

    /// <summary>Сжимать нужно только тогда, когда окно никто не видит.</summary>
    internal static bool ShouldTrim(bool visible, WindowState state) => !visible || state == WindowState.Minimized;

    private static DispatcherTimer? _timer;
    private static Window? _window;

    /// <summary>Подписка на сворачивание и скрытие окна. Вызывать один раз из UI-потока.</summary>
    public static void Attach(Window window)
    {
        _window = window;
        window.IsVisibleChanged += (_, _) => Check();
        window.StateChanged += (_, _) => Check();
    }

    /// <summary>Запуск вместе с Windows: окна нет вообще, сжать после того, как отработают стартовые проверки.</summary>
    public static void Schedule(TimeSpan delay)
    {
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private static void Check()
    {
        if (_window is { } w && ShouldTrim(w.IsVisible, w.WindowState)) Schedule(AfterHide);
        else _timer?.Stop(); // окно снова на виду: ничего не трогаем
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_window is not { } w || !ShouldTrim(w.IsVisible, w.WindowState)) return;
            TrimNow();
            timer.Interval = WhileHidden;
            timer.Start();
        };
        return timer;
    }

    /// <summary>Сжать рабочий набор процесса. Ошибка не мешает работе: память просто остается как была.</summary>
    internal static bool TrimNow()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return EmptyWorkingSet(self.Handle);
        }
        catch (Exception) { return false; }
    }
}
