using System.Windows;
using EQLDamageMeter.Services;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter;

public partial class AlertVolumeSettingsWindow : Window
{
    private readonly AlertVolumeSettingsViewModel _model;
    private readonly BuffAlertService _alerts = new();

    public AlertVolumeSettingsWindow(int currentVolumePercent)
    {
        InitializeComponent();
        _model = new AlertVolumeSettingsViewModel(currentVolumePercent);
        DataContext = _model;
    }

    public int ResultVolumePercent { get; private set; } = 100;

    private void TestAlert_Click(object sender, RoutedEventArgs e) =>
        _alerts.TestVolume(_model.VolumePercent);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResultVolumePercent = _model.VolumePercent;
        DialogResult = true;
        Close();
    }
}

public sealed class AlertVolumeSettingsViewModel : ObservableObject
{
    private int _volumePercent;

    public AlertVolumeSettingsViewModel(int volumePercent)
    {
        _volumePercent = Math.Clamp(volumePercent, 0, 100);
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

    public string VolumeText => $"{VolumePercent}%";
}
