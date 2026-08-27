namespace EQLDamageMeter.Services;

public enum VoiceEngineKind
{
    Windows = 0,
    Natural = 1
}

public sealed class VoiceSettings
{
    public VoiceEngineKind Engine { get; set; } = VoiceEngineKind.Windows;
    public string WindowsVoiceId { get; set; } = string.Empty;
    public string NaturalVoiceId { get; set; } = "af_heart";
    public bool BossDefeatSoundEnabled { get; set; }
    public string BossDefeatSoundPath { get; set; } = EventSoundService.DefaultSoundFileName;
}

public sealed record VoiceOption(string Id, string Label)
{
    public override string ToString() => Label;
}
