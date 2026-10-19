using System.Windows.Media;

namespace TidyDesktop.Boxes;

/// <summary>Turns a box's saved color and opacity into the brushes its windows draw with.</summary>
internal static class BoxStyle
{
    public const string DefaultColor = "#18181C";
    public const double DefaultOpacity = 0.47;
    public const double MinOpacity = 0.1;

    /// <summary>Swatches offered in the style window.</summary>
    public static readonly string[] Presets =
    {
        "#18181C", "#3A3F4B", "#1F3A5F", "#1E4D4A", "#2F4F2F",
        "#5A4A1E", "#6B2D2D", "#4B2D5E", "#7A7A7A", "#E8E8E8",
    };

    public static Color ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color color)
                return Color.FromRgb(color.R, color.G, color.B);
        }
        catch (FormatException)
        {
        }
        return (Color)ColorConverter.ConvertFromString(DefaultColor);
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static double ClampOpacity(double opacity) => Math.Clamp(opacity, MinOpacity, 1.0);

    public static Brush BodyBrush(BoxModel model) => Brush(model.Color, ClampOpacity(model.Opacity));

    // The title bar stays a little more solid than the body so the name remains readable.
    public static Brush TitleBrush(BoxModel model) =>
        Brush(model.Color, Math.Min(1.0, ClampOpacity(model.Opacity) + 0.3));

    public static Brush TextBrush(BoxModel model)
    {
        var c = ParseColor(model.Color);
        var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        return luminance > 0.6 ? Brushes.Black : Brushes.White;
    }

    private static Brush Brush(string hex, double opacity)
    {
        var c = ParseColor(hex);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
