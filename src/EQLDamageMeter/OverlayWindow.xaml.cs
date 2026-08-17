using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EQLDamageMeter.Services;

namespace EQLDamageMeter;

public partial class OverlayWindow : Window
{
    public static readonly DependencyProperty ShowDamageMetricProperty =
        DependencyProperty.Register(nameof(ShowDamageMetric), typeof(bool), typeof(OverlayWindow),
            new PropertyMetadata(true));

    public static readonly DependencyProperty ShowMetricUnitsProperty =
        DependencyProperty.Register(nameof(ShowMetricUnits), typeof(bool), typeof(OverlayWindow),
            new PropertyMetadata(true));

    public OverlayWindow()
    {
        InitializeComponent();
        Tag = OverlayWindowPlacement.DpsKey;
        Loaded += (_, _) =>
        {
            OverlayClickThrough.ApplySaved(this, OverlayWindowPlacement.DpsKey);
            UpdateMetricLayout();
        };
    }

    public bool ShowDamageMetric
    {
        get => (bool)GetValue(ShowDamageMetricProperty);
        set => SetValue(ShowDamageMetricProperty, value);
    }

    public bool ShowMetricUnits
    {
        get => (bool)GetValue(ShowMetricUnitsProperty);
        set => SetValue(ShowMetricUnitsProperty, value);
    }

    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMetricLayout();

    private void UpdateMetricLayout()
    {
        ShowDamageMetric = ActualWidth >= 280;
        ShowMetricUnits = ActualWidth < 280 || ActualWidth >= 390;
    }

    private void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (OverlayClickThrough.IsLocked(this)) return;
        if (e.LeftButton != MouseButtonState.Pressed || IsInteractiveControl(e.OriginalSource as DependencyObject))
            return;

        e.Handled = true;
        DragMove();
    }

    private static bool IsInteractiveControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or ScrollBar or Thumb or ResizeGrip) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
