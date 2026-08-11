using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EQLDamageMeter.Services;

namespace EQLDamageMeter;

public partial class SpellEffectOverlayWindow : Window
{
    public SpellEffectOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var key = Tag as string ?? "spell";
            OverlayClickThrough.ApplySaved(this, key);
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
}
