using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CardSatAttitudeGame;

/// <summary>
/// 背景：星空（またたき・ゆっくり流れる）、星雲、画面下の地球の縁と大気の光、右上の太陽光。
/// すべてコードで描くので画像ファイルは不要。
/// </summary>
public sealed class SpaceBackground : Grid
{
    private const int StarCount = 260;

    private readonly Canvas _starLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _earthLayer = new() { IsHitTestVisible = false };
    private readonly List<(Ellipse star, double fx, double fy)> _stars = new();
    private readonly Ellipse _earth = new();
    private readonly Ellipse _atmosphere = new();
    private readonly Ellipse _sun = new();
    private readonly Random _rng = new(20261031);

    public SpaceBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x01, 0x03, 0x0A), 0.0),
                new GradientStop(Color.FromRgb(0x04, 0x0B, 0x1E), 0.6),
                new GradientStop(Color.FromRgb(0x08, 0x18, 0x36), 1.0),
            },
            new Point(0.5, 0), new Point(0.5, 1));

        // 星雲（ぼんやりした色のかたまり）
        Children.Add(Nebula(Color.FromArgb(0x40, 0x5A, 0x2D, 0x9C), 0.18, 0.28, 0.55, 0.45));
        Children.Add(Nebula(Color.FromArgb(0x30, 0x1E, 0x8C, 0xB4), 0.82, 0.22, 0.50, 0.40));

        CreateStars();
        Children.Add(_starLayer);

        // 星全体をゆっくり横に流す
        var drift = new TranslateTransform();
        _starLayer.RenderTransform = drift;
        drift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -40, TimeSpan.FromSeconds(80))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        // 太陽光（右上）
        _sun.Fill = new RadialGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x55, 0xFF, 0xF4, 0xD8), 0.0),
                new GradientStop(Color.FromArgb(0x18, 0xFF, 0xD8, 0x9A), 0.25),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xD8, 0x9A), 1.0),
            });
        _earthLayer.Children.Add(_sun);

        // 地球（大気の光 → 本体の順に重ねる）。円が巨大なのでぼかし効果は使わずグラデーションで光らせる
        _earthLayer.Children.Add(_atmosphere);
        _earthLayer.Children.Add(_earth);
        Children.Add(_earthLayer);

        // 大気の光をゆっくり明滅させる
        _atmosphere.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, 1.0, TimeSpan.FromSeconds(4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

        SizeChanged += (_, _) => LayoutScene();
    }

    private static Ellipse Nebula(Color color, double cx, double cy, double w, double h)
    {
        var e = new Ellipse
        {
            Fill = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Tag = new Rect(cx, cy, w, h),
        };
        return e;
    }

    private void CreateStars()
    {
        for (int i = 0; i < StarCount; i++)
        {
            double size = _rng.NextDouble() switch
            {
                < 0.70 => 1.0 + _rng.NextDouble() * 0.6,
                < 0.95 => 1.8 + _rng.NextDouble() * 0.8,
                _ => 3.0 + _rng.NextDouble() * 1.5,
            };

            Color c = _rng.Next(10) switch
            {
                0 => Color.FromRgb(0xA8, 0xD8, 0xFF), // 青白い星
                1 => Color.FromRgb(0xFF, 0xE6, 0xB8), // 黄色っぽい星
                _ => Colors.White,
            };

            double baseOpacity = 0.35 + _rng.NextDouble() * 0.65;
            var star = new Ellipse
            {
                Width = size,
                Height = size,
                Opacity = baseOpacity,
                Fill = size >= 3.0
                    ? new RadialGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B))
                    : new SolidColorBrush(c),
            };
            if (size >= 3.0)
            {
                star.Width = star.Height = size * 2.2; // 大きい星はにじませる
            }

            // 3 割の星をまたたかせる
            if (_rng.NextDouble() < 0.3)
            {
                star.BeginAnimation(OpacityProperty, new DoubleAnimation(baseOpacity, baseOpacity * 0.15,
                    TimeSpan.FromSeconds(0.8 + _rng.NextDouble() * 2.5))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(_rng.NextDouble() * 3),
                });
            }

            // 横に流れる分だけ、画面より少し広い範囲にまく
            _stars.Add((star, -0.05 + _rng.NextDouble() * 1.1, _rng.NextDouble()));
            _starLayer.Children.Add(star);
        }
    }

    private void LayoutScene()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        foreach (var (star, fx, fy) in _stars)
        {
            Canvas.SetLeft(star, fx * w);
            Canvas.SetTop(star, fy * h);
        }

        foreach (var child in Children.OfType<Ellipse>())
        {
            if (child.Tag is Rect r)
            {
                child.Width = r.Width * w;
                child.Height = r.Height * h;
                child.Margin = new Thickness(r.X * w - child.Width / 2, r.Y * h - child.Height / 2, 0, 0);
            }
        }

        // 地球：上端が画面の高さの 88% あたりに来る大きな円
        double radius = Math.Max(w, h) * 1.25;
        double cx = w * 0.5;
        double top = h * 0.88;
        double cy = top + radius;

        _earth.Width = _earth.Height = radius * 2;
        Canvas.SetLeft(_earth, cx - radius);
        Canvas.SetTop(_earth, cy - radius);
        double band = (h - top) / radius;  // 見えている部分の厚み（半径に対する割合）
        _earth.Fill = new RadialGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x02, 0x06, 0x12), 0.0),
                new GradientStop(Color.FromRgb(0x05, 0x12, 0x2C), 1 - band),
                new GradientStop(Color.FromRgb(0x0C, 0x33, 0x6E), 1 - band * 0.35),
                new GradientStop(Color.FromRgb(0x2A, 0x86, 0xD8), 0.993),
                new GradientStop(Color.FromRgb(0xA6, 0xEC, 0xFF), 1.0),
            });

        double glow = 46;
        double ar = radius + glow;
        _atmosphere.Width = _atmosphere.Height = ar * 2;
        Canvas.SetLeft(_atmosphere, cx - ar);
        Canvas.SetTop(_atmosphere, cy - ar);
        double inner = radius / ar;
        _atmosphere.Fill = new RadialGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0x00, 0x3D, 0xB8, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x00, 0x3D, 0xB8, 0xFF), inner - 0.002),
                new GradientStop(Color.FromArgb(0xB0, 0x5C, 0xC8, 0xFF), inner),
                new GradientStop(Color.FromArgb(0x00, 0x3D, 0xB8, 0xFF), 1.0),
            });

        // 太陽光
        double sr = Math.Max(w, h) * 0.45;
        _sun.Width = _sun.Height = sr * 2;
        Canvas.SetLeft(_sun, w * 0.92 - sr);
        Canvas.SetTop(_sun, h * 0.02 - sr);
    }
}
