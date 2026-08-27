namespace EQLDamageMeter.Services;

public static class NamedNpc
{
    public static bool IsBossName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        if (trimmed.Equals("You", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.StartsWith("an ", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.StartsWith("a ", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsGenericTheName(trimmed)) return false;
        return trimmed.Length >= 2;
    }

    public static bool TryReadSlainName(string message, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(message)) return false;
        if (message.StartsWith("You have been slain", StringComparison.OrdinalIgnoreCase))
            return false;

        const string youPrefix = "You have slain ";
        if (message.StartsWith(youPrefix, StringComparison.OrdinalIgnoreCase) && message.EndsWith('!'))
        {
            name = message[youPrefix.Length..^1].Trim();
            return name.Length > 0;
        }

        const string marker = " has been slain by ";
        var index = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index <= 0 || !message.EndsWith('!')) return false;
        name = message[..index].Trim();
        return name.Length > 0;
    }

    /// <summary>
    /// "the goblin" is trash. "the Hand of Veeshan" / "The Fabled X" are named.
    /// </summary>
    private static bool IsGenericTheName(string name)
    {
        if (!name.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = name[4..].Trim();
        if (rest.Length == 0) return true;
        return rest.Length > 0 && char.IsLower(rest[0]) && !rest.Contains(' ');
    }
}
