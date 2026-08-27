using System.IO;

namespace EQLDamageMeter.Services;

public static class EventSoundService
{
    public const string DefaultSoundFileName = "boss-defeat-test.wav";
    public static string DefaultSoundPath => AppPaths.Combine(DefaultSoundFileName);

    private static readonly Dictionary<string, DateTime> Recent = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBundledDefault(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var name = Path.GetFileName(path.Trim());
        return name.Equals(DefaultSoundFileName, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveSoundPath(string? path) =>
        IsBundledDefault(path) ? DefaultSoundPath : path!.Trim();

    public static string PersistSoundPath(string? path) =>
        IsBundledDefault(path) ? DefaultSoundFileName : path!.Trim();

    public static void ObserveSlain(string message, Func<string, bool> isHostileTarget)
    {
        if (!SpeechPlayback.Settings.BossDefeatSoundEnabled) return;
        if (!NamedNpc.TryReadSlainName(message, out var name)) return;
        if (!NamedNpc.IsBossName(name)) return;
        if (!isHostileTarget(name)) return;

        var now = DateTime.UtcNow;
        if (Recent.TryGetValue(name, out var previous) && now - previous < TimeSpan.FromSeconds(3))
            return;
        Recent[name] = now;
        if (Recent.Count > 40)
        {
            foreach (var stale in Recent.Where(pair => now - pair.Value > TimeSpan.FromMinutes(2)).Select(pair => pair.Key).ToList())
                Recent.Remove(stale);
        }

        var soundPath = ResolveSoundPath(SpeechPlayback.Settings.BossDefeatSoundPath);
        _ = Task.Run(() => CustomSoundPlayer.PlayFile(soundPath, BuffAlertService.VolumePercent));
    }
}
