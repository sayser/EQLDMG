using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using EQLDamageMeter.Services;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter;

public partial class MouseHighlightSettingsWindow : Window
{
    private readonly MouseHighlightSettingsViewModel _model;

    public MouseHighlightSettingsWindow(MouseHighlightSettings current)
    {
        InitializeComponent();
        _model = new MouseHighlightSettingsViewModel(current);
        DataContext = _model;
    }

    public MouseHighlightSettings Result { get; private set; } = new();

    private void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MouseHighlightColorOption option })
            _model.SelectColor(option.Hex);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _model.ToSettings();
        DialogResult = true;
        Close();
    }
}

public sealed class MouseHighlightColorOption : ObservableObject
{
    private bool _isSelected;

    public required string Hex { get; init; }
    public required string Name { get; init; }
    public required Brush Swatch { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class MouseHighlightSettingsViewModel : ObservableObject
{
    private static readonly (string Hex, string Name)[] PresetDefinitions =
    [
        ("#FF5522", "Orange"),
        ("#FFE14A", "Yellow"),
        ("#2FD8C7", "Teal"),
        ("#7C5CFC", "Purple"),
        ("#A98BFF", "Lavender"),
        ("#FF6C91", "Pink"),
        ("#7FC6FF", "Sky"),
    ];

    private bool _enabled;
    private string _colorHex;
    private double _diameter;
    private double _thickness;
    private bool _blink;
    private double _blinkHz;
    private bool _secondRing;
    private double _secondDiameter;

    public MouseHighlightSettingsViewModel(MouseHighlightSettings source)
    {
        _enabled = source.Enabled;
        _colorHex = NormalizeHex(string.IsNullOrWhiteSpace(source.ColorHex) ? "#FF5522" : source.ColorHex);
        _diameter = source.Diameter;
        _thickness = source.Thickness;
        _blink = source.Blink;
        _blinkHz = source.BlinkHz <= 0 ? 2.0 : source.BlinkHz;
        _secondRing = source.SecondRing;
        _secondDiameter = source.SecondDiameter <= 0 ? 84 : source.SecondDiameter;

        foreach (var (hex, name) in PresetDefinitions)
        {
            var brush = new SolidColorBrush(ParseColor(hex));
            brush.Freeze();
            ColorPresets.Add(new MouseHighlightColorOption
            {
                Hex = hex,
                Name = name,
                Swatch = brush
            });
        }

        RefreshSelection();
    }

    public ObservableCollection<MouseHighlightColorOption> ColorPresets { get; } = [];

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            var normalized = NormalizeHex(value ?? "#FF5522");
            if (!SetProperty(ref _colorHex, normalized)) return;
            RaisePropertyChanged(nameof(ColorBrush));
            RaisePropertyChanged(nameof(SelectedColorName));
            RefreshSelection();
        }
    }

    public Brush ColorBrush
    {
        get
        {
            var brush = new SolidColorBrush(ParseColor(ColorHex));
            brush.Freeze();
            return brush;
        }
    }

    public string SelectedColorName
    {
        get
        {
            var match = ColorPresets.FirstOrDefault(item =>
                string.Equals(item.Hex, ColorHex, StringComparison.OrdinalIgnoreCase));
            return match?.Name ?? "Custom";
        }
    }

    public double Diameter
    {
        get => _diameter;
        set
        {
            if (!SetProperty(ref _diameter, value)) return;
            RaisePropertyChanged(nameof(DiameterText));
        }
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
            if (!SetProperty(ref _thickness, value)) return;
            RaisePropertyChanged(nameof(ThicknessText));
        }
    }

    public bool Blink
    {
        get => _blink;
        set => SetProperty(ref _blink, value);
    }

    public double BlinkHz
    {
        get => _blinkHz;
        set
        {
            if (!SetProperty(ref _blinkHz, value)) return;
            RaisePropertyChanged(nameof(BlinkHzText));
        }
    }

    public bool SecondRing
    {
        get => _secondRing;
        set => SetProperty(ref _secondRing, value);
    }

    public double SecondDiameter
    {
        get => _secondDiameter;
        set
        {
            if (!SetProperty(ref _secondDiameter, value)) return;
            RaisePropertyChanged(nameof(SecondDiameterText));
        }
    }

    public string DiameterText => $"{Diameter:0} px";
    public string ThicknessText => $"{Thickness:0} px";
    public string BlinkHzText => $"{BlinkHz:0.0}/s";
    public string SecondDiameterText => $"{SecondDiameter:0} px";

    public void SelectColor(string hex) => ColorHex = hex;

    public MouseHighlightSettings ToSettings() => new()
    {
        Enabled = Enabled,
        ColorHex = ColorHex,
        Diameter = Diameter,
        Thickness = Thickness,
        Opacity = 0.85,
        Blink = Blink,
        BlinkHz = BlinkHz,
        SecondRing = SecondRing,
        SecondDiameter = SecondDiameter
    };

    private void RefreshSelection()
    {
        foreach (var option in ColorPresets)
            option.IsSelected = string.Equals(option.Hex, ColorHex, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHex(string value)
    {
        var hex = value.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (hex.Length == 7) return hex.ToUpperInvariant();
        return "#FF5522";
    }

    private static Color ParseColor(string hex) =>
        new MouseHighlightSettings { ColorHex = hex }.ToColor();
}
