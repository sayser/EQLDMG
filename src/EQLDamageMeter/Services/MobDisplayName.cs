namespace EQLDamageMeter.Services;

/// <summary>
/// Formats mob names for UI display. Internal tracking keeps the raw log name.
/// </summary>
public static class MobDisplayName
{
    public static string Format(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        if (trimmed.Length >= 4 && trimmed.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
            return trimmed[3..].TrimStart();
        if (trimmed.Length >= 3 && trimmed.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            return trimmed[2..].TrimStart();
        return trimmed;
    }
}
