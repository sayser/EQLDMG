using System.Reflection;
using System.Runtime.InteropServices;

namespace EQLDamageMeter.Services;

public static class WindowsTts
{
    public static IReadOnlyList<VoiceOption> ListVoices()
    {
        object? voice = null;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (voiceType is null) return [new VoiceOption("", "Default voice")];
            voice = Activator.CreateInstance(voiceType);
            if (voice is null) return [new VoiceOption("", "Default voice")];
            var tokens = voiceType.InvokeMember("GetVoices", BindingFlags.InvokeMethod, null, voice, []);
            if (tokens is null) return [new VoiceOption("", "Default voice")];
            var tokenType = tokens.GetType();
            var count = Convert.ToInt32(tokenType.InvokeMember("Count", BindingFlags.GetProperty, null, tokens, []));
            var list = new List<VoiceOption> { new("", "Default voice") };
            for (var i = 0; i < count; i++)
            {
                var token = tokenType.InvokeMember("Item", BindingFlags.InvokeMethod, null, tokens, [i]);
                if (token is null) continue;
                var description = token.GetType()
                    .InvokeMember("GetDescription", BindingFlags.InvokeMethod, null, token, [])
                    ?.ToString();
                if (string.IsNullOrWhiteSpace(description)) continue;
                list.Add(new VoiceOption(description, description));
            }

            return list;
        }
        catch (COMException)
        {
            return [new VoiceOption("", "Default voice")];
        }
        catch (TargetInvocationException)
        {
            return [new VoiceOption("", "Default voice")];
        }
        finally
        {
            if (voice is not null && Marshal.IsComObject(voice))
                Marshal.FinalReleaseComObject(voice);
        }
    }

    public static bool Speak(string text, string voiceId, int volumePercent)
    {
        object? voice = null;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (voiceType is null) return false;
            voice = Activator.CreateInstance(voiceType);
            if (voice is null) return false;
            if (!string.IsNullOrWhiteSpace(voiceId))
                TrySetVoice(voiceType, voice, voiceId);
            voiceType.InvokeMember("Volume", BindingFlags.SetProperty, null, voice,
                [Math.Clamp(volumePercent, 0, 100)]);
            voiceType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, [text, 0]);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (MissingMethodException)
        {
            return false;
        }
        finally
        {
            if (voice is not null && Marshal.IsComObject(voice))
                Marshal.FinalReleaseComObject(voice);
        }
    }

    private static void TrySetVoice(Type voiceType, object voice, string voiceId)
    {
        var tokens = voiceType.InvokeMember("GetVoices", BindingFlags.InvokeMethod, null, voice, []);
        if (tokens is null) return;
        var tokenType = tokens.GetType();
        var count = Convert.ToInt32(tokenType.InvokeMember("Count", BindingFlags.GetProperty, null, tokens, []));
        for (var i = 0; i < count; i++)
        {
            var token = tokenType.InvokeMember("Item", BindingFlags.InvokeMethod, null, tokens, [i]);
            if (token is null) continue;
            var description = token.GetType()
                .InvokeMember("GetDescription", BindingFlags.InvokeMethod, null, token, [])
                ?.ToString();
            if (!string.Equals(description, voiceId, StringComparison.OrdinalIgnoreCase)) continue;
            voiceType.InvokeMember("Voice", BindingFlags.SetProperty, null, voice, [token]);
            return;
        }
    }
}
