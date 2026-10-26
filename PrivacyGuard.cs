using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Dnex;

/// <summary>
/// Режим скрытия данных. Несколько слоев, чтобы личные данные (ваш внешний IP, город, регион, провайдер) не попали
/// ни на скриншот, ни на запись экрана, ни в трансляцию, ни на фото экрана:
///
/// 1. Захват экрана: каждое окно процесса (главное, диалоги, всплывающие подсказки, меню, меню трея) помечается
///    WDA_EXCLUDEFROMCAPTURE. Windows не отдает его ни одной программе захвата: Print Screen, «Ножницы», Xbox Game Bar,
///    OBS, демонстрация экрана в Discord, Zoom, Teams, браузере. На записи вместо окна видно то, что под ним. На Windows
///    старше 10 2004 такой пометки нет, и окно на записи становится черным (WDA_MONITOR).
/// 2. Миниатюры: на панели задач, в Alt+Tab и при Peek вместо содержимого окна показывается значок.
/// 3. Маскировка: известные личные значения заменяются на «скрыто» в журнале, на панели проверки, в диагностике и трее,
///    на случай фото экрана телефоном или взгляда через плечо.
/// 4. Уведомления Windows рисует система, их из захвата не исключить: в их тексте личных данных нет (Mask).
/// 5. Копирование журнала и отчетов очищает текст от личного.
///
/// Метка ставится в момент появления окна (Loaded, открытие подсказки или меню) и дополнительно обходом видимых окон
/// процесса четыре раза в секунду: так не пропускается ни одно всплывающее окно. Скрытые служебные окна (WPF и WinForms
/// держат их в левом верхнем углу экрана) не трогаются: смена метки у скрытого окна заставляла DWM нарисовать на его
/// месте белый прямоугольник. Снимается метка только с тех окон, которые помечали сами.
/// </summary>
public static class PrivacyGuard
{
    /// <summary>Режим включен: личные данные маскируются.</summary>
    public static bool Enabled { get; private set; }
    /// <summary>Окна еще и исключены из захвата экрана (стример мод). Без него режим только маскирует данные.</summary>
    public static bool HidesWindows { get; private set; }
    public static event Action? Changed;

    public const string Hidden = "скрыто";

    private const uint WDA_NONE = 0, WDA_MONITOR = 1, WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7, DWMWA_DISALLOW_PEEK = 11;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumProc f, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private delegate bool EnumProc(IntPtr h, IntPtr p);

    private static readonly DispatcherTimer Scan = new(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(250) };
    private static readonly HashSet<IntPtr> Marked = new();
    private static bool _installed;

