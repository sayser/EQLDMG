using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EQLDamageMeter.Controls;

public enum FightLogSegmentStyle
{
    Normal,
    Timestamp,
    Spell,
    Ability,
    Actor
}

public sealed class FightLogSegment
{
    public required string Text { get; init; }
    public Brush Foreground { get; init; } = Brushes.White;
    public bool Bold { get; init; }
    public FightLogSegmentStyle Style { get; init; } = FightLogSegmentStyle.Normal;
}

public sealed class FightLogLineControl : TextBlock
{
    private static readonly FontFamily TimestampFont = new("Consolas, Cascadia Mono, Courier New");
    private static readonly FontFamily SpellFont = new("Georgia, Cambria, Times New Roman, serif");
    private static readonly FontFamily BodyFont = new("Segoe UI");

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable<FightLogSegment>), typeof(FightLogLineControl),
        new PropertyMetadata(null, OnSegmentsChanged));

    public IEnumerable<FightLogSegment>? Segments
    {
        get => (IEnumerable<FightLogSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FightLogLineControl control) return;
        control.Inlines.Clear();
        if (e.NewValue is not IEnumerable<FightLogSegment> segments) return;
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text)
            {
                Foreground = segment.Foreground,
                FontWeight = segment.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = segment.Style switch
                {
                    FightLogSegmentStyle.Timestamp => TimestampFont,
                    FightLogSegmentStyle.Spell => SpellFont,
                    _ => BodyFont
                },
                FontStyle = segment.Style == FightLogSegmentStyle.Spell
                    ? FontStyles.Italic
                    : FontStyles.Normal,
                FontSize = segment.Style switch
                {
                    FightLogSegmentStyle.Spell => control.FontSize + 0.5,
                    FightLogSegmentStyle.Ability => control.FontSize,
                    _ => control.FontSize
                }
            };
            control.Inlines.Add(run);
        }
    }
}
