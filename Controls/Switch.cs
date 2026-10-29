using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Dnex.Controls;

/// <summary>
/// «Мягкий» переключатель по мотивам SquishSwitch из React Bits (MIT): бегунок доезжает пружиной, на скорости
/// вытягивается и сужается (площадь сохраняется), при наведении чуть увеличивается, его можно перетащить.
/// Цвета дорожки и бегунка плавно меняются за 320 мс. Это ToggleButton: щелчок, пробел и экранный диктор работают,
/// Click срабатывает один раз на отпускание (при перетаскивании только если сторона сменилась).
/// Width задает размер, высота вдвое меньше. Без анимаций Windows все движения мгновенные.
/// </summary>
public sealed class Switch : ToggleButton
{
    // Настройки как у оригинала по умолчанию: speed 50, stretch 36, hoverScale 1.035, colorDuration 320
    private const double Stiffness = 170, Damping = 21.5, Mass = 0.9;
    private const double FlowK = 320, FlowC = 40, FlowM = 0.6, SwellK = 520, SwellC = 34, SwellM = 0.6;
    private const double MaxStretch = 0.4, StretchSpeed = 600, StretchGain = 0.36, HoverScale = 1.035, Slop = 4;

    private readonly Grid _track = new();
    private readonly Border _trackOn = new(), _thumbOff = new(), _thumbOn = new();
    private readonly Grid _thumb = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _scale = new();

    private double _min, _max, _x, _v, _flow, _flowV, _swell = 1, _swellV, _swellTo = 1;
    private bool _running, _dragging, _moved, _pressed;
    private double _grab, _pressX;
    private TimeSpan _last;

