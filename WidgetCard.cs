using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopShortcuts;

public sealed class WidgetCard : Border
{
    private const int MaxTransparency = 90;
    private int? _transparency;

    public static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(
        nameof(ThemeBackground), typeof(Brush), typeof(WidgetCard),
        new FrameworkPropertyMetadata(null, (element, _) => ((WidgetCard)element).UpdateBackground()));

    public Brush? ThemeBackground
    {
        get => (Brush?)GetValue(ThemeBackgroundProperty);
        set => SetValue(ThemeBackgroundProperty, value);
    }

    internal int? Transparency
    {
        get => _transparency;
        set
        {
            int? transparency = value is int percent ? Math.Clamp(percent, 0, MaxTransparency) : null;
            if (_transparency != transparency)
            {
                _transparency = transparency;
                UpdateBackground();
            }
        }
    }

    internal MenuItem CreateTransparencyMenu(Func<int?, bool> apply)
    {
        var menu = new MenuItem();
        AddChoice("기본값", null);
        menu.Items.Add(new Separator());
        for (int percent = 0; percent <= MaxTransparency; percent += 10)
        {
            AddChoice(percent == 0 ? "0% (불투명)" : $"{percent}%", percent);
        }

        UpdateChecks();
        menu.IsVisibleChanged += (_, _) => UpdateChecks();
        menu.SubmenuOpened += (_, _) => UpdateChecks();
        return menu;

        void AddChoice(string label, int? value)
        {
            var choice = new MenuItem { Header = label, Tag = value, IsCheckable = true };
            choice.Click += (_, _) =>
            {
                if (apply(value))
                {
                    Transparency = value;
                }
                UpdateChecks();
            };
            menu.Items.Add(choice);
        }

        void UpdateChecks()
        {
            menu.Header = Transparency is int percent ? $"배경 투명도 ({percent}%)" : "배경 투명도";
            foreach (object item in menu.Items)
            {
                if (item is MenuItem choice)
                {
                    choice.IsChecked = (int?)choice.Tag == Transparency;
                }
            }
        }
    }

    private void UpdateBackground()
    {
        if (ThemeBackground is not Brush theme || Transparency is not int percent)
        {
            Background = ThemeBackground;
            return;
        }

        double opacity = (100 - percent) / 100.0;
        byte alpha = (byte)Math.Round(byte.MaxValue * opacity);
        Brush background = theme.CloneCurrentValue();
        switch (background)
        {
            case GradientBrush gradient:
                foreach (GradientStop stop in gradient.GradientStops)
                {
                    stop.Color = WithAlpha(stop.Color, alpha);
                }
                break;
            case SolidColorBrush solid:
                solid.Color = WithAlpha(solid.Color, alpha);
                break;
            default:
                background.Opacity = opacity;
                break;
        }

        background.Freeze();
        Background = background;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
