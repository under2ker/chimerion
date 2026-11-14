using System.Windows;

namespace Dnex.Controls;

/// <summary>
/// Состояние «ошибка» у поля ввода: общий стиль TextBox в App.xaml рисует рамку цветом Bad, даже когда поле в фокусе.
/// Так ошибка выглядит одинаково во всех окнах: красная рамка у поля и текст ошибки под ним.
/// </summary>
public static class FieldError
{
    public static readonly DependencyProperty IsErrorProperty = DependencyProperty.RegisterAttached(
        "IsError", typeof(bool), typeof(FieldError), new PropertyMetadata(false));

    public static bool GetIsError(DependencyObject d) => (bool)d.GetValue(IsErrorProperty);
    public static void SetIsError(DependencyObject d, bool value) => d.SetValue(IsErrorProperty, value);
}
