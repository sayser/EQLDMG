namespace EQLDamageMeter.Services;

/// <summary>
/// Monk specials print as a generic verb after a one-time "You will now use …" line.
/// Strike carries Tiger Claw / Eagle Strike / Dragon Punch / Tail Rake; kick carries
/// Kick / Round Kick / Flying Kick. Bash is left alone — Slam never displaces it in the log.
/// </summary>
public sealed class SpecialAttackNames
{
    private static readonly Dictionary<string, string> LaneBySkill =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tiger Claw"] = "Strike",
            ["Eagle Strike"] = "Strike",
            ["Dragon Punch"] = "Strike",
            ["Tail Rake"] = "Strike",
            ["Kick"] = "Kick",
            ["Round Kick"] = "Kick",
            ["Flying Kick"] = "Kick"
        };

    public static bool IsNamedSpecial(string ability) => LaneBySkill.ContainsKey(ability);

    private readonly Dictionary<string, string> _activeByLane = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(string message)
    {
        if (!TryReadSwitch(message, out var skill)) return;
        if (!LaneBySkill.TryGetValue(skill, out var lane)) return;
        _activeByLane[lane] = skill;
    }

    public string Resolve(string ability)
    {
        if (!_activeByLane.TryGetValue(ability, out var named) || string.IsNullOrWhiteSpace(named))
            return ability;
        return named;
    }

    public static bool TryReadSwitch(string message, out string skill)
    {
        skill = "";
        const string prefix = "You will now use ";
        if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!message.Contains(" while ", StringComparison.OrdinalIgnoreCase)) return false;
        if (!message.Contains("attacking", StringComparison.OrdinalIgnoreCase)) return false;

        var rest = message[prefix.Length..];
        var instead = rest.IndexOf(" instead of ", StringComparison.OrdinalIgnoreCase);
        var whileAt = rest.IndexOf(" while ", StringComparison.OrdinalIgnoreCase);
        if (whileAt <= 0) return false;
        var nameSpan = instead > 0 && instead < whileAt ? rest[..instead] : rest[..whileAt];
        skill = nameSpan.Trim().TrimEnd('.');
        return skill.Length > 0;
    }
}
