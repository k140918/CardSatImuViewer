using System.Windows.Media.Media3D;

namespace CardSatImu.Core;

/// <summary>
/// ハミルトン形式のクォータニオン q = W + Xi + Yj + Zk。
/// WPF の System.Windows.Media.Media3D.Quaternion は掛け算の順序や回転の向きが
/// 分かりにくいので使わず、ここで明示的に計算する。
/// </summary>
public readonly record struct Quat(double W, double X, double Y, double Z)
{
    public static readonly Quat Identity = new(1, 0, 0, 0);

    public double Norm => Math.Sqrt(W * W + X * X + Y * Y + Z * Z);

    public Quat Conjugate() => new(W, -X, -Y, -Z);

    public Quat Normalized()
    {
        double n = Norm;
        return n < 1e-12 ? Identity : new Quat(W / n, X / n, Y / n, Z / n);
    }

    /// <summary>ハミルトン積 a ⊗ b（b を先に、a を後に作用させる回転の合成）</summary>
    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    /// <summary>
    /// 回転行列 R（列ベクトル規約：v' = R·v = q ⊗ v ⊗ q*）。
    /// q が「機体座標→地上座標」の回転なら、R は機体座標のベクトルを地上座標に写す。
    /// </summary>
    public double[,] ToRotationMatrix()
    {
        var q = Normalized();
        double w = q.W, x = q.X, y = q.Y, z = q.Z;
        return new double[3, 3]
        {
            { 1 - 2 * (y * y + z * z), 2 * (x * y - w * z),     2 * (x * z + w * y) },
            { 2 * (x * y + w * z),     1 - 2 * (x * x + z * z), 2 * (y * z - w * x) },
            { 2 * (x * z - w * y),     2 * (y * z + w * x),     1 - 2 * (x * x + y * y) },
        };
    }
}

/// <summary>
/// 地上座標（Z 上向き）と WPF 座標（Y 上向き）の対応。
///
///   地上 X（基準姿勢で B 列が向く方向）→ WPF +Z（画面の手前）
///   地上 Y（基準姿勢で ESP32-S3 側が向く方向）→ WPF +X（画面の右）
///   地上 Z（上）→ WPF +Y（上）
///
/// これは軸の巡回置換 P で、行列式は +1（右手系どうし、鏡像にならない）。
/// 回転が単位元のとき、基板は「部品面が上・A 列が奥・B 列が手前・USB-C が左」で表示される。
/// </summary>
public static class CoordinateMapping
{
    /// <summary>地上座標のベクトルを WPF 座標に変換する（v_wpf = P·v_earth）</summary>
    public static Vector3D EarthToWpf(double x, double y, double z) => new(y, z, x);

    /// <summary>
    /// 機体座標で作ったモデルを WPF 上に置くための変換行列。
    /// v_wpf = P · R(q) · v_body。
    /// WPF の Matrix3D は行ベクトル規約（v' = v·M）なので、M = (P·R)ᵀ を返す。
    /// </summary>
    public static Matrix3D BodyToWpf(Quat q)
    {
        double[,] r = q.ToRotationMatrix();

        // P·R：P は「WPF の X,Y,Z 行 ← 地上の Y,Z,X 行」の並べ替え
        double[,] pr =
        {
            { r[1, 0], r[1, 1], r[1, 2] },
            { r[2, 0], r[2, 1], r[2, 2] },
            { r[0, 0], r[0, 1], r[0, 2] },
        };

        // M[i,j] = PR[j,i]
        return new Matrix3D(
            pr[0, 0], pr[1, 0], pr[2, 0], 0,
            pr[0, 1], pr[1, 1], pr[2, 1], 0,
            pr[0, 2], pr[1, 2], pr[2, 2], 0,
            0, 0, 0, 1);
    }
}
