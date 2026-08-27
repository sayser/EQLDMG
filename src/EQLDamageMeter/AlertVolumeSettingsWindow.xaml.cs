using System.Collections.ObjectModel;
using System.Windows;
using EQLDamageMeter.Services;
using EQLDamageMeter.ViewModels;
using Microsoft.Win32;

namespace EQLDamageMeter;

public partial class AlertVolumeSettingsWindow : Window
{
    private readonly AlertVolumeSettingsViewModel _model;

    public AlertVolumeSettingsWindow(int currentVolumePercent, VoiceSettings voice)
    {
        InitializeComponent();
        _model = new AlertVolumeSettingsViewModel(currentVolumePercent, voice);
        DataContext = _model;
    }

    public int ResultVolumePercent { get; private set; } = 100;
    public VoiceSettings ResultVoice { get; private set; } = new();

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreviewSettings();
        _ = Task.Run(() => SpeechPlayback.Preview());
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!_model.CanDownload) return;
        _model.IsBusy = true;
        _model.VoiceStatus = "Downloading natural voice pack (~320 MB). This can take a few minutes…";
        try
        {
            var error = await NaturalTts.DownloadAsync(percent =>
            {
                var shown = Math.Clamp((int)(percent * 100), 0, 100);
                Dispatcher.BeginInvoke(() =>
                    _model.VoiceStatus = $"Downloading natural voice pack… {shown}%");
            });
            _model.RefreshDownloadState();
            _model.VoiceStatus = string.IsNullOrWhiteSpace(error)
                ? "Natural voice pack is ready."
                : error;
        }
        catch (Exception ex)
        {
            _model.VoiceStatus = "Download failed: " + ex.Message;
        }
        finally
        {
            _model.IsBusy = false;
        }
    }

    private void BrowseBossSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a boss defeat sound",
            Filter = "Audio files|*.wav;*.mp3;*.ogg;*.wma|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            _model.BossDefeatSoundPath = dialog.FileName;
    }

    private void TestBossSound_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_model.BossDefeatSoundPath) ||
            !CustomSoundPlayer.PlayFile(_model.BossDefeatSoundPath, _model.VolumePercent))
        {
            MessageBox.Show(this, "Could not play that file. Choose a WAV or MP3.",
                "Boss defeat music", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultVolumePercent = _model.VolumePercent;
        ResultVoice = _model.ToVoiceSettings();
        DialogResult = true;
        Close();
    }

    private void ApplyPreviewSettings()
    {
        BuffAlertService.VolumePercent = _model.VolumePercent;
        SpeechPlayback.Settings = _model.ToVoiceSettings();
    }
}

public sealed class EngineOption : ObservableObject
{
    private string _label = string.Empty;

    public required VoiceEngineKind Kind { get; init; }

    public required string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public override string ToString() => Label;
}

public sealed class AlertVolumeSettingsViewModel : ObservableObject
{
    private int _volumePercent;
    private EngineOption _selectedEngine;
    private VoiceOption? _selectedVoice;
    private bool _isBusy;
    private string _voiceStatus = string.Empty;
    private bool _bossDefeatSoundEnabled;
    private string _bossDefeatSoundPath = string.Empty;

    public AlertVolumeSettingsViewModel(int volumePercent, VoiceSettings voice)
    {
        _volumePercent = Math.Clamp(volumePercent, 0, 100);
        _bossDefeatSoundEnabled = voice.BossDefeatSoundEnabled;
        _bossDefeatSoundPath = EventSoundService.ResolveSoundPath(voice.BossDefeatSoundPath);
        Engines =
        [
            new EngineOption { Kind = VoiceEngineKind.Windows, Label = "Windows Voice (built-in)" },
            new EngineOption
            {
                Kind = VoiceEngineKind.Natural,
                Label = NaturalVoiceLabel()
            }
        ];
        _selectedEngine = Engines.FirstOrDefault(engine => engine.Kind == voice.Engine) ?? Engines[0];
        ReloadVoices(voice.Engine == VoiceEngineKind.Natural ? voice.NaturalVoiceId : voice.WindowsVoiceId);
        RefreshDownloadState();
        if (_selectedEngine.Kind == VoiceEngineKind.Natural && !NaturalTts.IsDownloaded)
            VoiceStatus = "Choose Download natural voice pack (~320 MB) before spoken alerts can use it.";
    }

    public ObservableCollection<EngineOption> Engines { get; }
    public ObservableCollection<VoiceOption> Voices { get; } = [];

    public EngineOption SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (!SetProperty(ref _selectedEngine, value)) return;
            var keep = _selectedVoice?.Id;
            ReloadVoices(keep);
            RefreshDownloadState();
        }
    }

    public VoiceOption? SelectedVoice
    {
        get => _selectedVoice;
        set => SetProperty(ref _selectedVoice, value);
    }

    public int VolumePercent
    {
        get => _volumePercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volumePercent, clamped)) return;
            RaisePropertyChanged(nameof(VolumeText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaisePropertyChanged(nameof(CanDownload));
        }
    }

    public string VoiceStatus
    {
        get => _voiceStatus;
        set => SetProperty(ref _voiceStatus, value);
    }

    public bool BossDefeatSoundEnabled
    {
        get => _bossDefeatSoundEnabled;
        set => SetProperty(ref _bossDefeatSoundEnabled, value);
    }

    public string BossDefeatSoundPath
    {
        get => _bossDefeatSoundPath;
        set => SetProperty(ref _bossDefeatSoundPath, value);
    }

    public string VolumeText => $"{VolumePercent}%";
    public bool ShowDownload => SelectedEngine.Kind == VoiceEngineKind.Natural;
    public bool CanDownload => ShowDownload && !IsBusy;
    public string DownloadButtonText => NaturalTts.IsDownloaded
        ? "Re-download natural voice pack"
        : "Download natural voice pack";

    public void RefreshDownloadState()
    {
        RaisePropertyChanged(nameof(ShowDownload));
        RaisePropertyChanged(nameof(CanDownload));
        RaisePropertyChanged(nameof(DownloadButtonText));
        var natural = Engines.FirstOrDefault(engine => engine.Kind == VoiceEngineKind.Natural);
        if (natural is not null)
            natural.Label = NaturalVoiceLabel();
        RaisePropertyChanged(nameof(Engines));
    }

    public VoiceSettings ToVoiceSettings() => new()
    {
        Engine = SelectedEngine.Kind,
        WindowsVoiceId = SelectedEngine.Kind == VoiceEngineKind.Windows
            ? SelectedVoice?.Id ?? string.Empty
            : string.Empty,
        NaturalVoiceId = SelectedEngine.Kind == VoiceEngineKind.Natural
            ? SelectedVoice?.Id ?? "af_heart"
            : "af_heart",
        BossDefeatSoundEnabled = BossDefeatSoundEnabled,
        BossDefeatSoundPath = BossDefeatSoundPath
    };

    private void ReloadVoices(string? selectedId)
    {
        Voices.Clear();
        var options = SelectedEngine.Kind == VoiceEngineKind.Natural
            ? NaturalTts.Voices
            : WindowsTts.ListVoices();
        foreach (var option in options)
            Voices.Add(option);
        SelectedVoice = Voices.FirstOrDefault(voice =>
                            voice.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                        ?? Voices.FirstOrDefault();
        RaisePropertyChanged(nameof(ShowDownload));
        RaisePropertyChanged(nameof(CanDownload));
    }

    private static string NaturalVoiceLabel() =>
        NaturalTts.IsDownloaded ? "Natural Voice (downloaded)" : "Natural Voice";
}
