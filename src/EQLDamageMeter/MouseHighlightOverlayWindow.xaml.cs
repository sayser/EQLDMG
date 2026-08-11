using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EQLDamageMeter.Services;

namespace EQLDamageMeter;

/// <summary>
/// Click-through fullscreen canvas with rings that track the cursor via composition rendering
/// (avoids moving a small HWND every frame, which lags).
/// </summary>
public sealed class MouseHighlightOverlayWindow : Window
{
    private readonly Canvas _canvas;
    private readonly Ellipse _inner;
    private readonly Ellipse _outer;
    private readonly TranslateTransform _innerTx = new();
    private readonly TranslateTransform _outerTx = new();

    private Color _color = Color.FromRgb(255, 85, 34);
    private double _diameter = 48;
    private double _secondDiameter = 84;
    private double _thickness = 3;
    private double _baseOpacity = 0.85;
    private bool _blink;
    private double _blinkHz = 2.0;
    private bool _secondRing;
    private bool _enabled;
    private bool _rendering;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;
    private double _lastBlinkOpacity = -1;

    public MouseHighlightOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        IsHitTestVisible = false;

        _inner = CreateRing();
        _outer = CreateRing();
        _inner.RenderTransform = _innerTx;
        _outer.RenderTransform = _outerTx;
        _outer.Visibility = Visibility.Collapsed;

        _canvas = new Canvas { IsHitTestVisible = false };
        _canvas.Children.Add(_outer);
        _canvas.Children.Add(_inner);
        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            CoverVirtualScreen();
            ApplyClickThrough();
        };
        Loaded += (_, _) =>
        {
            CoverVirtualScreen();
            ApplyClickThrough();
            SetRendering(true);
            FollowCursor(force: true);
        };
        Closed += (_, _) => SetRendering(false);
    }

    public void ApplyOptions(MouseHighlightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _enabled = settings.Enabled;
        _color = settings.ToColor();
        _diameter = Math.Clamp(settings.Diameter, 16, 200);
        _secondDiameter = Math.Clamp(settings.SecondDiameter, 20, 260);
        _thickness = Math.Clamp(settings.Thickness, 1, 16);
        _baseOpacity = Math.Clamp(settings.Opacity <= 0 ? 0.85 : settings.Opacity, 0.15, 1.0);
        _blink = settings.Blink;
        _blinkHz = Math.Clamp(settings.BlinkHz <= 0 ? 2.0 : settings.BlinkHz, 0.5, 8.0);
        _secondRing = settings.SecondRing;
        ApplyAppearance();

        if (_enabled)
        {
            if (!IsVisible) Show();
            CoverVirtualScreen();
            ApplyClickThrough();
            SetRendering(true);
            FollowCursor(force: true);
        }
        else
        {
            SetRendering(false);
            if (IsVisible) Hide();
        }
    }

    private static Ellipse CreateRing() => new()
    {
        Fill = Brushes.Transparent,
        IsHitTestVisible = false,
        SnapsToDevicePixels = false
    };

    private void ApplyAppearance()
    {
        var brush = new SolidColorBrush(_color);
        brush.Freeze();

        ConfigureRing(_inner, _diameter, brush);
        ConfigureRing(_outer, _secondDiameter, brush);
        _outer.Visibility = _secondRing ? Visibility.Visible : Visibility.Collapsed;
        ApplyOpacity(_baseOpacity);
    }

    private void ConfigureRing(Ellipse ring, double diameter, Brush brush)
    {
        ring.Width = diameter;
        ring.Height = diameter;
        ring.StrokeThickness = _thickness;
        ring.Stroke = brush;
    }

    private void ApplyOpacity(double opacity)
    {
        _inner.Opacity = opacity;
        _outer.Opacity = opacity * 0.75;
        _lastBlinkOpacity = opacity;
    }

    private void SetRendering(bool on)
    {
        if (on == _rendering) return;
        if (on)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
        _rendering = on;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_enabled || !IsVisible) return;
        FollowCursor(force: false);
        if (_blink)
            UpdateBlink();
        else if (_lastBlinkOpacity != _baseOpacity)
            ApplyOpacity(_baseOpacity);
    }

    private void UpdateBlink()
    {
        var hz = _blinkHz;
        var phase = (Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) * hz;
        // Soft blink: 15% ↔ 100% of base opacity
        var wave = 0.5 + 0.5 * Math.Sin(phase * Math.PI * 2.0);
        var opacity = _baseOpacity * (0.15 + 0.85 * wave);
        if (Math.Abs(opacity - _lastBlinkOpacity) < 0.01) return;
        ApplyOpacity(opacity);
    }

    private void FollowCursor(bool force)
    {
        if (!GetCursorPos(out var pt)) return;
        if (!force && pt.X == _lastCursorX && pt.Y == _lastCursorY) return;
        _lastCursorX = pt.X;
        _lastCursorY = pt.Y;

        var screen = new Point(pt.X, pt.Y);
        if (PresentationSource.FromVisual(this) is { CompositionTarget: { } target })
            screen = target.TransformFromDevice.Transform(screen);

        var x = screen.X - Left;
        var y = screen.Y - Top;

        _innerTx.X = x - _diameter / 2.0;
        _innerTx.Y = y - _diameter / 2.0;
        if (_secondRing)
        {
            _outerTx.X = x - _secondDiameter / 2.0;
            _outerTx.Y = y - _secondDiameter / 2.0;
        }
    }

    private void CoverVirtualScreen()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const int gwlExStyle = -20;
        const int wsExTransparent = 0x00000020;
        const int wsExLayered = 0x00080000;
        const int wsExToolWindow = 0x00000080;
        var style = GetWindowLong(hwnd, gwlExStyle);
        SetWindowLong(hwnd, gwlExStyle, style | wsExTransparent | wsExLayered | wsExToolWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
