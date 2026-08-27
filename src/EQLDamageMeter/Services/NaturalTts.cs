using System.IO;
using KokoroSharp;
using KokoroSharp.Core;

namespace EQLDamageMeter.Services;

public static class NaturalTts
{
    private static readonly object Gate = new();
    private static KokoroTTS? _engine;
    private static bool _voicesLoaded;

    public static string ModelFolder => AppPaths.Combine("natural-voice");
    public static string ModelPath => Path.Combine(ModelFolder, "kokoro.onnx");

    public static bool IsDownloaded
    {
        get
        {
            try
            {
                return File.Exists(ModelPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static IReadOnlyList<VoiceOption> Voices { get; } =
    [
        new("", "Default voice"),
        new("af_heart", "Heart (US, female)"),
        new("af_nicole", "Nicole (US, female)"),
        new("af_aoede", "Aoede (US, female)"),
        new("af_kore", "Kore (US, female)"),
        new("af_sarah", "Sarah (US, female)"),
        new("af_nova", "Nova (US, female)"),
        new("af_sky", "Sky (US, female)"),
        new("af_alloy", "Alloy (US, female)"),
        new("af_jessica", "Jessica (US, female)"),
        new("af_river", "River (US, female)"),
        new("af_bella", "Bella (US, female)"),
        new("am_fenrir", "Fenrir (US, male)"),
        new("am_michael", "Michael (US, male)"),
        new("am_puck", "Puck (US, male)"),
        new("am_echo", "Echo (US, male)"),
        new("am_eric", "Eric (US, male)"),
        new("am_liam", "Liam (US, male)"),
        new("am_onyx", "Onyx (US, male)"),
        new("am_santa", "Santa (US, male)"),
        new("am_adam", "Adam (US, male)"),
        new("bf_emma", "Emma (UK, female)"),
        new("bf_isabella", "Isabella (UK, female)"),
        new("bf_alice", "Alice (UK, female)"),
        new("bf_lily", "Lily (UK, female)"),
        new("bm_george", "George (UK, male)"),
        new("bm_daniel", "Daniel (UK, male)"),
        new("bm_fable", "Fable (UK, male)"),
        new("bm_lewis", "Lewis (UK, male)")
    ];

    public static async Task<string> DownloadAsync(Action<float>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(ModelFolder);
        var previous = Directory.GetCurrentDirectory();
        KokoroTTS? loaded = null;
        try
        {
            Directory.SetCurrentDirectory(ModelFolder);
            loaded = await KokoroTTS.LoadModelAsync(KModel.float32, percent =>
            {
                onProgress?.Invoke(percent);
            });
        }
        catch (Exception ex)
        {
            loaded?.Dispose();
            return "Download failed: " + ex.Message;
        }
        finally
        {
            try { Directory.SetCurrentDirectory(previous); }
            catch (IOException) { }
        }

        lock (Gate)
        {
            _engine?.Dispose();
            _engine = loaded;
            EnsureVoicesLoaded();
        }

        return File.Exists(ModelPath) ? string.Empty : "Natural voice pack could not be saved.";
    }

    public static bool Speak(string text, string voiceId, int volumePercent)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        lock (Gate)
        {
            if (_engine is null)
            {
                if (!IsDownloaded) return false;
                try
                {
                    _engine = KokoroTTS.LoadModel(ModelPath);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            EnsureVoicesLoaded();
            var id = string.IsNullOrWhiteSpace(voiceId) ? "af_heart" : voiceId.Trim();
            KokoroVoice? voice;
            try
            {
                voice = KokoroVoiceManager.GetVoice(id);
            }
            catch (InvalidOperationException)
            {
                try { voice = KokoroVoiceManager.GetVoice("af_heart"); }
                catch (InvalidOperationException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }

            if (voice is null) return false;
            try
            {
                _engine.SetVolume(Math.Clamp(volumePercent, 0, 100) / 100f);
                _engine.SpeakFast(text, voice);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static void EnsureVoicesLoaded()
    {
        if (_voicesLoaded) return;
        foreach (var folder in VoiceFolders())
        {
            try
            {
                if (Directory.Exists(folder))
                    KokoroVoiceManager.LoadVoicesFromPath(folder);
            }
            catch (Exception)
            {
                // Voices may already be loaded from the default package path.
            }
        }

        _voicesLoaded = true;
    }

    private static IEnumerable<string> VoiceFolders()
    {
        yield return Path.Combine(AppPaths.AppDirectory, "voices");
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
            yield return Path.Combine(baseDir, "voices");
    }
}
