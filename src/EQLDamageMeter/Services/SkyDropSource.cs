namespace EQLDamageMeter.Services;

public static class SkyDropSource
{
    public static string Format(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return string.Empty;
        var key = note.Trim();
        return key.ToLowerInvariant() switch
        {
            "2-pos" => "Azarack Island (Island 2)",
            "3-gorga" => "Harpy Island (Island 3) · Gorgalosk",
            "4-kos" => "Thunder Island (Island 4) · Keeper of Souls",
            "5-sl" => "Spiroc Island (Island 5) · Spiroc Lord",
            "6-bz" => "Wasp Island (Island 6) · Bazzt Zzzt",
            "6" => "Wasp Island (Island 6)",
            "7-sots" => "Sister Island (Island 7) · Sister of the Spire",
            "7-trash" => "Sister Island (Island 7) · trash",
            "7" => "Sister Island (Island 7)",
            "8-eov" => "Veeshan Island (Island 8) · Eye of Veeshan",
            _ => key
        };
    }
}
