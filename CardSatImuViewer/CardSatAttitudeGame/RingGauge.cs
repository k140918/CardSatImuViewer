using System.Windows;
using System.Windows.Media;

namespace CardSatAttitudeGame;

/// <summary>
/// 円形ゲージ。真上から時計回りに Value（0〜1）の割合だけ弧を描く。
/// ShowTicks で外周に目盛りを付ける（計器らしい見た目）。
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(FrozenBrush(Color.FromArgb(0x30, 0x3D, 0xE0, 0xFF)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    // 依存関係プロパティの既定値にする Freezable は凍結しておく
    private static SolidColorBrush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTicksProperty = DependencyProperty.Register(
        nameof(ShowTicks), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public bool ShowTicks
    {
        get => (bool)GetValue(ShowTicksProperty);
        set => SetValue(ShowTicksProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double tickSpace = ShowTicks ? 14 : 0;
        double r = size / 2 - Thickness / 2 - tickSpace;
        if (r <= 0)
            return;

        // 目盛り（5 本ごとに長く）
        if (ShowTicks)
        {
            var tickPen = new Pen(TrackBrush, 1.5);
            double outer = size / 2 - 1;
            for (int i = 0; i < 60; i++)
            {
                double len = i % 5 == 0 ? 9 : 4;
                double a = i * 6.0;
                dc.DrawLine(tickPen, PointAt(center, outer, a), PointAt(center, outer - len, a));
            }
        }

        // 下地のリング
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, r, r);

        // 値の弧
        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0.0005)
            return;

        var pen = new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.9995)
        {
            dc.DrawEllipse(null, pen, center, r, r);
            return;
        }

        double sweep = 360 * v;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(PointAt(center, r, 0), false, false);
            ctx.ArcTo(PointAt(center, r, sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>真上を 0° として時計回りに angleDeg の位置</summary>
    private static Point PointAt(Point c, double radius, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180;
        return new Point(c.X + radius * Math.Sin(a), c.Y - radius * Math.Cos(a));
    }
}
