using System.Globalization;
using System.Windows.Media;

namespace EQLDamageMeter.ViewModels;

public sealed class AbilityViewModel
{
    public required string Name { get; init; }
    public long Damage { get; init; }
    public double Dps { get; init; }
    public double Share { get; init; }
    public required Brush Color { get; init; }
    public ImageSource? Icon { get; init; }
    public bool IsPetSummary { get; init; }
    public AbilityViewModel[] Children { get; init; } = [];
    public string DamageText => Damage.ToString("N0", CultureInfo.CurrentCulture);
    public string DpsText => Dps.ToString("N1", CultureInfo.CurrentCulture);
    public string ShareText => $"{Share:0.0}%";
}
