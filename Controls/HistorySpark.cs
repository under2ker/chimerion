using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Dnex.Models;

namespace Dnex.Controls;

/// <summary>
/// История сервера для подсказки: по столбику на проверку (зеленый — работал, красный — нет), высота
/// столбика по пингу (выше — быстрее). Последние проверки справа.
/// </summary>
public sealed class HistorySpark : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<CheckPoint>), typeof(HistorySpark),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<CheckPoint>? Points
    {
        get => (IReadOnlyList<CheckPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private const int Max = 48, Bar = 4, Gap = 1;
    /// <summary>Цвет из палитры программы (App.xaml), без своей копии оттенка.</summary>
    private static Brush Pal(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Brush Ok => Pal("Ok");
    private static Brush Bad => Pal("Bad");
    private static Brush Track => Pal("Border");

    public HistorySpark()
    {
        Width = Max * (Bar + Gap);
        Height = 36;
    }

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Track, null, new Rect(0, Height - 1, Width, 1));
        var pts = Points;
        if (pts == null || pts.Count == 0)
        {
            var ft = new FormattedText("истории пока нет", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, Track, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(0, Height / 2 - 8));
            return;
        }
        var last = pts.Skip(Math.Max(0, pts.Count - Max)).ToList();
        double x = Width - last.Count * (Bar + Gap);
        foreach (var p in last)
        {
            // Высота: неудача — низкий красный столбик; удача — по пингу, от 30% (500 мс и дольше) до 100% (до 30 мс)
            double h = !p.Ok ? 6 : Math.Clamp(1 - Math.Log10(Math.Max(30, p.Ms ?? 500) / 30.0) / Math.Log10(500 / 30.0) * 0.7, 0.3, 1) * (Height - 2);
            dc.DrawRectangle(p.Ok ? Ok : Bad, null, new Rect(x, Height - 1 - h, Bar, h));
            x += Bar + Gap;
        }
    }
}
