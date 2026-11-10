using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace Dnex.Controls;

/// <summary>
/// Плитка-кнопка из рамки: нажимается мышью, Enter и Space, а экранный диктор и UI Automation видят ее как кнопку
/// с действием «Нажать» (обычная Border для них невидима). Click срабатывает один раз на любой способ нажатия.
/// </summary>
public sealed class ClickTile : Border
{
    public event RoutedEventHandler? Click;

    public ClickTile()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, e) => { Raise(); e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Raise(); e.Handled = true; } };
    }

    internal void Raise() => Click?.Invoke(this, new RoutedEventArgs());

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(ClickTile owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(ClickTile);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        public override object? GetPattern(PatternInterface p) => p == PatternInterface.Invoke ? this : base.GetPattern(p);

        public void Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(owner.Raise);
        }
    }
}
