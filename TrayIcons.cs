using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitmap = System.Drawing.Bitmap;
using DColor = System.Drawing.Color;

namespace Dnex;

/// <summary>
/// Значки для меню трея (WinForms): те же контуры Lucide, что в окнах программы (Controls/Icon.cs), нарисованные
/// средствами WPF в картинку 16×16. Цвет задается при отрисовке, поэтому смена темы подхватывается сама: картинки кэшируются
/// по паре «значок, цвет».
/// </summary>
internal static class TrayIcons
{
    public const int Size = 16;
    private static readonly Dictionary<(string, int), Bitmap> Cache = new();

    public static Bitmap? Get(string kind, DColor color)
    {
        if (!Controls.Icon.Outline.TryGetValue(kind, out var data)) return null;
        var key = (kind, color.ToArgb());
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        // Линия 2 в поле 24×24, как у значков в окнах: после масштаба 16/24 она около 1,3 px
        var pen = new System.Windows.Media.Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Size / 24.0, Size / 24.0));
            dc.DrawGeometry(null, pen, Controls.Icon.ParseData(data));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var bmp = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var bits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, Size, Size), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[Size * bits.Stride];
            rtb.CopyPixels(new Int32Rect(0, 0, Size, Size), pixels, bits.Stride, 0);
            Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
        }
        finally { bmp.UnlockBits(bits); }
        Cache[key] = bmp;
        return bmp;
    }
}
