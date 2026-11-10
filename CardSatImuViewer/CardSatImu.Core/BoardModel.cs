using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace CardSatImu.Core;

/// <summary>基板モデルの見た目</summary>
public enum BoardStyle
{
    /// <summary>Realistic に、KiCad の 3D モデル（IC・コネクタ・受動部品など）を載せたもの</summary>
    Detailed,

    /// <summary>実物に近い見た目（Gerber から作った表裏の画像・実際の外形・USB-C・太陽電池）</summary>
    Realistic,

    /// <summary>単純な箱＋色帯＋文字ラベル（画像が読めないときもこちらになる）</summary>
    Simple,
}

/// <summary>
/// 3D モデルの生成。基板と軸矢印は「機体座標（mm）」で作り、
/// 表示時に CoordinateMapping.BodyToWpf で WPF 座標へ写す。
///
/// 機体座標（右手系）
///   +X：B 列の長辺の方向（部品面から見て下）
///   +Y：右の短辺（ESP32-S3 側）の方向
///   +Z：部品面の外向き
/// 基板の中心（KiCad の (136.2, 122.41)）を原点とする。
/// KiCad 座標との関係：x = KiCadY − 122.41、y = KiCadX − 136.2
/// </summary>
public static class BoardModel
{
    // 基板寸法 85.6 × 54 × 1.6 mm（長辺が Y、短辺が X）
    public const double HalfX = 54.0 / 2;    // A 列(-X) 〜 B 列(+X)
    public const double HalfY = 85.6 / 2;    // USB-C 側(-Y) 〜 ESP32-S3 側(+Y)
    public const double HalfZ = 1.6 / 2;     // 裏面(-Z) 〜 部品面(+Z)

    private const double BandWidth = 3.0;    // 縁の色帯の幅（Simple のみ）
    private const double BandThickness = 0.1;

    // ---- Simple 用の色 ----
    public static readonly Color TopColor = Color.FromRgb(0x2E, 0x8B, 0x3E);   // 部品面
    public static readonly Color BottomColor = Color.FromRgb(0x14, 0x3D, 0x1C); // 裏面
    public static readonly Color SideColor = Color.FromRgb(0x1E, 0x5E, 0x2A);   // ESP32-S3 側の辺

    // ---- Realistic 用の色（凡例にも使う） ----
    public static readonly Color RealTopColor = Color.FromRgb(0xF4, 0xF4, 0xEF);    // 白レジスト
    public static readonly Color RealBottomColor = Color.FromRgb(0x1A, 0x22, 0x3E); // 太陽電池
    public static readonly Color RealSideColor = Color.FromRgb(0xDC, 0xD3, 0xB4);   // 基板の断面（FR4）

    // ---- 共通の色 ----
    public static readonly Color EdgeAColor = Color.FromRgb(0xF2, 0xC2, 0x1B);  // A 列：黄
    public static readonly Color EdgeBColor = Color.FromRgb(0xD8, 0x3C, 0xC8);  // B 列：マゼンタ
    public static readonly Color EdgeUsbColor = Color.FromRgb(0x9A, 0x9A, 0x9A); // USB-C 側：灰
    public static readonly Color UsbConnectorColor = Color.FromRgb(0xC8, 0xC8, 0xD0);
    public static readonly Color AxisXColor = Color.FromRgb(0xE5, 0x39, 0x35);  // 赤
    public static readonly Color AxisYColor = Color.FromRgb(0x43, 0xA0, 0x47);  // 緑
    public static readonly Color AxisZColor = Color.FromRgb(0x1E, 0x88, 0xE5);  // 青

