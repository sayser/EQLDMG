using System.IO;
using NAudio.Wave;

namespace EQLDamageMeter.Services;

public static class CustomSoundPlayer
{
    private static readonly object Gate = new();
    private static IWavePlayer? _player;
    private static AudioFileReader? _reader;

    public static bool PlayFile(string? path, int volumePercent)
    {
        if (string.IsNullOrWhiteSpace(path) || volumePercent <= 0) return false;
        if (!File.Exists(path)) return false;
        try
        {
            lock (Gate)
            {
                StopLocked();
                _reader = new AudioFileReader(path)
                {
                    Volume = Math.Clamp(volumePercent, 0, 100) / 100f
                };
                _player = new WaveOutEvent();
                _player.Init(_reader);
                _player.Play();
            }

            return true;
        }
        catch (Exception)
        {
            lock (Gate)
                StopLocked();
            return false;
        }
    }

    private static void StopLocked()
    {
        try { _player?.Stop(); } catch (Exception) { }
        try { _player?.Dispose(); } catch (Exception) { }
        try { _reader?.Dispose(); } catch (Exception) { }
        _player = null;
        _reader = null;
    }
}
