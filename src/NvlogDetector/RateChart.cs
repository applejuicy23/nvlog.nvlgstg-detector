using System.Windows;
using System.Windows.Media;
using NvlogDetector.Core;

namespace NvlogDetector;

public sealed class RateChart : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars),
        typeof(IReadOnlyList<ChartBar>),
        typeof(RateChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartBar>? Bars
    {
        get => (IReadOnlyList<ChartBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var size = RenderSize;
        if (size.Width < 8 || size.Height < 8)
            return;

        var bars = Bars;
        var count = bars?.Count ?? 0;
        if (count == 0)
            return;

        var accent = BrushOf("AccentFillColorDefaultBrush", Color.FromRgb(0x60, 0xCD, 0xFF));
        var alert = BrushOf("SystemFillColorCriticalBrush", Color.FromRgb(0xFF, 0x99, 0xA4));
        var caution = BrushOf("SystemFillColorCautionBrush", Color.FromRgb(0xFC, 0xE1, 0x00));
        var quiet = BrushOf("ControlStrongFillColorDefaultBrush", Color.FromRgb(0x9A, 0x9A, 0x9A));
        var slot = size.Width / count;
        var barWidth = Math.Max(2, slot - 3);
        var plot = size.Height - 4;

        for (var index = 0; index < count; index++)
        {
            var bar = bars![index];
            var height = bar.Fraction <= 0
                ? 2
                : Math.Max(4, bar.Fraction * plot);
            var brush = bar.Fraction <= 0
                ? quiet
                : bar.Level switch
                {
                    EpisodeSeverity.Loop => alert,
                    EpisodeSeverity.Spike => caution,
                    _ => accent,
                };
            var x = index * slot;
            var y = size.Height - height;
            context.DrawRoundedRectangle(brush, null, new Rect(x, y, barWidth, height), 2, 2);
        }
    }

    Brush BrushOf(string key, Color fallback)
    {
        if (TryFindResource(key) is Brush brush)
            return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }
}
