using System.Windows;
using System.Windows.Threading;

namespace EQLDamageMeter.Services;

/// <summary>
/// Restores and persists overlay Left/Top/Width/Height in settings.json.
/// </summary>
public static class OverlayWindowPlacement
{
    public const string DpsKey = "dps";
    public const string BuffKey = "buff";
    public const string DotKey = "dot";
    public const string ControlKey = "control";
    public const string HostileKey = "hostile";

    private static readonly Dictionary<string, DispatcherTimer> SaveTimers = new(StringComparer.OrdinalIgnoreCase);

    public static void Attach(Window window, string key)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Owned transparent overlays can be recentered by WPF on Show() unless Manual.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        ApplySavedBounds(window, key);

        void reapply(object? _, EventArgs __) => ApplySavedBounds(window, key);
        window.SourceInitialized += reapply;
        window.Loaded += (_, _) =>
        {
            window.SourceInitialized -= reapply;
            ApplySavedBounds(window, key);
        };

        void scheduleSave(object? _, EventArgs __) => ScheduleSave(window, key);
        window.LocationChanged += scheduleSave;
        window.SizeChanged += scheduleSave;
        window.Closed += (_, _) =>
        {
            window.LocationChanged -= scheduleSave;
            window.SizeChanged -= scheduleSave;
            window.SourceInitialized -= reapply;
            FlushSave(window, key);
        };
    }

    public static void ApplySavedBounds(Window window, string key)
    {
        if (!AppSettingsStore.TryLoadOverlayBounds(key, out var bounds) || bounds is null)
            return;

        var width = Math.Max(window.MinWidth > 0 ? window.MinWidth : 100, bounds.Width);
        var height = Math.Max(window.MinHeight > 0 ? window.MinHeight : 60, bounds.Height);
        var left = bounds.Left;
        var top = bounds.Top;
        ClampToVirtualScreen(ref left, ref top, width, height);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
    }

    private static void ScheduleSave(Window window, string key)
    {
        if (window.WindowState == WindowState.Minimized) return;
        if (double.IsNaN(window.Left) || double.IsNaN(window.Top)) return;

        if (SaveTimers.TryGetValue(key, out var existing))
            existing.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        SaveTimers[key] = timer;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            SaveTimers.Remove(key);
            if (window.WindowState == WindowState.Minimized) return;
            if (double.IsNaN(window.Left) || double.IsNaN(window.Top)) return;
            await AppSettingsStore.TrySaveOverlayBoundsAsync(key, Capture(window));
        };
        timer.Start();
    }

    private static void FlushSave(Window window, string key)
    {
        if (SaveTimers.TryGetValue(key, out var timer))
        {
            timer.Stop();
            SaveTimers.Remove(key);
        }

        try
        {
            if (double.IsNaN(window.Left) || double.IsNaN(window.Top)) return;
            _ = AppSettingsStore.TrySaveOverlayBoundsAsync(key, Capture(window));
        }
        catch (InvalidOperationException)
        {
            // Window may already be torn down.
        }
    }

    private static OverlayBounds Capture(Window window) =>
        new()
        {
            Left = window.Left,
            Top = window.Top,
            Width = window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            Height = window.ActualHeight > 0 ? window.ActualHeight : window.Height
        };

    private static void ClampToVirtualScreen(ref double left, ref double top, double width, double height)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        var screenRight = screenLeft + screenWidth;
        var screenBottom = screenTop + screenHeight;

        // Keep at least 40px of the window visible on the virtual desktop.
        const double margin = 40;
        if (left + width < screenLeft + margin) left = screenLeft;
        if (top + height < screenTop + margin) top = screenTop;
        if (left > screenRight - margin) left = screenRight - Math.Min(width, screenWidth);
        if (top > screenBottom - margin) top = screenBottom - Math.Min(height, screenHeight);
    }
}

public sealed class OverlayBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
