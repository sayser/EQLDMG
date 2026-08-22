using System.Globalization;
using System.Windows.Media;

namespace EQLDamageMeter.ViewModels;

public sealed class AbilityViewModel
{
    public required string Name { get; init; }
    public long Damage { get; init; }
    public int Hits { get; init; }
    public double Dps { get; init; }
    public double Ppm { get; init; }
    public double Share { get; init; }
    public required Brush Color { get; init; }
    public ImageSource? Icon { get; init; }
    public bool IsPetSummary { get; init; }
    public AbilityViewModel[] Children { get; init; } = [];
    public string DamageText => Damage.ToString("N0", CultureInfo.CurrentCulture);
    public string DpsText => Dps.ToString("N1", CultureInfo.CurrentCulture);
    public string PpmText => Ppm <= 0 ? "—" : Ppm.ToString("N1", CultureInfo.CurrentCulture);
    public string ShareText => $"{Share:0.0}%";

    public static bool SequenceEquals(AbilityViewModel[]? left, AbilityViewModel[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (!ItemEquals(left[i], right[i])) return false;
        }

        return true;
    }

    private static bool ItemEquals(AbilityViewModel left, AbilityViewModel right) =>
        left.Name == right.Name &&
        left.Damage == right.Damage &&
        left.Hits == right.Hits &&
        Math.Abs(left.Dps - right.Dps) < 0.05 &&
        Math.Abs(left.Ppm - right.Ppm) < 0.05 &&
        Math.Abs(left.Share - right.Share) < 0.05 &&
        left.IsPetSummary == right.IsPetSummary &&
        ReferenceEquals(left.Color, right.Color) &&
        ReferenceEquals(left.Icon, right.Icon) &&
        SequenceEquals(left.Children, right.Children);
}
