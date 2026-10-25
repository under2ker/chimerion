using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Dnex.Controls;

/// <summary>
/// Знак CHIMERION: три наклонные полосы. Геометрия та же, что у иконки программы (docs/brand, scratchpad brand.py):
/// поле 100×100, наклон 0.85, ширина полосы 15. Как в фирменном знаке: первая короткая (верх вровень со второй),
/// вторая длинная (уходит ниже всех), третья короткая и начинается ниже. Первые две полосы цвета LogoStripe
/// (акцент из «Цветов интерфейса» или белый в стандартной схеме), третья LogoTail (оливковая, не меняется).
/// </summary>
public static class Brand
{
    private const double Slope = 0.85, W = 15;

    private static Point[] Stripe(double x, double yBottom, double yTop, double w = W)
    {
        var dx = Slope * (yBottom - yTop);
        return new[] { new Point(x, yBottom), new Point(x + w, yBottom), new Point(x + w + dx, yTop), new Point(x + dx, yTop) };
    }

    /// <summary>Полосы обычного размера и упрощенные для 16–24 пикселей (толще, чтобы не слиться в пятно).</summary>
    public static readonly Point[][] Stripes = { Stripe(3, 71, 10), Stripe(8.85, 90, 10), Stripe(35.1, 85, 30) };
    public static readonly Point[][] StripesSmall = { Stripe(0, 71, 10, 18), Stripe(7.85, 90, 10, 18), Stripe(36.1, 85, 30, 18) };

    public static Geometry Polygon(Point[] p, double k = 1, double ox = 0, double oy = 0)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(ox + p[0].X * k, oy + p[0].Y * k), true, true);
            c.PolyLineTo(p.Skip(1).Select(q => new Point(ox + q.X * k, oy + q.Y * k)).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    private static Color Res(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    public static Color StripeColor => Res("LogoStripe", Color.FromRgb(0xF5, 0xF5, 0xF0));
    public static Color TailColor => Res("LogoTail", Color.FromRgb(0xB5, 0xB2, 0x9C));

    /// <summary>Иконка: знак на темной скругленной плитке, как значок программы, в текущих цветах.</summary>
    public static BitmapSource RenderIcon(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = size * 0.22;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1A)), new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x2F, 0x34)), Math.Max(1, size / 64.0)),
                new Rect(0.5, 0.5, size - 1, size - 1), r, r);
            var small = size <= 24;
            var k = size / 100.0 * (small ? 0.82 : 0.74);
            var off = size * (small ? 0.09 : 0.13);
            var geom = small ? StripesSmall : Stripes;
            var c12 = new SolidColorBrush(StripeColor);
            var c3 = new SolidColorBrush(TailColor);
            for (int i = 0; i < 3; i++) dc.DrawGeometry(i < 2 ? c12 : c3, null, Polygon(geom[i], k, off, off));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Значок окон в текущих цветах (сразу несколько размеров: Windows берет подходящий).</summary>
    public static ImageSource WindowIcon
    {
        get
        {
            var enc = new IconBitmapEncoderLite();
            return enc.Make(new[] { 16, 24, 32, 48, 64, 256 }.Select(RenderIcon));
        }
    }

    /// <summary>Значок для трея (WinForms) в текущих цветах.</summary>
    public static System.Drawing.Icon TrayIcon()
    {
        var src = RenderIcon(32);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var bmp = new System.Drawing.Bitmap(ms);
        var h = bmp.GetHicon();
        var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    /// <summary>Обновить значки всех открытых окон (после смены цветов).</summary>
    public static void RefreshWindowIcons()
    {
        if (Application.Current == null) return;
        var icon = WindowIcon;
        foreach (Window w in Application.Current.Windows) w.Icon = icon;
    }
}

/// <summary>Несколько размеров значка в одном .ico в памяти: окно получает четкий значок и в заголовке, и в Alt+Tab.</summary>
internal sealed class IconBitmapEncoderLite
{
    public ImageSource Make(IEnumerable<BitmapSource> frames)
    {
        var pngs = frames.Select(f =>
        {
            var e = new PngBitmapEncoder();
            e.Frames.Add(BitmapFrame.Create(f));
            using var m = new MemoryStream();
            e.Save(m);
            return (f.PixelWidth, Data: m.ToArray());
        }).ToList();
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)pngs.Count);
            var offset = 6 + 16 * pngs.Count;
            foreach (var (size, data) in pngs)
            {
                bw.Write((byte)(size >= 256 ? 0 : size)); bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                bw.Write(data.Length); bw.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in pngs) bw.Write(data);
        }
        ms.Position = 0;
        var dec = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }
}

/// <summary>Знак CHIMERION в интерфейсе (вектор). Цвета берутся из ресурсов и меняются вместе с настройками цветов.</summary>
public sealed class BrandMark : Viewbox
{
    public BrandMark()
    {
        Width = Height = 18;
        Stretch = Stretch.Uniform;
        var canvas = new Canvas { Width = 100, Height = 100 };
        for (int i = 0; i < 3; i++)
        {
            var p = new System.Windows.Shapes.Path { Data = Brand.Polygon(Brand.Stripes[i]) };
            p.SetResourceReference(Shape.FillProperty, i < 2 ? "LogoStripe" : "LogoTail");
            canvas.Children.Add(p);
        }
        Child = canvas;
        SnapsToDevicePixels = true;
    }
}

/// <summary>Надпись CHIMERION: широкие заглавные с разрядкой (в WPF нет межбуквенного интервала, буквы ставятся по одной).</summary>
public sealed class BrandWordmark : FrameworkElement
{
    public static readonly DependencyProperty FontSizeProperty = System.Windows.Documents.TextElement.FontSizeProperty.AddOwner(typeof(BrandWordmark),
        new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(BrandWordmark),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Разрядка в долях кегля.</summary>
    public double Tracking { get; set; } = 0.28;
    public string Text { get; set; } = "CHIMERION";

    private static readonly Typeface Face = new(new FontFamily("Bahnschrift SemiBold, Bahnschrift, Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private IEnumerable<FormattedText> Glyphs()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return Text.Select(ch => new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSize, Foreground, dpi));
    }

    protected override Size MeasureOverride(Size available)
    {
        var g = Glyphs().ToList();
        var w = g.Sum(x => x.WidthIncludingTrailingWhitespace) + FontSize * Tracking * (g.Count - 1);
        return new Size(w, g.Count == 0 ? 0 : g.Max(x => x.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double x = 0;
        foreach (var f in Glyphs())
        {
            dc.DrawText(f, new Point(Math.Round(x), 0));
            x += f.WidthIncludingTrailingWhitespace + FontSize * Tracking;
        }
    }
}
