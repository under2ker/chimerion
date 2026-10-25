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
        int on = ThemeColors.IsDark ? 1 : 0;
        // 20 — атрибут для Windows 11 и новых сборок 10, 19 — для более старых сборок 10
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        // Цвет заголовка — цвет фона окна текущей темы (COLORREF в порядке BGR)
        var bg = Application.Current?.TryFindResource("Bg") is System.Windows.Media.SolidColorBrush b ? b.Color : System.Windows.Media.Color.FromRgb(0x0F, 0x10, 0x12);
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        // Скругление всех четырех углов окна (DWMWCP_ROUND = 2), иначе верхний левый угол остается острым
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }
}
