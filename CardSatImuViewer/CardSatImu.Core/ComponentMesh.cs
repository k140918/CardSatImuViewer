using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CardSatImu.Core;

/// <summary>
/// 部品の 3D モデル（Assets/components.bin.gz）を読み込む。
/// KiCad 10 で書き出した CardkSat_V002.glb から、基板本体と太陽電池を除いた部品だけを
/// 機体座標 [mm] に変換し、色ごとにまとめたもの（tools/convert_glb.py で作成）。
///
/// 形式（リトルエンディアン、gzip 圧縮）
///   "CSM1"、int グループ数
///   グループごとに：byte R, G, B, A、int 頂点数、int 添字数、
///                   float[頂点数×3] 位置、float[頂点数×3] 法線、int[添字数] 三角形の頂点番号
/// </summary>
internal static class ComponentMesh
{
    private const string ResourceUri = "pack://application:,,,/CardSatImu.Core;component/Assets/components.bin.gz";

    // 読み込みは 1 回だけ。凍結した同じモデルを 2 つの画面で共有する
    private static readonly Lazy<Model3DGroup?> Cached = new(Load);

    /// <summary>部品のモデル。読めなかったときは null</summary>
    public static Model3DGroup? Model => Cached.Value;

    private static Model3DGroup? Load()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri(ResourceUri, UriKind.Absolute));
            if (info is null)
                return null;

            using var gz = new GZipStream(info.Stream, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);

            byte[] magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'C' || magic[1] != 'S' || magic[2] != 'M' || magic[3] != '1')
                return null;

            int groupCount = r.ReadInt32();
            var group = new Model3DGroup();
            for (int g = 0; g < groupCount; g++)
            {
                byte cr = r.ReadByte(), cg = r.ReadByte(), cb = r.ReadByte();
                r.ReadByte(); // A（未使用）
                int vertexCount = r.ReadInt32();
                int indexCount = r.ReadInt32();

                var positions = new Point3DCollection(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                    positions.Add(new Point3D(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));

                var normals = new Vector3DCollection(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                    normals.Add(new Vector3D(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));

                var indices = new Int32Collection(indexCount);
                for (int i = 0; i < indexCount; i++)
                    indices.Add(r.ReadInt32());

                var mesh = new MeshGeometry3D
                {
                    Positions = positions,
                    Normals = normals,
                    TriangleIndices = indices,
                };
                mesh.Freeze();

                var color = Color.FromRgb(cr, cg, cb);
                var front = CreateMaterial(color, out var back);
                var model = new GeometryModel3D(mesh, front) { BackMaterial = back };
                model.Freeze();
                group.Children.Add(model);
            }

            group.Freeze();
            return group;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 色から材質を作る。明るく彩度の低い色（ピン・シールドなどの金属）には少しつやを付ける。
    /// GLB の材質は両面表示なので、裏側にも同じ色を付ける。
    /// </summary>
    private static Material CreateMaterial(Color c, out Material back)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        var diffuse = new DiffuseMaterial(brush);
        diffuse.Freeze();
        back = diffuse;

        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        bool metallic = max >= 150 && max - min <= 40;
        if (!metallic)
            return diffuse;

        var shine = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        shine.Freeze();
        var group = new MaterialGroup();
        group.Children.Add(diffuse);
        group.Children.Add(new SpecularMaterial(shine, 40));
        group.Freeze();
        return group;
    }
}