    /// <summary>Подписка на появление окон, подсказок и меню. Один раз при запуске программы.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (HidesWindows) MarkOf((Visual)s); }));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => { if (HidesWindows) MarkOf((Visual)s); }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => { if (HidesWindows) MarkOf((Visual)s); }));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((_, _) => { if (HidesWindows) MarkAll(); }));
        Scan.Tick += (_, _) => MarkAll();
    }

    /// <param name="on">Маскировать личные данные.</param>
    /// <param name="hideWindows">Еще и исключить окна из захвата экрана (стример мод).</param>
    public static void Set(bool on, bool hideWindows = true)
    {
        bool hide = on && hideWindows;
        if (on == Enabled && hide == HidesWindows) return;
        Enabled = on;
        if (hide != HidesWindows)
        {
            HidesWindows = hide;
            if (hide) Scan.Start(); else Scan.Stop();
            MarkAll(clear: !hide);
        }
        Changed?.Invoke();
    }

    /// <summary>Окно WinForms (меню трея): пометить сразу при открытии.</summary>
    public static void MarkHandle(IntPtr h) { if (HidesWindows && IsWindowVisible(h)) Apply(h, true); }

    private static void MarkOf(Visual v)
    {
        if (PresentationSource.FromVisual(v) is HwndSource src && IsWindowVisible(src.Handle)) Apply(src.Handle, true);
        MarkAll();
    }

    /// <summary>Видимые окна процесса (главное, диалоги, открытые подсказки и меню) пометить, или снять пометку с помеченных.</summary>
    private static void MarkAll(bool clear = false)
    {
        if (clear)
        {
            foreach (var h in Marked.ToList()) if (IsWindow(h)) Apply(h, false);
            Marked.Clear();
            return;
        }
        uint me = (uint)Environment.ProcessId;
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var h in list) Apply(h, true);
        Marked.RemoveWhere(h => !IsWindow(h));
    }

    /// <summary>Окно с кнопкой на панели задач (главное, диалоги): только им нужна замена миниатюры значком.</summary>
    private static bool HasTaskbarButton(IntPtr h)
    {
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        return (ex & WS_EX_APPWINDOW) != 0 || GetWindow(h, GW_OWNER) == IntPtr.Zero;
    }

    private static void Apply(IntPtr h, bool on)
    {
        if (h == IntPtr.Zero) return;
        if (on && Marked.Contains(h)) return;
        if (on)
        {
            if (!SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(h, WDA_MONITOR);
            Marked.Add(h);
        }
        else SetWindowDisplayAffinity(h, WDA_NONE);
        if (!HasTaskbarButton(h)) return;
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(h, DWMWA_FORCE_ICONIC_REPRESENTATION, ref v, sizeof(int));
        DwmSetWindowAttribute(h, DWMWA_DISALLOW_PEEK, ref v, sizeof(int));
    }

    /// <summary>Исключено ли окно из захвата сейчас (для проверки и тестов).</summary>
    public static bool IsExcluded(IntPtr h) => GetWindowDisplayAffinity(h, out var a) && a is WDA_EXCLUDEFROMCAPTURE or WDA_MONITOR;

    // ---------- Маскировка личных значений ----------

    private static readonly ConcurrentDictionary<string, byte> Personal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Запомнить ваши личные значения из проверки подключения: внешний IP, место («RU, Область, Город» — область и
    /// город, страна остается) и провайдера. Маскируются только они, адреса DNS-серверов и прокси нужны для работы.
    /// </summary>
    public static void RememberPersonal(string? publicIp, string? location, string? provider)
    {
        void Add(string? v) { if (!string.IsNullOrWhiteSpace(v) && v.Trim().Length >= 3 && v != "—") Personal.TryAdd(v.Trim(), 0); }
        Add(publicIp);
        Add(provider);
        if (!string.IsNullOrWhiteSpace(location))
        {
            var parts = location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts.Skip(1)) Add(p);         // область и город
            if (parts.Length > 1) Add(string.Join(", ", parts.Skip(1)));
        }
        if (!string.IsNullOrWhiteSpace(provider))
        {
            // «AS28890 INSYS ISP»: и номер сети, и название по отдельности
            var m = System.Text.RegularExpressions.Regex.Match(provider, @"^(AS\d+)\s+(.+)$");
            if (m.Success) { Add(m.Groups[1].Value); Add(m.Groups[2].Value); }
        }
    }

    /// <summary>Текст для экрана, уведомлений и копирования: при включенном режиме личные значения заменены на «скрыто».</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Enabled || Personal.IsEmpty) return text ?? "";
        // Длинные значения первыми: «Sverdlovsk Oblast, Pervouralsk» раньше, чем «Pervouralsk»
        foreach (var v in Personal.Keys.OrderByDescending(k => k.Length))
            text = ReplaceWord(text, v);
        return text;
    }

    /// <summary>Замена целого значения (не части другого слова или адреса): «1.2.3.4» не трогает «11.2.3.45».</summary>
    private static string ReplaceWord(string text, string value) =>
        System.Text.RegularExpressions.Regex.Replace(text,
            $@"(?<![\w.:]){System.Text.RegularExpressions.Regex.Escape(value)}(?![\w.:])", Hidden, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Для копирования наружу: маска плюс очистка, как у отчета для GitHub (локальные адреса, имя пользователя, почта).</summary>
    public static string ForExport(string text) => Enabled ? Services.Diagnostics.Sanitize(Mask(text), null) : text;

    /// <summary>Сбросить запомненные значения (тесты).</summary>
    internal static void ForgetPersonal() => Personal.Clear();
}
