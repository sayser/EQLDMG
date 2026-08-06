using System.Collections;
using System.Windows;
using System.Windows.Media;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter.Controls;

public sealed class DonutChart : FrameworkElement
{
    private static readonly Brush EmptyRingBrush = CreateFrozenBrush(Color.FromRgb(38, 54, 83));
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
        var items = ItemsSource?.Cast<AbilityViewModel>().Where(item => item.Damage > 0).ToArray() ?? [];
        var total = items.Sum(item => item.Damage);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 8);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = Math.Max(18, radius * 0.28);

        drawingContext.DrawEllipse(null, new Pen(EmptyRingBrush, thickness), center,
            Math.Max(0, radius - thickness / 2), Math.Max(0, radius - thickness / 2));
        if (total <= 0 || radius <= 0) return;

        var start = -90d;
        foreach (var item in items)
        {
            var sweep = item.Damage * 360d / total;
            if (sweep >= 359.999)
            {
                drawingContext.DrawEllipse(null, new Pen(item.Color, thickness), center,
                    radius - thickness / 2, radius - thickness / 2);
            }
            else if (sweep > 0.05)
            {
                var geometry = CreateArc(center, radius - thickness / 2, start, sweep);
                var pen = new Pen(item.Color, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                drawingContext.DrawGeometry(null, pen, geometry);
            }
            start += sweep;
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
