using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopShortcuts;

public sealed class WidgetResizeLayer : Grid
{
    public WidgetResizeLayer()
    {
        Add(WidgetResizeEdge.Left, Cursors.SizeWE, 6, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch, new(0, 12, 0, 12));
        Add(WidgetResizeEdge.Right, Cursors.SizeWE, 6, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch, new(0, 12, 0, 15));
        Add(WidgetResizeEdge.Top, Cursors.SizeNS, double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Top, new(12, 0, 12, 0));
        Add(WidgetResizeEdge.Bottom, Cursors.SizeNS, double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, new(12, 0, 15, 0));
        Add(WidgetResizeEdge.Left | WidgetResizeEdge.Top, Cursors.SizeNWSE, 12, 12, HorizontalAlignment.Left, VerticalAlignment.Top, new());
        Add(WidgetResizeEdge.Right | WidgetResizeEdge.Top, Cursors.SizeNESW, 12, 12, HorizontalAlignment.Right, VerticalAlignment.Top, new());
        Add(WidgetResizeEdge.Left | WidgetResizeEdge.Bottom, Cursors.SizeNESW, 12, 12, HorizontalAlignment.Left, VerticalAlignment.Bottom, new());
        Add(WidgetResizeEdge.Right | WidgetResizeEdge.Bottom, Cursors.SizeNWSE, 15, 15, HorizontalAlignment.Right, VerticalAlignment.Bottom, new(), drawHandle: true);
    }

    private void Add(WidgetResizeEdge edge, Cursor cursor, double width, double height,
        HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness margin, bool drawHandle = false)
    {
        var template = new ControlTemplate(typeof(Thumb));
        var surface = new FrameworkElementFactory(typeof(Grid));
        surface.SetValue(BackgroundProperty, Brushes.Transparent);
        if (drawHandle)
        {
            var mark = new FrameworkElementFactory(typeof(Path));
            mark.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            mark.SetValue(VerticalAlignmentProperty, VerticalAlignment.Bottom);
            mark.SetValue(MarginProperty, new Thickness(0, 0, 3, 3));
            mark.SetValue(OpacityProperty, 0.64);
            mark.SetValue(Shape.StrokeThicknessProperty, 1.0);
            mark.SetValue(Path.DataProperty, Geometry.Parse("M 0,8 L 8,0 M 4,8 L 8,4"));
            mark.SetResourceReference(Shape.StrokeProperty, "ShortcutFolderBrush");
            surface.AppendChild(mark);
        }

        template.VisualTree = surface;
        Children.Add(new Thumb
        {
            Focusable = false, Cursor = cursor, Tag = edge, Template = template,
            Width = width, Height = height, Margin = margin,
            HorizontalAlignment = horizontal, VerticalAlignment = vertical,
            ToolTip = drawHandle ? "끌어서 크기 조절" : null
        });
    }
}
