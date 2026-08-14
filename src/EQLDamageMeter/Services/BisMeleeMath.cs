namespace EQLDamageMeter.Services;

/// <summary>
/// Classic-era melee output in <c>DMG/delay</c> ratio units (same as in-game Ratio).
/// EQ delay is tenths of a second, so real DPS ≈ 10 × ratio at 0% haste.
/// </summary>
public static class BisMeleeMath
{
    /// <summary>Dual wield is a skill check for extra hits, not a second full-speed weapon.</summary>
    public const double DualWieldOffhandFactor = 0.5;

    /// <summary>
    /// Combat weapon procs are modeled at 2 PPM (delay-scaled per swing).
    /// Proc real DPS = (2/60)×hit = hit/30. In ratio units that is hit/300.
    /// </summary>
    public const double ProcPpm = 2.0;

    public const double ProcHitToRatio = 300.0;

    public sealed record Weapon(
        string Name,
        double Dmg,
        double Delay,
        double Ratio,
        bool IsTwoHand,
        BisProcInfo Proc)
    {
        public double SwingRatio => Delay > 0 && Dmg > 0 ? Dmg / Delay : Ratio;

        public double ProcRatio =>
            BisItemEffects.IsDpsProc(Proc) ? Proc.EstimatedHit / ProcHitToRatio : 0;

        public double MeleeOutput => SwingRatio + ProcRatio;
    }

    public static Weapon FromStats(string name, IReadOnlyDictionary<string, double> stats, bool twoHand,
        BisProcInfo proc)
    {
        var dmg = Get(stats, "DMG");
        var delay = Get(stats, "DELAY");
        var ratio = Get(stats, "RATIO");
        if (ratio <= 0 && dmg > 0 && delay > 0)
            ratio = dmg / delay;
        return new Weapon(name, dmg, delay, ratio, twoHand, proc);
    }

    public static double ProcRatioEquivalent(double estimatedHit) =>
        estimatedHit <= 0 ? 0 : estimatedHit / ProcHitToRatio;

    /// <summary>
    /// Offhand swings on the primary delay. Offhand procs fire on those extra hits,
    /// so they are also scaled by <see cref="DualWieldOffhandFactor"/>.
    /// </summary>
    public static double DualWieldOutput(Weapon? main, Weapon? offhand)
    {
        if (main is null) return 0;
        var offSwing = 0.0;
        var offProc = 0.0;
        if (offhand is not null && main.Delay > 0)
        {
            offSwing = offhand.Dmg / main.Delay;
            offProc = offhand.ProcRatio;
        }

        return main.MeleeOutput + DualWieldOffhandFactor * (offSwing + offProc);
    }

    public static bool PreferTwoHand(Weapon? twoHand, Weapon? oneHand, Weapon? offhand)
    {
        if (twoHand is null) return false;
        return twoHand.MeleeOutput >= DualWieldOutput(oneHand, offhand);
    }

    private static double Get(IReadOnlyDictionary<string, double> stats, string key) =>
        stats.TryGetValue(key, out var value) ? value : 0;
}
