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

    public void PlayLootAlert(string itemName, BuffSoundKind sound, BuffAlertMode mode,
        string? voiceText = null) =>
        _ = Task.Run(() =>
        {
            var spoken = string.IsNullOrWhiteSpace(voiceText)
                ? $"{itemName} looted"
                : voiceText.Trim();
            PlayCore(mode, sound, spoken, itemName);
        });

    public void Test(BuffRuleSettings rule) => Play(rule);

    private static void PlayCore(BuffAlertMode mode, BuffSoundKind sound, string voiceText, string spellName)
    {
        mode = BuffAlertModeOptions.Normalize(mode);
        if (mode == BuffAlertMode.Sound)
        {
            PlayBuiltInSound(sound);
            return;
        }

        Speak(string.IsNullOrWhiteSpace(voiceText) ? $"{spellName} has expired" : voiceText.Trim());
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
        var duration = kind switch
        {
            BuffSoundKind.Drum or BuffSoundKind.Thud or BuffSoundKind.Knock or BuffSoundKind.Click => 0.45,
            BuffSoundKind.Fanfare or BuffSoundKind.Cascade or BuffSoundKind.Siren => 1.05,
            BuffSoundKind.Horn or BuffSoundKind.Gong => 0.95,
            _ => 0.75
        };
        var sampleCount = (int)(sampleRate * duration);
        var samples = new short[sampleCount];
        var random = new Random(HashCode.Combine((int)kind, 17));

        for (var index = 0; index < sampleCount; index++)
        {
            var time = index / (double)sampleRate;
            var sample = RenderSample(kind, time, duration, random);
            samples[index] = (short)(Math.Clamp(sample, -1, 1) * short.MaxValue * 0.55);
        }

        var stream = new MemoryStream(44 + samples.Length * sizeof(short));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
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

    private static double RenderSample(BuffSoundKind kind, double time, double duration, Random random) =>
        kind switch
        {
            BuffSoundKind.Bell =>
                Tone(880, time, 4.1) + 0.42 * Tone(1_760, time, 5.4),
            BuffSoundKind.Chime =>
                Tone(time < 0.36 ? 659.25 : 987.77, time < 0.36 ? time : time - 0.36, 5.2),
            BuffSoundKind.Drum =>
                0.78 * Tone(145 - (95 * time / duration), time, 7.5) +
                0.16 * Noise(random) * Math.Exp(-18 * time),
            BuffSoundKind.Ping =>
                Tone(1_320, time, 9.5),
            BuffSoundKind.Alert =>
                Tone(740 + 180 * Math.Sin(2 * Math.PI * 6 * time), time, 3.2),
            BuffSoundKind.Fanfare =>
                Tone(Note(time, duration, 523.25, 659.25, 783.99, 1046.5), time, 3.8),
            BuffSoundKind.Horn =>
                0.85 * Tone(220, time, 2.4) + 0.35 * Tone(440, time, 3.1) + 0.18 * Tone(660, time, 4.0),
            BuffSoundKind.Gong =>
                0.7 * Tone(196, time, 1.6) + 0.35 * Tone(392, time, 2.2) + 0.2 * Tone(588, time, 2.8) +
                0.12 * Noise(random) * Math.Exp(-10 * time),
            BuffSoundKind.Click =>
                Noise(random) * Math.Exp(-55 * time),
            BuffSoundKind.Blip =>
                Tone(980 * (1 - 0.35 * time / duration), time, 14),
            BuffSoundKind.Pulse =>
                Tone(520, time, 4.5) * (0.55 + 0.45 * Math.Sin(2 * Math.PI * 8 * time)),
            BuffSoundKind.Siren =>
                Tone(650 + 280 * Math.Sin(2 * Math.PI * 3.5 * time), time, 1.8),
            BuffSoundKind.Knock =>
                0.9 * Tone(110, time, 18) + 0.25 * Noise(random) * Math.Exp(-30 * time),
            BuffSoundKind.Triangle =>
                Tone(1_047, time, 3.5) * SoftTriangle(1_047, time),
            BuffSoundKind.Marimba =>
                Tone(time < 0.28 ? 523.25 : 659.25, time < 0.28 ? time : time - 0.28, 7.5),
            BuffSoundKind.Glass =>
                Tone(1_568, time, 6.2) + 0.4 * Tone(2_352, time, 8.0),
            BuffSoundKind.Coin =>
                Tone(1_760, time, 12) + 0.55 * Tone(2_200, time, 16) +
                0.2 * Noise(random) * Math.Exp(-40 * time),
            BuffSoundKind.Thud =>
                0.95 * Tone(85 - 40 * time / duration, time, 10) +
                0.2 * Noise(random) * Math.Exp(-22 * time),
            BuffSoundKind.Whistle =>
                Tone(1_200 + 400 * time / duration, time, 2.8),
            BuffSoundKind.Cascade =>
                Tone(Note(time, duration, 880, 988, 1_175, 1_319, 1_568), time, 4.6),
            _ => Tone(659.25, time, 5.2)
        };

    private static double Tone(double frequency, double time, double decay) =>
        Math.Sin(2 * Math.PI * frequency * time) * Math.Exp(-decay * time);

    private static double Noise(Random random) => (random.NextDouble() * 2) - 1;

    private static double SoftTriangle(double frequency, double time)
    {
        var phase = (frequency * time) % 1.0;
        var tri = phase < 0.5 ? (phase * 4) - 1 : 3 - (phase * 4);
        return 0.65 + 0.35 * tri;
    }

    private static double Note(double time, double duration, params double[] frequencies)
    {
        if (frequencies.Length == 0) return 440;
        var slot = Math.Clamp((int)(time / duration * frequencies.Length), 0, frequencies.Length - 1);
        return frequencies[slot];
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
