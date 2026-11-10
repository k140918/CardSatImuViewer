using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using CardSatImu.Core;

namespace CardSatAttitudeGame;

/// <summary>
/// 基板 1 枚を表示する 3D ビュー。「目標」と「いまの基板」で 1 つずつ使う。
/// 両方とも同じカメラ・同じ色分けにして見比べやすくする。
/// 足元にはゆっくり回る極座標グリッド（光る線）を置き、上下の目安にする。
/// </summary>
public sealed class AttitudeView
{
    private static readonly Vector3D CameraDirection = Normalize(new Vector3D(0, -0.62, -1.0));

    private readonly MatrixTransform3D _boardTransform = new(Matrix3D.Identity);
    private readonly PerspectiveCamera _camera;

    public Viewport3D Viewport { get; }

    public AttitudeView(Color gridColor, double gridSpinSeconds)
    {
        _camera = new PerspectiveCamera
        {
            FieldOfView = 38,
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 5,
            FarPlaneDistance = 5000,
        };
        SetCameraDistance(215);

        Viewport = new Viewport3D
        {
            Camera = _camera,
            ClipToBounds = true,
            IsHitTestVisible = false,
        };

        // 光源
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x70, 0x74, 0x80)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xB8, 0xB8, 0xB8), new Vector3D(-0.4, -1, -0.6)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x30, 0x48, 0x68), new Vector3D(0.6, 0.4, 0.8)));
        Viewport.Children.Add(new ModelVisual3D { Content = lights });

        // 足元のグリッド（WPF 座標の水平面。ゆっくり回す）
        var spin = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var grid = new ModelVisual3D
        {
            Content = CreatePolarGrid(-42, gridColor),
            Transform = new RotateTransform3D(spin),
        };
        Viewport.Children.Add(grid);
        spin.BeginAnimation(AxisAngleRotation3D.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(gridSpinSeconds)) { RepeatBehavior = RepeatBehavior.Forever });

        // 基板（機体座標のモデルを BodyToWpf で置く）
        Viewport.Children.Add(new ModelVisual3D
        {
            Content = BoardModel.CreateBoard(),
            Transform = _boardTransform,
        });
    }

    /// <summary>基準の向きに対する回転 q で基板を置く</summary>
    public void SetOrientation(Quat q) => _boardTransform.Matrix = CoordinateMapping.BodyToWpf(q);

    public void SetCameraDistance(double distance)
    {
        _camera.Position = new Point3D(0, 0, 0) - CameraDirection * distance;
        _camera.LookDirection = CameraDirection * distance;
    }

    /// <summary>同心円と放射線のグリッド（光る細い線）</summary>
    private static Model3DGroup CreatePolarGrid(double y, Color color)
    {
        var mesh = new MeshGeometry3D();
        double[] radii = { 35, 65, 95, 125 };
        const int segments = 96;
        const double halfWidth = 0.45;

        foreach (double r in radii)
        {
            for (int i = 0; i < segments; i++)
            {
                double a0 = 2 * Math.PI * i / segments;
                double a1 = 2 * Math.PI * (i + 1) / segments;
                AddQuad(mesh,
                    P(r - halfWidth, a0, y), P(r + halfWidth, a0, y),
                    P(r + halfWidth, a1, y), P(r - halfWidth, a1, y));
            }
        }

        // 放射線（30° ごと）
        for (int k = 0; k < 12; k++)
        {
            double a = 2 * Math.PI * k / 12;
            var dir = new Vector3D(Math.Cos(a), 0, Math.Sin(a));
            var side = new Vector3D(-Math.Sin(a), 0, Math.Cos(a)) * halfWidth;
            Point3D p0 = new Point3D(0, y, 0) + dir * 20;
            Point3D p1 = new Point3D(0, y, 0) + dir * 125;
            AddQuad(mesh, p0 - side, p1 - side, p1 + side, p0 + side);
        }

        // 外周の目盛り（10° ごとの短い線）
        for (int k = 0; k < 36; k++)
        {
            double a = 2 * Math.PI * k / 36;
            var dir = new Vector3D(Math.Cos(a), 0, Math.Sin(a));
            var side = new Vector3D(-Math.Sin(a), 0, Math.Cos(a)) * halfWidth;
            Point3D p0 = new Point3D(0, y, 0) + dir * 125;
            Point3D p1 = new Point3D(0, y, 0) + dir * (k % 3 == 0 ? 136 : 131);
            AddQuad(mesh, p0 - side, p1 - side, p1 + side, p0 + side);
        }
        mesh.Freeze();

        // 黒の拡散反射 + 発光色。光源の向きに関係なく同じ明るさの線になる
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(Brushes.Black));
        material.Children.Add(new EmissiveMaterial(brush));
        material.Freeze();

        var group = new Model3DGroup();
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        return group;
    }

    private static Point3D P(double r, double a, double y) => new(r * Math.Cos(a), y, r * Math.Sin(a));

    private static void AddQuad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d)
    {
        int n = mesh.Positions.Count;
        mesh.Positions.Add(a);
        mesh.Positions.Add(b);
        mesh.Positions.Add(c);
        mesh.Positions.Add(d);
        mesh.TriangleIndices.Add(n); mesh.TriangleIndices.Add(n + 1); mesh.TriangleIndices.Add(n + 2);
        mesh.TriangleIndices.Add(n); mesh.TriangleIndices.Add(n + 2); mesh.TriangleIndices.Add(n + 3);
    }

    private static Vector3D Normalize(Vector3D v)
    {
        v.Normalize();
        return v;
    }
}
