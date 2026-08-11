using System.Windows.Media;

namespace EQLDamageMeter.Services;

public sealed class MouseHighlightSettings
{
    public bool Enabled { get; set; }
    public string ColorHex { get; set; } = "#FF5522";
    public double Diameter { get; set; } = 48;
    public double Thickness { get; set; } = 3;
    public double Opacity { get; set; } = 0.85;
    public bool Blink { get; set; }
    /// <summary>Blink cycles per second (soft pulse).</summary>
    public double BlinkHz { get; set; } = 2.0;
    public bool SecondRing { get; set; }
    public double SecondDiameter { get; set; } = 84;

    public Color ToColor()
    {
        var hex = (ColorHex ?? "#FF5522").Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return Color.FromRgb(r, g, b);
        return Color.FromRgb(255, 85, 34);
    }

    public static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