    // ---- 実際の部品位置（KiCad の CardkSat_V002.kicad_pcb から） ----
    // USB-C（J1, J2）：裏面・左の短辺。中心の X 座標
    private static readonly double[] UsbCenterX = { -12.5, 12.5 };
    // 太陽電池（CEL2, CEL1）：裏面。KiCad の 3D モデル（SM141K09L）の外形に合わせた X 範囲・Y 範囲・厚さ
    private static readonly (double x0, double x1)[] SolarRangeX = { (-23.1, -0.1), (0.1, 23.1) };
    private const double SolarY0 = -29.6, SolarY1 = 33.4;
    private const double SolarThickness = 2.3;

    /// <summary>
    /// 基板・部品・機体軸の矢印などをまとめたモデル（機体座標）。
    /// 必要な画像や部品データが読めないときは、Detailed → Realistic → Simple の順に簡単な見た目へ切り替える。
    /// </summary>
    public static Model3DGroup CreateBoard(BoardStyle style = BoardStyle.Detailed)
    {
        if (style != BoardStyle.Simple && Textures.Value is { } tex)
        {
            Model3DGroup? parts = style == BoardStyle.Detailed ? ComponentMesh.Model : null;
            return CreateRealisticBoard(tex, parts);
        }
        return CreateSimpleBoard();
    }

    // ===================================================================
    // Realistic
    // ===================================================================

    private sealed record BoardTextures(BitmapSource Top, BitmapSource Bottom, BitmapSource Solar);

    // 2 つの画面で同じ画像を使い回す
    private static readonly Lazy<BoardTextures?> Textures = new(LoadTextures);

    private static BoardTextures? LoadTextures()
    {
        var top = LoadPng("board_top.png");
        var bottom = LoadPng("board_bottom.png");
        var solar = LoadPng("solar_cell.png");
        return top is null || bottom is null || solar is null ? null : new BoardTextures(top, bottom, solar);
    }

    private static BitmapSource? LoadPng(string name)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/CardSatImu.Core;component/Assets/{name}", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <param name="parts">KiCad の 3D モデルから作った部品（null なら USB-C だけ箱で置く）</param>
    private static Model3DGroup CreateRealisticBoard(BoardTextures tex, Model3DGroup? parts)
    {
        var group = new Model3DGroup();
        double[] xs = BoardOutline.X, ys = BoardOutline.Y;
        int[] tris = BoardOutline.Triangles;
        int n = xs.Length;

        // --- 部品面と裏面：外形を三角形に分けて、Gerber から作った画像を貼る ---
        // 画像は「部品面から見た向き」：左＝USB-C 側(-Y)、上＝A 列(-X)
        //   u = (y + HalfY) / 85.6、v = (x + HalfX) / 54
        var top = new MeshGeometry3D();
        var bottom = new MeshGeometry3D();
        for (int i = 0; i < n; i++)
        {
            var uv = new Point((ys[i] + HalfY) / (2 * HalfY), (xs[i] + HalfX) / (2 * HalfX));
            top.Positions.Add(new Point3D(xs[i], ys[i], HalfZ));
            top.TextureCoordinates.Add(uv);
            bottom.Positions.Add(new Point3D(xs[i], ys[i], -HalfZ));
            bottom.TextureCoordinates.Add(uv);
        }
        for (int t = 0; t < tris.Length; t += 3)
        {
            // 外形は +Z から見て反時計回り。裏面は逆順にして -Z 向きにする
            top.TriangleIndices.Add(tris[t]);
            top.TriangleIndices.Add(tris[t + 1]);
            top.TriangleIndices.Add(tris[t + 2]);
            bottom.TriangleIndices.Add(tris[t]);
            bottom.TriangleIndices.Add(tris[t + 2]);
            bottom.TriangleIndices.Add(tris[t + 1]);
        }
        group.Children.Add(TexturedModel(top, tex.Top));
        group.Children.Add(TexturedModel(bottom, tex.Bottom));

        // --- 側面：A 列・B 列・USB-C 側の辺だけ色を付ける（見比べやすさのため） ---
        var sideA = new MeshGeometry3D();
        var sideB = new MeshGeometry3D();
        var sideUsb = new MeshGeometry3D();
        var sideOther = new MeshGeometry3D();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            const double eps = 1e-3;
            MeshGeometry3D target =
                Math.Abs(xs[i] + HalfX) < eps && Math.Abs(xs[j] + HalfX) < eps ? sideA :
                Math.Abs(xs[i] - HalfX) < eps && Math.Abs(xs[j] - HalfX) < eps ? sideB :
                Math.Abs(ys[i] + HalfY) < eps && Math.Abs(ys[j] + HalfY) < eps ? sideUsb :
                sideOther;
            // 外形が反時計回りなので、下→下→上→上 の順で外向きの面になる
            AddQuad(target,
                new Point3D(xs[i], ys[i], -HalfZ), new Point3D(xs[j], ys[j], -HalfZ),
                new Point3D(xs[j], ys[j], HalfZ), new Point3D(xs[i], ys[i], HalfZ));
        }
        group.Children.Add(Model(sideA, EdgeAColor));
        group.Children.Add(Model(sideB, EdgeBColor));
        group.Children.Add(Model(sideUsb, EdgeUsbColor));
        group.Children.Add(Model(sideOther, RealSideColor));

