using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Dnex.Services;

/// <summary>Одно глобальное сочетание: что оно делает и какие клавиши.</summary>
public sealed record HotkeyDef(int Id, string Title, uint Modifiers, uint Key, string Text);

/// <summary>
/// Сочетания клавиш, которые работают из любой программы, пока CHIMERION запущен (в том числе скрытый в трее).
/// По умолчанию выключены: чужие программы тоже занимают сочетания, поэтому включает их сам человек.
/// Занятое сочетание не ломает программу: оно просто не регистрируется, об этом сообщает MainWindow.
/// </summary>
public static class GlobalHotkeys
{
    public const int ProtectionId = 0x4D01, PaletteId = 0x4D02;
    private const uint Alt = 1, Ctrl = 2, NoRepeat = 0x4000;

    /// <summary>Ctrl+Alt+D: включить или выключить защиту. Ctrl+Alt+K: окно «Поиск и команды».</summary>
    public static readonly IReadOnlyList<HotkeyDef> All = new[]
    {
        new HotkeyDef(ProtectionId, "включить или выключить защиту", Ctrl | Alt, 0x44, "Ctrl+Alt+D"),
        new HotkeyDef(PaletteId, "открыть «Поиск и команды»", Ctrl | Alt, 0x4B, "Ctrl+Alt+K"),
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Принимает нажатия: скрытое окно-приемник без рамки и без места на экране, поэтому оно работает и когда главное окно
    /// спрятано в трей. Живет, пока включены сочетания.
    /// </summary>
    public sealed class Host : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private static readonly IntPtr MessageOnly = new(-3); // HWND_MESSAGE
        private readonly HwndSource _source;
        private readonly List<int> _registered = new();

        public event Action<int>? Pressed;

        public Host()
        {
            var p = new HwndSourceParameters("DnexHotkeys") { ParentWindow = MessageOnly, Width = 0, Height = 0 };
            _source = new HwndSource(p);
            _source.AddHook(Hook);
        }

        /// <summary>Регистрирует все сочетания. Возвращает те, которые не удалось занять (их уже держит другая программа).</summary>
        public List<HotkeyDef> RegisterAll()
        {
            var failed = new List<HotkeyDef>();
            foreach (var d in All)
            {
                if (RegisterHotKey(_source.Handle, d.Id, d.Modifiers | NoRepeat, d.Key)) _registered.Add(d.Id);
                else failed.Add(d);
            }
            return failed;
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY) return IntPtr.Zero;
            handled = true;
            Pressed?.Invoke(wParam.ToInt32());
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _registered) UnregisterHotKey(_source.Handle, id);
            _registered.Clear();
            _source.RemoveHook(Hook);
            _source.Dispose();
        }
    }
}
