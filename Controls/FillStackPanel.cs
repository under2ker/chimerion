using System.Windows;
using System.Windows.Controls;

namespace Dnex.Controls;

/// <summary>
/// Вертикальный стек, в котором последний элемент забирает оставшуюся высоту. Нужен рамке «Проверка подключения»:
/// соседняя рамка задает высоту строки, и плитки сервисов растягиваются до нее вместо пустой полосы снизу.
/// </summary>
public sealed class FillStackPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            var child = visible[i];
            double h = child.DesiredSize.Height;
            if (i == visible.Count - 1) h = Math.Max(h, final.Height - y);
            child.Arrange(new Rect(0, y, final.Width, h));
            y += h;
        }
        return final;
    }
}
