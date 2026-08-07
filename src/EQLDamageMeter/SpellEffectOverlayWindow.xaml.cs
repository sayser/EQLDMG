using System.Windows;
using System.Windows.Input;

namespace EQLDamageMeter;

public partial class SpellEffectOverlayWindow : Window
{
    public SpellEffectOverlayWindow() => InitializeComponent();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