    public Switch()
    {
        Width = 52;
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = null;
        var trackOff = new Border();
        trackOff.SetResourceReference(Border.BackgroundProperty, "ControlBorder");
        _trackOn.SetResourceReference(Border.BackgroundProperty, "ToggleOn");
        _thumbOff.SetResourceReference(Border.BackgroundProperty, "Text");
        _thumbOn.SetResourceReference(Border.BackgroundProperty, "OnToggle");
        _thumb.Children.Add(_thumbOff); _thumb.Children.Add(_thumbOn);
        _thumb.RenderTransformOrigin = new Point(.5, .5);
        _thumb.RenderTransform = new TransformGroup { Children = { _scale, _move } };
        _track.Children.Add(trackOff); _track.Children.Add(_trackOn); _track.Children.Add(_thumb);
        var focus = new Border { BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed, Margin = new Thickness(-3) };
        focus.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_track); root.Children.Add(focus);
        Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Content = root;
        SizeChanged += (_, _) => Geometry(focus);
        Loaded += (_, _) => { Geometry(focus); Snap(); };
        Unloaded += (_, _) => Stop();
        Checked += (_, _) => Changed();
        Unchecked += (_, _) => Changed();
        IsKeyboardFocusedChanged += (_, _) => focus.Visibility = IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
        IsEnabledChanged += (_, _) => { Opacity = IsEnabled ? 1 : 0.5; Cursor = IsEnabled ? Cursors.Hand : Cursors.No; };
        MouseEnter += (_, _) => { _swellTo = IsEnabled ? HoverScale : 1; Start(); };
        MouseLeave += (_, _) => { _swellTo = 1; Start(); };
    }

    private static bool Animate => SystemParameters.ClientAreaAnimation;
    private double Target => IsChecked == true ? _max : _min;

    private void Geometry(Border focus)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width, h = Math.Round(w / 2);
        Height = h;
        double inset = Math.Max(3, Math.Round(h * 0.11)), d = h - inset * 2, r = h / 2, tr = Math.Max(2, r - inset);
        foreach (var b in new[] { (Border)_track.Children[0], _trackOn }) b.CornerRadius = new CornerRadius(r);
        focus.CornerRadius = new CornerRadius(r + 3);
        _thumb.Width = _thumb.Height = d;
        _thumb.Margin = new Thickness(0, inset, 0, 0);
        _thumbOff.CornerRadius = _thumbOn.CornerRadius = new CornerRadius(tr);
        _min = inset; _max = w - inset - d;
        if (!_running && !_dragging) Snap();
    }

    private void Snap()
    {
        _x = Target; _v = 0; _move.X = _x;
        double on = IsChecked == true ? 1 : 0;
        _trackOn.BeginAnimation(OpacityProperty, null); _thumbOn.BeginAnimation(OpacityProperty, null);
        _trackOn.Opacity = _thumbOn.Opacity = on;
    }

    private void Changed()
    {
        // Цвета перетекают за 320 мс, бегунок доезжает пружиной (или сразу, если анимации Windows выключены)
        double on = IsChecked == true ? 1 : 0;
        var fade = new DoubleAnimation(on, Animate ? TimeSpan.FromMilliseconds(320) : TimeSpan.Zero);
        _trackOn.BeginAnimation(OpacityProperty, fade); _thumbOn.BeginAnimation(OpacityProperty, fade);
        if (_dragging) return;
        if (!Animate || !IsLoaded) { Snap(); return; }
        Start();
    }

    private void Start()
    {
        if (_running || !IsLoaded) return;
        _running = true; _last = TimeSpan.Zero;
        CompositionTarget.Rendering += Tick;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _last).TotalSeconds, 0, 1 / 30.0);
        _last = now;
        if (dt <= 0) return;
        double prev = _x;
        if (!_dragging)
        {
            double a = (-Stiffness * (_x - Target) - Damping * _v) / Mass;
            _v += a * dt; _x += _v * dt;
        }
        double vel = (_x - prev) / dt;
        // Скорость сглаживается своей пружиной: растяжение нарастает и спадает мягко, без рывков
        _flowV += (-FlowK * (_flow - vel) - FlowC * _flowV) / FlowM * dt; _flow += _flowV * dt;
        double target = Animate ? _swellTo : 1;
        _swellV += (-SwellK * (_swell - target) - SwellC * _swellV) / SwellM * dt; _swell += _swellV * dt;
        double s = 1 + Math.Min(MaxStretch, Math.Abs(_flow) / StretchSpeed) * (Animate ? StretchGain : 0);
        _move.X = _x;
        _scale.ScaleX = s * _swell; _scale.ScaleY = _swell / s;
        bool rest = !_dragging && Math.Abs(_x - Target) < 0.01 && Math.Abs(_v) < 0.05 && Math.Abs(_flow) < 0.5 && Math.Abs(_flowV) < 0.5
                    && Math.Abs(_swell - target) < 0.0005 && Math.Abs(_swellV) < 0.001;
        if (rest) { _x = Target; _move.X = _x; _flow = _flowV = 0; _swell = target; _scale.ScaleX = _scale.ScaleY = target; Stop(); }
    }

    // Мышь обрабатываем сами: щелчок переключает, перетаскивание ведет бегунок за курсором
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsEnabled) return;
        Focus();
        CaptureMouse();
        _pressed = true; _moved = false; _dragging = true;
        _pressX = e.GetPosition(this).X;
        _grab = _pressX - _x;
        Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        double lx = e.GetPosition(this).X;
        if (!_moved && Math.Abs(lx - _pressX) > Slop) _moved = true;
        if (!_moved) return;
        _x = Math.Clamp(lx - _grab, _min, _max); _v = 0;
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_pressed) return;
        Release();
        bool inside = new Rect(RenderSize).Contains(e.GetPosition(this));
        bool flip = _moved ? (_x > (_min + _max) / 2) != (IsChecked == true) : inside;
        if (flip) OnClick(); // переключает IsChecked и поднимает Click один раз
        Start();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_pressed) { Release(); Start(); } // отмена: бегунок возвращается на свою сторону
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _pressed) { e.Handled = true; ReleaseMouseCapture(); return; }
        base.OnKeyDown(e);
    }

    private void Release()
    {
        _pressed = false; _dragging = false;
        Cursor = IsEnabled ? Cursors.Hand : Cursors.No;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
