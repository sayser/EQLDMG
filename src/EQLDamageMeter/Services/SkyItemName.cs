namespace EQLDamageMeter.Services;

public static class SkyItemName
{
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        var plus = trimmed.LastIndexOf(" +", StringComparison.Ordinal);
        if (plus <= 0) return trimmed;
        var suffix = trimmed[(plus + 2)..];
        return int.TryParse(suffix, out _) ? trimmed[..plus].Trim() : trimmed;
    }

    public static bool EqualsNormalized(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsCurrencyItem(string? name)
    {
        var normalized = Normalize(name);
        return normalized.StartsWith("Wind Rune ", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Mote of ", StringComparison.OrdinalIgnoreCase);
    }
}
