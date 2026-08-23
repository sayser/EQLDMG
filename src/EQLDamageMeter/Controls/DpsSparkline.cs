using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EQLDamageMeter.Controls;

/// <summary>
/// Rolling DPS chart. Optional SecondaryValues draws a stacked pet series in a
/// different color when pet DPS is combined with the owner.
/// SeriesBrush tints the primary (owner) series per combatant.
/// </summary>
public sealed class DpsSparkline : FrameworkElement
{
    private static readonly Brush DefaultLineBrush = CreateFrozenBrush(Color.FromRgb(171, 142, 255));
    private static readonly Brush DefaultFillBrush = CreateFrozenBrush(Color.FromArgb(70, 171, 142, 255));
    private static readonly Brush PetLineBrush = CreateFrozenBrush(Color.FromRgb(47, 216, 199));
    private static readonly Brush PetFillBrush = CreateFrozenBrush(Color.FromArgb(70, 47, 216, 199));
    private static readonly Brush GridBrush = CreateFrozenBrush(Color.FromRgb(42, 70, 94));
    private static readonly Brush AxisBrush = CreateFrozenBrush(Color.FromRgb(120, 90, 110));
    private static readonly Brush LabelBrush = CreateFrozenBrush(Color.FromRgb(150, 170, 185));
    private static readonly Pen DefaultLinePen = CreateFrozenPen(DefaultLineBrush, 1.8);
    private static readonly Pen PetLinePen = CreateFrozenPen(PetLineBrush, 1.8);
    private static readonly Pen GridPen = CreateFrozenPen(GridBrush, 1);
    private static readonly Pen AxisPen = CreateFrozenPen(AxisBrush, 1);
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(DpsSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(
        nameof(SecondaryValues), typeof(IEnumerable), typeof(DpsSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SeriesBrushProperty = DependencyProperty.Register(
        nameof(SeriesBrush), typeof(Brush), typeof(DpsSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IEnumerable? SecondaryValues
    {
        get => (IEnumerable?)GetValue(SecondaryValuesProperty);
        set => SetValue(SecondaryValuesProperty, value);
    }

    public Brush? SeriesBrush
    {
        get => (Brush?)GetValue(SeriesBrushProperty);
        set => SetValue(SeriesBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var owner = CollectValues(Values);
        var pet = CollectValues(SecondaryValues);
        AlignSeries(ref owner, ref pet);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 40) return;

        const double leftPad = 38;
        const double bottomPad = 20;
        const double topPad = 14;
        const double rightPad = 10;
        var plotLeft = leftPad;
        var plotTop = topPad;
        var plotWidth = Math.Max(1, width - leftPad - rightPad);
        var plotHeight = Math.Max(1, height - topPad - bottomPad);
        var plotBottom = plotTop + plotHeight;
        var plotRight = plotLeft + plotWidth;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var (lineBrush, fillBrush, linePen) = ResolveSeriesBrushes();

        drawingContext.DrawLine(AxisPen, new Point(plotLeft, plotBottom), new Point(plotRight, plotBottom));
        drawingContext.DrawLine(GridPen, new Point(plotLeft, plotTop), new Point(plotLeft, plotBottom));
        DrawLabel(drawingContext, dpi, "DPS", 4, plotTop - 12, 9, FontWeights.SemiBold);

        if (owner.Count == 0 && pet.Count == 0)
        {
            DrawLabel(drawingContext, dpi, "0", plotLeft - 4, plotBottom - 6, 9, FontWeights.Normal,
                HorizontalAlignment.Right);
            DrawTimeTick(drawingContext, dpi, plotLeft, plotBottom, 0);
            return;
        }

        var length = Math.Max(owner.Count, pet.Count);
        var stacked = new double[length];
        for (var i = 0; i < length; i++)
        {
            var o = i < owner.Count ? owner[i] : 0;
            var p = i < pet.Count ? pet[i] : 0;
            stacked[i] = o + p;
        }

        var max = stacked.Length == 0 ? 0 : stacked.Max();
        if (max <= 0) max = 1;

        DrawLabel(drawingContext, dpi, FormatDps(max), plotLeft - 4, plotTop - 2, 9, FontWeights.Normal,
            HorizontalAlignment.Right);
        DrawLabel(drawingContext, dpi, "0", plotLeft - 4, plotBottom - 6, 9, FontWeights.Normal,
            HorizontalAlignment.Right);
        var midY = plotTop + plotHeight / 2;
        drawingContext.DrawLine(GridPen, new Point(plotLeft, midY), new Point(plotRight, midY));
        DrawLabel(drawingContext, dpi, FormatDps(max / 2), plotLeft - 4, midY - 6, 8, FontWeights.Normal,
            HorizontalAlignment.Right);

        var durationSeconds = Math.Max(1, length - 1);
        foreach (var (seconds, fraction) in BuildTimeMarks(durationSeconds, plotWidth))
            DrawTimeTick(drawingContext, dpi, plotLeft + fraction * plotWidth, plotBottom, seconds);

        var stepX = length == 1 ? 0 : plotWidth / (length - 1);
        var hasPet = pet.Count > 0 && pet.Any(v => v > 0);

        if (hasPet)
        {
            var petTop = new Point[length];
            var totalTop = new Point[length];
            for (var i = 0; i < length; i++)
            {
                var x = plotLeft + i * stepX;
                var p = i < pet.Count ? pet[i] : 0;
                var o = i < owner.Count ? owner[i] : 0;
                petTop[i] = new Point(x, plotBottom - p / max * plotHeight);
                totalTop[i] = new Point(x, plotBottom - (p + o) / max * plotHeight);
            }

            DrawArea(drawingContext, petTop, plotBottom, PetFillBrush);
            DrawStackedOwnerArea(drawingContext, petTop, totalTop, fillBrush);
            DrawLine(drawingContext, petTop, PetLinePen);
            DrawLine(drawingContext, totalTop, linePen);
        }
        else
        {
            var points = new Point[length];
            for (var i = 0; i < length; i++)
            {
                var x = plotLeft + i * stepX;
                var v = i < owner.Count ? owner[i] : 0;
                points[i] = new Point(x, plotBottom - v / max * plotHeight);
            }

            DrawArea(drawingContext, points, plotBottom, fillBrush);
            DrawLine(drawingContext, points, linePen);
        }
    }

    private (Brush Line, Brush Fill, Pen Pen) ResolveSeriesBrushes()
    {
        if (SeriesBrush is SolidColorBrush solid)
        {
            var c = solid.Color;
            var line = CreateFrozenBrush(c);
            var fill = CreateFrozenBrush(Color.FromArgb(70, c.R, c.G, c.B));
            return (line, fill, CreateFrozenPen(line, 1.8));
        }

        if (SeriesBrush is not null)
            return (SeriesBrush, DefaultFillBrush, CreateFrozenPen(SeriesBrush, 1.8));

        return (DefaultLineBrush, DefaultFillBrush, DefaultLinePen);
    }

    private static void AlignSeries(ref List<double> owner, ref List<double> pet)
    {
        if (owner.Count == 0 && pet.Count == 0) return;
        var length = Math.Max(owner.Count, pet.Count);
        while (owner.Count < length) owner.Add(0);
        while (pet.Count < length) pet.Add(0);
    }

    private static void DrawArea(DrawingContext drawingContext, Point[] top, double plotBottom, Brush fill)
    {
        if (top.Length == 0) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(top[0].X, plotBottom), true, true);
            foreach (var point in top) ctx.LineTo(point, true, false);
            ctx.LineTo(new Point(top[^1].X, plotBottom), true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(fill, null, geometry);
    }

    private static void DrawStackedOwnerArea(DrawingContext drawingContext, Point[] petTop, Point[] totalTop, Brush fill)
    {
        if (totalTop.Length == 0) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(petTop[0], true, true);
            foreach (var point in totalTop) ctx.LineTo(point, true, false);
            for (var i = petTop.Length - 1; i >= 0; i--)
                ctx.LineTo(petTop[i], true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(fill, null, geometry);
    }

    private static void DrawLine(DrawingContext drawingContext, Point[] points, Pen pen)
    {
        if (points.Length == 0) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Length; i++)
                ctx.LineTo(points[i], true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static IReadOnlyList<(int Seconds, double Fraction)> BuildTimeMarks(int durationSeconds, double plotWidth)
    {
        var labelBudget = plotWidth >= 280 ? 4 : plotWidth >= 180 ? 3 : 2;
        labelBudget = Math.Min(labelBudget, durationSeconds + 1);
        if (labelBudget < 2) labelBudget = 2;

        var marks = new List<(int Seconds, double Fraction)>(labelBudget);
        for (var i = 0; i < labelBudget; i++)
        {
            var fraction = labelBudget == 1 ? 0 : i / (double)(labelBudget - 1);
            var seconds = (int)Math.Round(fraction * durationSeconds);
            marks.Add((seconds, fraction));
        }

        marks[0] = (0, 0);
        marks[^1] = (durationSeconds, 1);
        return marks;
    }

    private void DrawTimeTick(DrawingContext drawingContext, double dpi, double x, double plotBottom, int seconds)
    {
        drawingContext.DrawLine(AxisPen, new Point(x, plotBottom), new Point(x, plotBottom + 3));
        DrawLabel(drawingContext, dpi, FormatTime(seconds), x, plotBottom + 4, 9, FontWeights.Normal,
            HorizontalAlignment.Center);
    }

    private static string FormatTime(int totalSeconds)
    {
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:00}";
    }

    private static string FormatDps(double value) =>
        value >= 100
            ? value.ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("0.#", CultureInfo.CurrentCulture);

    private static void DrawLabel(DrawingContext drawingContext, double dpi, string text, double x, double y, double size,
        FontWeight weight, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            size,
            LabelBrush,
            dpi);

        var drawX = align switch
        {
            HorizontalAlignment.Right => x - formatted.Width,
            HorizontalAlignment.Center => x - formatted.Width / 2,
            _ => x
        };
        drawingContext.DrawText(formatted, new Point(drawX, y));
    }

    private static List<double> CollectValues(IEnumerable? source)
    {
        var values = new List<double>();
        if (source is null) return values;
        foreach (var entry in source)
        {
            switch (entry)
            {
                case double d:
                    values.Add(d);
                    break;
                case float f:
                    values.Add(f);
                    break;
                case int i:
                    values.Add(i);
                    break;
                case long l:
                    values.Add(l);
                    break;
            }
        }
        return values;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}
