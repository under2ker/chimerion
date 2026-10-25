using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dnex.Controls;

/// <summary>
/// Плавная прокрутка колесом мыши для ScrollViewer.
///
/// Колесо сдвигает цель, а положение каждый кадр догоняет ее по экспоненте с постоянной времени Tau (по времени
/// кадра, а не по числу кадров). Новое деление колеса только отодвигает цель: скорость меняется без скачков.
/// Прежний вариант на анимациях с плавным торможением при каждом делении начинал движение заново, и при быстрой
/// прокрутке скорость пульсировала, текст будто прыгал.
///
/// Положение ставится только на целые пиксели (на дробном смещении текст пересглаживается и дрожит), а внутри
/// считается точно, без накопления ошибки округления. Списки с прокруткой по строкам остаются со стандартной
/// прокруткой. Вложенная прокрутка: внешний список уступает внутреннему под мышью, пока тот может прокручиваться.
/// Если положение поменяли не мы (ползунок, клавиши, код), плавное движение прекращается.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);

    private const double StepPx = 64;     // путь на одно деление колеса
    private const double TauMs = 75;      // за это время проходится ~63% оставшегося пути

    private sealed class State
    {
        public double Pos, Target, Applied;
    }

    private static readonly Dictionary<ScrollViewer, State> Active = new();
    private static readonly Stopwatch Clock = new();
    private static TimeSpan _last;
    private static bool _hooked;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue) { sv.PreviewMouseWheel += OnWheel; sv.ScrollChanged += OnScrollChanged; }
        else { sv.PreviewMouseWheel -= OnWheel; sv.ScrollChanged -= OnScrollChanged; Active.Remove(sv); }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
        if (!SystemParameters.ClientAreaAnimation) return;                 // анимации в Windows выключены: обычная прокрутка
        if (sv.CanContentScroll && !IsPixelUnit(sv)) return;               // прокрутка строками: стандартная
        if (InnerCanScroll(e.OriginalSource as DependencyObject, sv, e.Delta)) return;

        if (!Active.TryGetValue(sv, out var st))
            st = new State { Pos = sv.VerticalOffset, Target = sv.VerticalOffset, Applied = sv.VerticalOffset };
        bool down = e.Delta < 0;
        // У края колесо уходит наружу (окно с прокруткой вокруг списка)
        if ((down && st.Target >= sv.ScrollableHeight - 0.5) || (!down && st.Target <= 0.5)) return;
        st.Target = Math.Clamp(st.Target - e.Delta / 120.0 * StepPx, 0, sv.ScrollableHeight);
        Active[sv] = st;
        e.Handled = true;
        Start();
    }

    private static void Start()
    {
        if (_hooked) return;
        _hooked = true;
        Clock.Restart();
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed;
        var dt = Math.Clamp((now - _last).TotalMilliseconds, 1, 50); // после зависания не прыгаем на весь путь
        _last = now;
        var k = 1 - Math.Exp(-dt / TauMs);
        foreach (var (sv, st) in Active.ToList())
        {
            st.Target = Math.Clamp(st.Target, 0, sv.ScrollableHeight); // содержимое могло измениться
            st.Pos += (st.Target - st.Pos) * k;
            if (Math.Abs(st.Target - st.Pos) < 0.5) st.Pos = st.Target;
            var px = Math.Round(st.Pos);
            if (px != st.Applied)
            {
                st.Applied = px;
                sv.ScrollToVerticalOffset(px);
            }
            if (st.Pos == st.Target) Active.Remove(sv);
        }
        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }

    /// <summary>Положение поменяли не мы (ползунок, клавиши, переход к строке): плавное движение больше не ведем.</summary>
    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || sender is not ScrollViewer sv || !Active.TryGetValue(sv, out var st)) return;
        if (Math.Abs(sv.VerticalOffset - st.Applied) > 1) Active.Remove(sv);
    }

    private static bool IsPixelUnit(ScrollViewer sv) =>
        sv.TemplatedParent is DependencyObject p && VirtualizingPanel.GetScrollUnit(p) == ScrollUnit.Pixel;

    /// <summary>Есть ли между элементом под мышью и этим ScrollViewer другой, который может прокрутиться в эту сторону.</summary>
    private static bool InnerCanScroll(DependencyObject? from, ScrollViewer outer, int delta)
    {
        for (var d = from; d != null && d != outer; d = Parent(d))
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0)
                return delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight - 0.5 : inner.VerticalOffset > 0.5;
        return false;
    }

    /// <summary>
    /// Родитель для подъема от элемента под мышью. Под мышью бывает и кусок текста (Run, Hyperlink): он не Visual,
    /// VisualTreeHelper на нем бросает исключение, поэтому для текста берем логического родителя.
    /// </summary>
    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d) ?? (d as FrameworkContentElement)?.Parent;
}