        if (parts is not null)
        {
            // --- KiCad の 3D モデル（USB-C も含む。太陽電池だけは下の画像付きの板を使う） ---
            group.Children.Add(parts);
        }
        else
        {
            // --- USB-C（裏面・左の短辺に 2 個） ---
            foreach (double cx in UsbCenterX)
                AddUsbC(group, cx);
        }

        // --- 太陽電池（裏面に 2 枚） ---
        foreach (var (x0, x1) in SolarRangeX)
            AddSolarCell(group, x0, x1, tex.Solar);

        AddAxes(group);
        return group;
    }

    /// <summary>USB-C レセプタクル（幅 8.94 × 奥行 7.35 × 高さ 3.2 mm、端から 0.2 mm はみ出す）</summary>
    private static void AddUsbC(Model3DGroup group, double cx)
    {
        const double halfW = 4.47, depth = 7.35, height = 3.2, overhang = 0.2;
        double y0 = -HalfY - overhang;
        var shell = new MeshGeometry3D();
        AddBox(shell, new Point3D(cx - halfW, y0, -HalfZ - height), new Point3D(cx + halfW, y0 + depth, -HalfZ));
        group.Children.Add(Model(shell, UsbConnectorColor));

        // 差し込み口（端面の暗い部分）
        var port = new MeshGeometry3D();
        AddBox(port,
            new Point3D(cx - halfW + 0.6, y0 - 0.05, -HalfZ - height + 0.55),
            new Point3D(cx + halfW - 0.6, y0 + 0.3, -HalfZ - 0.55));
        group.Children.Add(Model(port, Color.FromRgb(0x20, 0x20, 0x24)));
    }

    /// <summary>太陽電池。外向きの面（-Z）にセルの画像を貼る</summary>
    private static void AddSolarCell(Model3DGroup group, double x0, double x1, BitmapSource texture)
    {
        double z1 = -HalfZ, z0 = -HalfZ - SolarThickness;
        var min = new Point3D(x0, SolarY0, z0);
        var max = new Point3D(x1, SolarY1, z1);

        // 側面（基板に接する +Z 面は見えないので作らない）
        var sides = new MeshGeometry3D();
        foreach (var f in new[] { BoxFace.PosX, BoxFace.NegX, BoxFace.PosY, BoxFace.NegY })
            AddFace(sides, min, max, f);
        group.Children.Add(Model(sides, Color.FromRgb(0x2A, 0x2E, 0x38)));

        // 外向きの面：画像の横方向を機体 Y、縦方向を機体 X に合わせる
        var face = new MeshGeometry3D();
        face.Positions.Add(new Point3D(x0, SolarY0, z0));
        face.Positions.Add(new Point3D(x0, SolarY1, z0));
        face.Positions.Add(new Point3D(x1, SolarY1, z0));
        face.Positions.Add(new Point3D(x1, SolarY0, z0));
        face.TextureCoordinates.Add(new Point(0, 0));
        face.TextureCoordinates.Add(new Point(1, 0));
        face.TextureCoordinates.Add(new Point(1, 1));
        face.TextureCoordinates.Add(new Point(0, 1));
        // (x0,Y0)→(x0,Y1)→(x1,Y1)：(+Y)×(+X+Y) = -Z 向き
        face.TriangleIndices.Add(0); face.TriangleIndices.Add(1); face.TriangleIndices.Add(2);
        face.TriangleIndices.Add(0); face.TriangleIndices.Add(2); face.TriangleIndices.Add(3);
        group.Children.Add(TexturedModel(face, texture));
    }

    private static GeometryModel3D TexturedModel(MeshGeometry3D mesh, BitmapSource image)
    {
        mesh.Freeze();
        var brush = new ImageBrush(image)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        var model = new GeometryModel3D(mesh, material);
        model.Freeze();
        return model;
    }

    private static void AddAxes(Model3DGroup group)
    {
        group.Children.Add(Arrow(new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), new Vector3D(0, 0, 1), 50, AxisXColor));
        group.Children.Add(Arrow(new Vector3D(0, 1, 0), new Vector3D(0, 0, 1), new Vector3D(1, 0, 0), 66, AxisYColor));
        group.Children.Add(Arrow(new Vector3D(0, 0, 1), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0), 40, AxisZColor));
    }

    // ===================================================================
    // Simple（以前の見た目）
    // ===================================================================

    private static Model3DGroup CreateSimpleBoard()
    {
        var group = new Model3DGroup();

        // --- 基板本体：面ごとに色を変える ---
        var min = new Point3D(-HalfX, -HalfY, -HalfZ);
        var max = new Point3D(HalfX, HalfY, HalfZ);
        group.Children.Add(Face(min, max, BoxFace.PosZ, TopColor));
        group.Children.Add(Face(min, max, BoxFace.NegZ, BottomColor));
        group.Children.Add(Face(min, max, BoxFace.NegX, EdgeAColor));   // A 列の辺
        group.Children.Add(Face(min, max, BoxFace.PosX, EdgeBColor));   // B 列の辺
        group.Children.Add(Face(min, max, BoxFace.NegY, EdgeUsbColor)); // USB-C 側の辺
        group.Children.Add(Face(min, max, BoxFace.PosY, SideColor));    // ESP32-S3 側の辺

        // --- 縁の色帯（厚み 1.6 mm の側面だけでは見えにくいので、表裏の縁にも色を付ける） ---
        foreach (double zSign in new[] { +1.0, -1.0 })
        {
            double z0 = zSign > 0 ? HalfZ : -HalfZ - BandThickness;
            double z1 = zSign > 0 ? HalfZ + BandThickness : -HalfZ;

            group.Children.Add(Box(new Point3D(-HalfX, -HalfY, z0), new Point3D(-HalfX + BandWidth, HalfY, z1), EdgeAColor));
            group.Children.Add(Box(new Point3D(HalfX - BandWidth, -HalfY, z0), new Point3D(HalfX, HalfY, z1), EdgeBColor));
            group.Children.Add(Box(new Point3D(-HalfX + BandWidth, -HalfY, z0), new Point3D(HalfX - BandWidth, -HalfY + BandWidth, z1), EdgeUsbColor));
        }

        // --- USB-C（実際の位置） ---
        foreach (double cx in UsbCenterX)
            AddUsbC(group, cx);

        AddAxes(group);

        // --- 部品面のラベル（透過を含むので最後に追加する） ---
        double labelZ = HalfZ + BandThickness + 0.05;
        group.Children.Add(TopLabel("A 列", -HalfX + 9, 0, 9, 26, labelZ));
        group.Children.Add(TopLabel("B 列", HalfX - 9, 0, 9, 26, labelZ));
        group.Children.Add(TopLabel("USB-C", 0, -HalfY + 12, 7, 20, labelZ));
        group.Children.Add(TopLabel("ESP32-S3", 0, HalfY - 14, 6, 24, labelZ));

        return group;
    }

    /// <summary>地上の目安になる水平グリッド（WPF 座標で直接作る。回転しない）</summary>
    public static Model3DGroup CreateGroundGrid(double y = -45, double halfSize = 150, double step = 25, Color? color = null)
    {
        var mesh = new MeshGeometry3D();
        const double w = 0.25;
        for (double v = -halfSize; v <= halfSize + 1e-9; v += step)
        {
            // WPF 座標：X 右、Y 上、Z 手前。水平面は XZ 平面
            AddBox(mesh, new Point3D(v - w, y - w, -halfSize), new Point3D(v + w, y + w, halfSize));
            AddBox(mesh, new Point3D(-halfSize, y - w, v - w), new Point3D(halfSize, y + w, v + w));
        }
        var group = new Model3DGroup();
        group.Children.Add(Model(mesh, color ?? Color.FromRgb(0x5A, 0x63, 0x6E)));
        return group;
    }

    // ===================================================================
    // 以下、メッシュ生成の補助
    // ===================================================================

    private enum BoxFace { PosX, NegX, PosY, NegY, PosZ, NegZ }

    /// <summary>
    /// 直方体の頂点番号：i = (x が max なら 1) | (y が max なら 2) | (z が max なら 4)。
    /// 各面は外側から見て反時計回り（(b-a)×(c-a) が外向き法線）になる順に並べてある。
    /// </summary>
    private static readonly Dictionary<BoxFace, int[]> FaceIndices = new()
    {
        [BoxFace.PosZ] = new[] { 4, 5, 7, 6 },
        [BoxFace.NegZ] = new[] { 0, 2, 3, 1 },
        [BoxFace.PosX] = new[] { 1, 3, 7, 5 },
        [BoxFace.NegX] = new[] { 0, 4, 6, 2 },
        [BoxFace.PosY] = new[] { 2, 6, 7, 3 },
        [BoxFace.NegY] = new[] { 0, 1, 5, 4 },
    };

    private static Point3D Corner(Point3D min, Point3D max, int i) => new(
        (i & 1) != 0 ? max.X : min.X,
        (i & 2) != 0 ? max.Y : min.Y,
        (i & 4) != 0 ? max.Z : min.Z);

    private static void AddFace(MeshGeometry3D mesh, Point3D min, Point3D max, BoxFace face)
    {
        int[] idx = FaceIndices[face];
        AddQuad(mesh,
            Corner(min, max, idx[0]), Corner(min, max, idx[1]),
            Corner(min, max, idx[2]), Corner(min, max, idx[3]));
    }

    private static void AddBox(MeshGeometry3D mesh, Point3D min, Point3D max)
    {
        foreach (BoxFace f in Enum.GetValues<BoxFace>())
            AddFace(mesh, min, max, f);
    }

    /// <summary>a→b→c→d を反時計回りとする四角形（三角形 abc と acd）</summary>
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

    private static GeometryModel3D Face(Point3D min, Point3D max, BoxFace face, Color color)
    {
        var mesh = new MeshGeometry3D();
        AddFace(mesh, min, max, face);
        return Model(mesh, color);
    }

    private static GeometryModel3D Box(Point3D min, Point3D max, Color color)
    {
        var mesh = new MeshGeometry3D();
        AddBox(mesh, min, max);
        return Model(mesh, color);
    }

    private static GeometryModel3D Model(MeshGeometry3D mesh, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        mesh.Freeze();
        // 裏面も同じ色で描く（巻き順の取り違えがあっても消えないように）
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    /// <summary>
    /// 原点から axis 方向へ伸びる矢印（円柱＋円錐）。u, v は axis に垂直で u×v = axis となる単位ベクトル。
    /// </summary>
    private static GeometryModel3D Arrow(Vector3D axis, Vector3D u, Vector3D v, double length, Color color)
    {
        const int segments = 20;
        const double shaftRadius = 0.9;
        const double headRadius = 2.6;
        const double headLength = 8.0;
        double shaftLength = length - headLength;

        var mesh = new MeshGeometry3D();
        var origin = new Point3D(0, 0, 0);
        Point3D tip = origin + axis * length;

        for (int i = 0; i < segments; i++)
        {
            double t0 = 2 * Math.PI * i / segments;
            double t1 = 2 * Math.PI * (i + 1) / segments;
            Vector3D r0 = u * Math.Cos(t0) + v * Math.Sin(t0);
            Vector3D r1 = u * Math.Cos(t1) + v * Math.Sin(t1);

            // 軸（円柱の側面）
            Point3D a = origin + r0 * shaftRadius;
            Point3D b = origin + r1 * shaftRadius;
            Point3D c = origin + axis * shaftLength + r1 * shaftRadius;
            Point3D d = origin + axis * shaftLength + r0 * shaftRadius;
            AddQuad(mesh, a, b, c, d);

            // 矢じり（円錐の側面）
            Point3D h0 = origin + axis * shaftLength + r0 * headRadius;
            Point3D h1 = origin + axis * shaftLength + r1 * headRadius;
            AddTriangle(mesh, h0, h1, tip);

            // 矢じりの底面
            Point3D hc = origin + axis * shaftLength;
            AddTriangle(mesh, hc, h1, h0);
        }

        return Model(mesh, color);
    }

    private static void AddTriangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
    {
        int n = mesh.Positions.Count;
        mesh.Positions.Add(a);
        mesh.Positions.Add(b);
        mesh.Positions.Add(c);
        mesh.TriangleIndices.Add(n);
        mesh.TriangleIndices.Add(n + 1);
        mesh.TriangleIndices.Add(n + 2);
    }

    /// <summary>
    /// 部品面に貼る文字ラベル。基準姿勢（A 列が奥、USB-C が左）で上から見て正しく読める向きにする。
    /// 画面の右 = 機体 +Y、画面の上 = 機体 -X（A 列側）。
    /// </summary>
    private static GeometryModel3D TopLabel(string text, double cx, double cy, double height, double width, double z)
    {
        double hx = height / 2, hy = width / 2;
        var mesh = new MeshGeometry3D();
        // 左上・左下・右下・右上（+Z から見て反時計回り）
        mesh.Positions.Add(new Point3D(cx - hx, cy - hy, z));
        mesh.Positions.Add(new Point3D(cx + hx, cy - hy, z));
        mesh.Positions.Add(new Point3D(cx + hx, cy + hy, z));
        mesh.Positions.Add(new Point3D(cx - hx, cy + hy, z));
        // テクスチャ座標は mm 単位にして、縦横比を四角形と一致させる
        // （ブラシの Stretch.Uniform はテクスチャ座標の外接矩形を基準に計算されるため）
        mesh.TextureCoordinates.Add(new Point(0, 0));
        mesh.TextureCoordinates.Add(new Point(0, height));
        mesh.TextureCoordinates.Add(new Point(width, height));
        mesh.TextureCoordinates.Add(new Point(width, 0));
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
        mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(2); mesh.TriangleIndices.Add(3);
        mesh.Freeze();

        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            48,
            Brushes.White,
            1.0);
        Geometry glyphs = formatted.BuildGeometry(new Point(0, 0));

        var drawing = new GeometryDrawing(Brushes.White, null, glyphs);
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Uniform };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();

        var model = new GeometryModel3D(mesh, material);
        model.Freeze();
        return model;
    }
}
