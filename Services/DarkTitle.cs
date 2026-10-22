using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Dnex.Services;

/// <summary>Темная рамка окна Windows 10/11: заголовок и кнопки в цвете программы, а не белым системным.</summary>
public static class DarkTitle
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Применяет тему после создания окна. Вызывать из SourceInitialized.</summary>
    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        // 20 — атрибут для Windows 11 и новых сборок 10, 19 — для более старых сборок 10
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        int caption = 0x001B1614; // COLORREF (BGR) = #14161B
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }
}
