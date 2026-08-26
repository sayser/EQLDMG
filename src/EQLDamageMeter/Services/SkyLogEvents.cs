using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public static partial class SkyLogEvents
{
    public static bool IsCandidate(string text) =>
        text.Contains("looted", StringComparison.Ordinal) ||
        text.Contains("destroyed", StringComparison.Ordinal) ||
        text.Contains("You offered ", StringComparison.Ordinal) ||
        text.Contains("complete the trade", StringComparison.Ordinal) ||
        text.Contains("cancelled the trade", StringComparison.Ordinal) ||
        text.Contains("merged two items together", StringComparison.Ordinal);

    public static bool TryReadDestroyed(string message, out string itemName, out int count)
    {
        itemName = string.Empty;
        count = 1;
        var match = DestroyedRegex().Match(message);
        if (!match.Success) return false;
        count = int.Parse(match.Groups["count"].Value);
        itemName = SkyItemName.Normalize(match.Groups["item"].Value);
        return itemName.Length > 0;
    }

    public static bool TryReadInventoryMerge(string message, out string itemName)
    {
        itemName = string.Empty;
        var match = InventoryMergeRegex().Match(message);
        if (!match.Success) return false;
        itemName = SkyItemName.Normalize(match.Groups["item"].Value);
        return itemName.Length > 0;
    }

    public static bool TryReadOffered(string message, out string itemName, out int count, out string npc)
    {
        itemName = npc = string.Empty;
        count = 1;
        var match = OfferedRegex().Match(message);
        if (!match.Success) return false;
        count = int.Parse(match.Groups["count"].Value);
        itemName = SkyItemName.Normalize(match.Groups["item"].Value);
        npc = match.Groups["npc"].Value.Trim();
        return itemName.Length > 0 && npc.Length > 0;
    }

    public static bool TryReadTradeComplete(string message, out string npc)
    {
        npc = string.Empty;
        var match = TradeCompleteRegex().Match(message);
        if (!match.Success) return false;
        npc = match.Groups["npc"].Value.Trim();
        return npc.Length > 0;
    }

    public static bool IsTradeCancelled(string message) =>
        message.Contains("cancelled the trade", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^You successfully destroyed (?<count>\d+) (?<item>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex DestroyedRegex();

    [GeneratedRegex(@"^You have successfully merged two items together to create a new item: (?<item>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex InventoryMergeRegex();

    [GeneratedRegex(@"^You offered (?<count>\d+) (?<item>.+) to (?<npc>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex OfferedRegex();

    [GeneratedRegex(@"^You complete the trade with (?<npc>.+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex TradeCompleteRegex();
}
