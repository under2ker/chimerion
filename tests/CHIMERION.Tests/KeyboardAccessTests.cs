using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Xunit;

namespace Dnex.Tests;

/// <summary>Действия, которые раньше делались только мышью, доступны с клавиатуры (аудит UI-03).</summary>
public class KeyboardAccessTests
{
    [Fact]
    public void Enter_and_space_on_element_run_action_but_typing_inside_does_not()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("test"));
                int runs = 0;
                var box = new TextBox();
                var card = new StackPanel { Children = { box } }.Keyboard(() => runs++, "Свой DNS");
                void Press(System.Windows.UIElement target, Key key) =>
                    target.RaiseEvent(new KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });

                Assert.True(card.Focusable);
                Assert.Equal("Свой DNS", System.Windows.Automation.AutomationProperties.GetName(card));
                Press(card, Key.Enter);
                Press(card, Key.Space);
                Assert.Equal(2, runs);
                Press(card, Key.A);
                Press(box, Key.Space); // пробел в поле ввода внутри карточки остается полю
                Press(box, Key.Enter);
                Assert.Equal(2, runs);
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
    }
}
