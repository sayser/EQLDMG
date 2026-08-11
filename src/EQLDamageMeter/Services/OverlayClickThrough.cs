using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EQLDamageMeter.Services;

/// <summary>
/// Makes an overlay click-through (WS_EX_TRANSPARENT) while locked so clicks reach the game.
/// Lock state is controlled from the main app checkboxes next to each overlay button.
/// </summary>
public static class OverlayClickThrough
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private static readonly HashSet<Window> LockedWindows = [];

    public static bool IsLocked(Window window) => LockedWindows.Contains(window);

    public static void SetLocked(Window window, bool locked)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (locked) LockedWindows.Add(window);
        else LockedWindows.Remove(window);
        Apply(window, locked);
        _ = AppSettingsStore.TrySaveOverlayLockedAsync(GetKey(window), locked);
    }

    public static void ApplySaved(Window window, string key)
    {
        var locked = AppSettingsStore.TryLoadOverlayLocked(key);
        if (locked) LockedWindows.Add(window);
        else LockedWindows.Remove(window);
        window.SourceInitialized += (_, _) => Apply(window, locked);
        if (window.IsLoaded) Apply(window, locked);
    }

    private static string GetKey(Window window) =>
        window.Tag as string ?? window.GetType().Name;

    private static void Apply(Window window, bool clickThrough)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GwlExStyle);
        if (clickThrough)
            style |= WsExTransparent | WsExLayered;
        else
            style &= ~WsExTransparent;

        SetWindowLong(hwnd, GwlExStyle, style);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
