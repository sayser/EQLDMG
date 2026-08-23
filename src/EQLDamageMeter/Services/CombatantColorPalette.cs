using System.Windows.Media;

namespace EQLDamageMeter.Services;

/// <summary>Stable per-name colors for DPS bars, graphs, and fight-log actors.</summary>
public static class CombatantColorPalette
{
    private static readonly Color[] Colors =
    [
        Color.FromRgb(171, 142, 255), // purple
        Color.FromRgb(47, 216, 199),  // teal
        Color.FromRgb(64, 176, 255),  // sky
        Color.FromRgb(255, 183, 77),  // amber
        Color.FromRgb(255, 99, 132),  // rose
        Color.FromRgb(63, 205, 118),  // green
        Color.FromRgb(255, 126, 80),  // coral
        Color.FromRgb(120, 200, 255), // ice
        Color.FromRgb(232, 140, 255), // magenta
        Color.FromRgb(255, 220, 100), // gold
        Color.FromRgb(100, 230, 180), // mint
        Color.FromRgb(180, 160, 255)  // lilac
    ];

    private static readonly Brush[] Brushes = Colors.Select(FreezeBrush).ToArray();

    public static Brush ForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Brushes.Length == 0)
            return Brushes[0];
        return Brushes[IndexFor(name)];
    }

    private static int IndexFor(string name)
    {
        uint hash = 2166136261;
        foreach (var ch in name.Trim().ToUpperInvariant())
            hash = (hash ^ ch) * 16777619;
        return (int)(hash % (uint)Colors.Length);
    }

    private static Brush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
