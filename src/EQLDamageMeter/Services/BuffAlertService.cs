using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class BuffAlertService
{
    public void Play(BuffRuleSettings rule) =>
        _ = Task.Run(() => PlayCore(rule.AlertMode, rule.Sound, rule.VoiceText, rule.SpellName));

    public void Test(BuffRuleSettings rule) => Play(rule);

    private static void PlayCore(BuffAlertMode mode, BuffSoundKind sound, string voiceText, string spellName)
    {
        if (mode is BuffAlertMode.Sound or BuffAlertMode.Both) PlayBuiltInSound(sound);
        if (mode is BuffAlertMode.TextToSpeech or BuffAlertMode.Both)
        {
            Speak(string.IsNullOrWhiteSpace(voiceText) ? $"{spellName} has expired" : voiceText.Trim());
        }
    }

    private static void PlayBuiltInSound(BuffSoundKind kind)
    {
        using var stream = CreateWave(kind);
        using var player = new SoundPlayer(stream);
        player.Load();
        player.PlaySync();
    }

    private static MemoryStream CreateWave(BuffSoundKind kind)
    {
        const int sampleRate = 22_050;
        var duration = kind == BuffSoundKind.Drum ? 0.55 : 0.85;
        var sampleCount = (int)(sampleRate * duration);
        var samples = new short[sampleCount];
        var random = new Random(17);

        for (var index = 0; index < sampleCount; index++)
        {
            var time = index / (double)sampleRate;
            double sample;
            switch (kind)
            {
                case BuffSoundKind.Bell:
                    sample = Math.Sin(2 * Math.PI * 880 * time) * Math.Exp(-4.1 * time) +
                             0.42 * Math.Sin(2 * Math.PI * 1_760 * time) * Math.Exp(-5.4 * time);
                    break;
                case BuffSoundKind.Drum:
                    var frequency = 145 - (95 * time / duration);
                    sample = 0.78 * Math.Sin(2 * Math.PI * frequency * time) * Math.Exp(-7.5 * time) +
                             0.16 * ((random.NextDouble() * 2) - 1) * Math.Exp(-18 * time);
                    break;
                default:
                    var chimeFrequency = time < 0.36 ? 659.25 : 987.77;
                    var localTime = time < 0.36 ? time : time - 0.36;
                    sample = Math.Sin(2 * Math.PI * chimeFrequency * localTime) * Math.Exp(-5.2 * localTime);
                    break;
            }
            samples[index] = (short)(Math.Clamp(sample, -1, 1) * short.MaxValue * 0.55);
        }

        var stream = new MemoryStream(44 + samples.Length * sizeof(short));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            var dataLength = samples.Length * sizeof(short);
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataLength);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * sizeof(short));
            writer.Write((short)sizeof(short));
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(dataLength);
            foreach (var sample in samples) writer.Write(sample);
        }
        stream.Position = 0;
        return stream;
    }

    private static void Speak(string text)
    {
        object? voice = null;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (voiceType is null) return;
            voice = Activator.CreateInstance(voiceType);
            voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [text, 0]);
        }
        catch (COMException)
        {
            // Speech is optional; sound alerts continue to work if SAPI is unavailable.
        }
        catch (TargetInvocationException)
        {
            // Installed Windows voices can fail independently of the tracker.
        }
        finally
        {
            if (voice is not null && Marshal.IsComObject(voice)) Marshal.FinalReleaseComObject(voice);
        }
    }
}
