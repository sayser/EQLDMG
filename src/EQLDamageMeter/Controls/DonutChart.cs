using System.Collections;
using System.Windows;
using System.Windows.Media;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter.Controls;

public sealed class DonutChart : FrameworkElement
{
    private static readonly Brush EmptyRingBrush = CreateFrozenBrush(Color.FromRgb(38, 54, 83));
    private static readonly Dictionary<int, Pen> EmptyRingPens = [];
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        long total = 0;
        List<AbilityViewModel>? items = null;
        if (ItemsSource is not null)
        {
            foreach (var entry in ItemsSource)
            {
                if (entry is not AbilityViewModel item || item.Damage <= 0) continue;
                items ??= [];
                items.Add(item);
                total += item.Damage;
            }
        }

        // Keep outer diameter fixed to the control size; only the ring band gets thicker.
        var outerRadius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 6);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = Math.Clamp(outerRadius * 0.46, 12, outerRadius * 0.72);
        var pathRadius = Math.Max(0, outerRadius - thickness / 2);
        var thicknessKey = (int)Math.Round(thickness * 100);

        drawingContext.DrawEllipse(null, GetEmptyRingPen(thicknessKey, thickness), center, pathRadius, pathRadius);
        if (total <= 0 || pathRadius <= 0 || items is null) return;

        var start = -90d;
        foreach (var item in items)
        {
            var sweep = item.Damage * 360d / total;
            if (sweep >= 359.999)
            {
                drawingContext.DrawEllipse(null, new Pen(item.Color, thickness), center, pathRadius, pathRadius);
            }
            else if (sweep > 0.05)
            {
                var geometry = CreateArc(center, pathRadius, start, sweep);
                var pen = new Pen(item.Color, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                drawingContext.DrawGeometry(null, pen, geometry);
            }
            start += sweep;
        }
    }

    private static Pen GetEmptyRingPen(int thicknessKey, double thickness)
    {
        lock (EmptyRingPens)
        {
            if (EmptyRingPens.TryGetValue(thicknessKey, out var cached)) return cached;
            var pen = new Pen(EmptyRingBrush, thickness);
            pen.Freeze();
            EmptyRingPens[thicknessKey] = pen;
            return pen;
        }
    }

    private static PathGeometry CreateArc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var startRadians = startDegrees * Math.PI / 180d;
        var endRadians = (startDegrees + sweepDegrees) * Math.PI / 180d;
        var start = new Point(center.X + radius * Math.Cos(startRadians), center.Y + radius * Math.Sin(startRadians));
        var end = new Point(center.X + radius * Math.Cos(endRadians), center.Y + radius * Math.Sin(endRadians));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, sweepDegrees > 180,
            SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
