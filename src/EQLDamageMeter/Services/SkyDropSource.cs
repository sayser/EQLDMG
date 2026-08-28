namespace EQLDamageMeter.Services;

public readonly record struct SkyDropPlace(int IslandOrder, string IslandName, string BossName)
{
    public bool HasBoss => BossName.Length > 0;

    public string Display =>
        IslandName.Length == 0 ? string.Empty :
        HasBoss ? $"{IslandName} · {BossName}" :
        IslandName;
}

public static class SkyDropSource
{
    public static string Format(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return string.Empty;
        return Parse(note).Display;
    }

    public static SkyDropPlace Parse(string? note, string? itemName = null)
    {
        if (SkyItemName.IsCurrencyItem(itemName))
            return new(0, "Wind Runes", string.Empty);

        if (string.IsNullOrWhiteSpace(note))
            return new(90, "Other", string.Empty);

        var trimmed = note.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "2-pos" => new(2, "Azarack Island (Island 2)", string.Empty),
            "3-gorga" => new(3, "Harpy Island (Island 3)", "Gorgalosk"),
            "4-kos" => new(4, "Thunder Island (Island 4)", "Keeper of Souls"),
            "5-sl" => new(5, "Spiroc Island (Island 5)", "Spiroc Lord"),
            "6-bz" => new(6, "Wasp Island (Island 6)", "Bazzt Zzzt"),
            "6" => new(6, "Wasp Island (Island 6)", string.Empty),
            "7-sots" => new(7, "Sister Island (Island 7)", "Sister of the Spire"),
            "7-trash" => new(7, "Sister Island (Island 7)", "trash"),
            "7" => new(7, "Sister Island (Island 7)", string.Empty),
            "8-eov" => new(8, "Veeshan Island (Island 8)", "Eye of Veeshan"),
            _ => new(91, trimmed, string.Empty)
        };
    }
}
