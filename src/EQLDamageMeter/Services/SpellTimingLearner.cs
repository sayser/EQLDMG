using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public enum SpellTimingSampleKind
{
    Cast,
    Duration
}

public sealed record SpellTimingSample(Guid RuleId, SpellTimingSampleKind Kind, double Seconds);

/// <summary>
/// EMA calibration of cast/duration from completed casts and natural worn-off/fade only.
/// Duration waits for several consistent samples before replacing the catalog baseline.
/// Updates rule defaults for the next cast; does not rewrite mid-flight timers.
/// </summary>
public static class SpellTimingLearner
{
    public const double EmaAlpha = 0.3;
    public const int MinCastSamplesToApply = 2;
    /// <summary>Collect this many natural expiries before changing DurationSeconds.</summary>
    public const int MinDurationSamplesToApply = 3;
    /// <summary>EQ logs are 1-second resolution; sub-second gaps are not trustworthy.</summary>
    public const double MinCastSeconds = 1.0;
    public const double MaxCastSeconds = 30;
    public const double MinDurationSeconds = 1;
    public const double MaxDurationSeconds = 6 * 60 * 60;

    public static double ApplyEma(double current, double sample, double alpha = EmaAlpha) =>
        ((1.0 - alpha) * current) + (alpha * sample);

    public static bool IsSaneCastSample(double seconds) =>
        seconds is >= MinCastSeconds and <= MaxCastSeconds;

    public static bool IsSaneDurationSample(double seconds) =>
        seconds is >= MinDurationSeconds and <= MaxDurationSeconds;

    /// <summary>
    /// Rejects early breaks (mez/root/charm) and early kills. Short DoTs use a
    /// proportional floor; longer spells require ≥70% of the current rule duration
    /// (min 12s) so a half-duration death cannot drag Odium from 30s toward 0:24.
    /// </summary>
    public static bool IsPlausibleFullDurationSample(BuffRuleSettings rule, double sampleSeconds)
    {
        if (!IsSaneDurationSample(sampleSeconds)) return false;
        if (rule.DurationSeconds <= 0) return true;

        double floor;
        if (rule.DurationSeconds <= 12)
            floor = Math.Max(MinDurationSeconds, rule.DurationSeconds * 0.5);
        else
            floor = Math.Max(12.0, rule.DurationSeconds * 0.70);

        return sampleSeconds >= floor;
    }

    public static bool TryApplySample(BuffRuleSettings rule, SpellTimingSample sample,
        out BuffRuleSettings updated)
    {
        updated = rule;
        if (sample.RuleId != rule.Id) return false;

        return sample.Kind switch
        {
            SpellTimingSampleKind.Cast => TryApplyCast(rule, sample.Seconds, out updated),
            SpellTimingSampleKind.Duration => TryApplyDuration(rule, sample.Seconds, out updated),
            _ => false
        };
    }

    public static bool TryApplyCast(BuffRuleSettings rule, double sampleSeconds,
        out BuffRuleSettings updated)
    {
        updated = rule;
        if (rule.CastSource == SpellTimingSource.Manual) return false;
        if (!IsSaneCastSample(sampleSeconds)) return false;

        var count = rule.CastSampleCount + 1;
        var sum = rule.CastSampleSum + sampleSeconds;
        if (count < MinCastSamplesToApply)
        {
            updated = rule with { CastSampleCount = count, CastSampleSum = sum };
            return true;
        }

        // First applied baseline is the mean of collected samples; later casts EMA.
        var next = rule.CastSource == SpellTimingSource.Catalog
            ? sum / count
            : ApplyEma(rule.CastTimeSeconds, sampleSeconds);
        next = Math.Clamp(Math.Round(next, 2), MinCastSeconds, MaxCastSeconds);
        updated = rule with
        {
            CastTimeSeconds = next,
            CastSource = SpellTimingSource.Learned,
            CastSampleCount = count,
            CastSampleSum = 0
        };
        return true;
    }

    public static bool TryApplyDuration(BuffRuleSettings rule, double sampleSeconds,
        out BuffRuleSettings updated)
    {
        updated = rule;
        if (rule.DurationSource == SpellTimingSource.Manual) return false;
        if (!IsPlausibleFullDurationSample(rule, sampleSeconds)) return false;

        // Reject outliers vs running mean while buffering, and vs learned value after.
        if (rule.DurationSampleCount > 0 &&
            rule.DurationSource == SpellTimingSource.Catalog &&
            rule.DurationSampleSum > 0)
        {
            var runningMean = rule.DurationSampleSum / rule.DurationSampleCount;
            if (sampleSeconds < runningMean * 0.8 || sampleSeconds > runningMean * 1.35)
            {
                // A bad first buffer can stall forever; if this sample is closer to the
                // catalog seed than the running mean, replace the buffer with it.
                if (rule.DurationSeconds > 0 &&
                    Math.Abs(sampleSeconds - rule.DurationSeconds) <
                    Math.Abs(runningMean - rule.DurationSeconds))
                {
                    updated = rule with { DurationSampleCount = 1, DurationSampleSum = sampleSeconds };
                    return true;
                }

                return false;
            }
        }
        else if (rule.DurationSource == SpellTimingSource.Learned &&
                 rule.DurationSeconds > 0 &&
                 (sampleSeconds < rule.DurationSeconds * 0.8 ||
                  sampleSeconds > rule.DurationSeconds * 1.35))
        {
            return false;
        }

        var count = rule.DurationSampleCount + 1;
        var sum = rule.DurationSampleSum + sampleSeconds;
        if (count < MinDurationSamplesToApply)
        {
            updated = rule with { DurationSampleCount = count, DurationSampleSum = sum };
            return true;
        }

        // Baseline = mean of the collected natural expiries (catalog seed stays until then).
        // Later expiries EMA-smooth so one outlier cannot thrash the timer.
        double next;
        if (rule.DurationSource == SpellTimingSource.Catalog)
            next = sum / count;
        else
            next = ApplyEma(rule.DurationSeconds, sampleSeconds);

        var nextSeconds = (int)Math.Clamp(Math.Round(next), MinDurationSeconds, MaxDurationSeconds);
        updated = rule with
        {
            DurationSeconds = nextSeconds,
            DurationSource = SpellTimingSource.Learned,
            DurationSampleCount = count,
            DurationSampleSum = 0
        };
        return true;
    }
}
