using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Dnex.Controls;

/// <summary>
/// Значок связи для пинга: четыре столбика одинаковой ширины с равным шагом. Все столбики имеют свою высоту,
/// горящие окрашены по значению, погашенные серые той же высоты. До 50 мс горят все четыре зеленые,
/// до 100 — три, до 200 — два желтые, дольше — один красный. Без данных все серые.
/// Геометрия в целых пикселях, чтобы промежутки не отличались на экране.
/// </summary>
public sealed class PingBars : Canvas
{
    public static readonly DependencyProperty LatencyProperty = DependencyProperty.Register(
        nameof(Latency), typeof(long?), typeof(PingBars), new FrameworkPropertyMetadata(null, OnLatencyChanged));

    /// <summary>Пинг в миллисекундах; null — данных нет.</summary>
    public long? Latency
    {
        get => (long?)GetValue(LatencyProperty);
        set => SetValue(LatencyProperty, value);
    }

    private const int BarWidth = 3, Gap = 2, IconHeight = 14;
    private static readonly int[] Heights = { 5, 8, 11, 14 };
    /// <summary>Цвет из палитры программы (App.xaml), без своей копии оттенка.</summary>
    private static Brush Pal(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Brush Ok => Pal("Ok");
    private static Brush Warn => Pal("Warn");
    private static Brush Bad => Pal("Bad");
    private static Brush Off => Pal("ScrollThumb");

    private readonly Rectangle[] _bars = new Rectangle[4];

    public PingBars()
    {
        Width = 4 * BarWidth + 3 * Gap;
        Height = IconHeight;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = BarWidth, Height = Heights[i], RadiusX = 1, RadiusY = 1, Fill = Off,
                SnapsToDevicePixels = true,
            };
            SetLeft(_bars[i], i * (BarWidth + Gap));
            SetBottom(_bars[i], 0);
            Children.Add(_bars[i]);
        }
        Loaded += (_, _) => Show(animate: false);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static void OnLatencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PingBars)d).Show(animate: true);

    /// <summary>Сколько столбиков горит и цвет по пингу.</summary>
    public static (int Lit, Brush Color) Level(long? ms) => ms switch
    {
        null => (0, Off),
        <= 50 => (4, Ok),
        <= 100 => (3, Ok),
        <= 200 => (2, Warn),
        _ => (1, Bad),
    };

    private void Show(bool animate)
    {
        var (lit, color) = Level(Latency);
        var motion = animate && SystemParameters.ClientAreaAnimation;
        ToolTip = Latency is { } ms ? $"Пинг {ms} мс" : "Пинг: нет данных";
        for (var i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            bool on = i < lit;
            bar.Fill = on ? color : Off;
            bar.BeginAnimation(HeightProperty, null);
            if (motion && on)
            {
                // Горящие столбики вырастают по очереди: от нуля до своей высоты
                bar.Height = 0;
                bar.BeginAnimation(HeightProperty, new DoubleAnimation(0, Heights[i], TimeSpan.FromMilliseconds(300))
                {
                    BeginTime = TimeSpan.FromMilliseconds(60 * i),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.HoldEnd,
                });
            }
            else
            {
                bar.Height = Heights[i];
            }
        }
    }
}
