using CardSatImu.Core;

namespace CardSatAttitudeGame;

/// <summary>ゲームで使うクォータニオンの補助計算</summary>
public static class QuatUtil
{
    /// <summary>
    /// 2 つの姿勢の差の回転角 [度]。a* ⊗ b の w 成分は a・b（内積）なので θ = 2·acos|a・b|。
    /// q と -q は同じ姿勢なので絶対値をとる。
    /// </summary>
    public static double AngleBetweenDeg(Quat a, Quat b)
    {
        double d = Math.Abs(a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z);
        d = Math.Min(1.0, d);
        return 2.0 * Math.Acos(d) * 180.0 / Math.PI;
    }

    /// <summary>回転行列（列ベクトル規約）からクォータニオンを作る（Shepperd の方法）</summary>
    public static Quat FromRotationMatrix(double[,] m)
    {
        double tr = m[0, 0] + m[1, 1] + m[2, 2];
        double w, x, y, z;
        if (tr > 0)
        {
            double s = Math.Sqrt(tr + 1.0) * 2;
            w = 0.25 * s;
            x = (m[2, 1] - m[1, 2]) / s;
            y = (m[0, 2] - m[2, 0]) / s;
            z = (m[1, 0] - m[0, 1]) / s;
        }
        else if (m[0, 0] > m[1, 1] && m[0, 0] > m[2, 2])
        {
            double s = Math.Sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2;
            w = (m[2, 1] - m[1, 2]) / s;
            x = 0.25 * s;
            y = (m[0, 1] + m[1, 0]) / s;
            z = (m[0, 2] + m[2, 0]) / s;
        }
        else if (m[1, 1] > m[2, 2])
        {
            double s = Math.Sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2;
            w = (m[0, 2] - m[2, 0]) / s;
            x = (m[0, 1] + m[1, 0]) / s;
            y = 0.25 * s;
            z = (m[1, 2] + m[2, 1]) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2;
            w = (m[1, 0] - m[0, 1]) / s;
            x = (m[0, 2] + m[2, 0]) / s;
            y = (m[1, 2] + m[2, 1]) / s;
            z = 0.25 * s;
        }
        return new Quat(w, x, y, z).Normalized();
    }

    /// <summary>一様にランダムな姿勢（Shoemake の方法）</summary>
    public static Quat RandomUniform(Random rng)
    {
        double u1 = rng.NextDouble(), u2 = rng.NextDouble(), u3 = rng.NextDouble();
        double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
        return new Quat(
            b * Math.Cos(2 * Math.PI * u3),
            a * Math.Sin(2 * Math.PI * u2),
            a * Math.Cos(2 * Math.PI * u2),
            b * Math.Sin(2 * Math.PI * u3));
    }
}

/// <summary>
/// 姿勢を言葉で説明する（受講生向けのヒント）。
/// 基準の向き（部品面が上・A 列が奥・USB-C が左）の座標で、部品面と B 列がどちらを向いているかを返す。
///   +X = 手前（基準で B 列が向く方向）、+Y = 右、+Z = 上
/// </summary>
public static class AttitudeDescriber
{
    private static readonly string[] Positive = { "手前", "右", "上" };
    private static readonly string[] Negative = { "奥", "左", "下" };

    public static string Describe(Quat q)
    {
        double[,] r = q.ToRotationMatrix();
        // R の列 j は、機体の j 軸が基準座標でどちらを向くか
        string top = Direction(r[0, 2], r[1, 2], r[2, 2]); // 機体 +Z = 部品面
        string b = Direction(r[0, 0], r[1, 0], r[2, 0]);   // 機体 +X = B 列
        return $"部品面：{top}　B 列：{b}";
    }

    private static string Direction(double x, double y, double z)
    {
        double[] v = { x, y, z };
        int[] order = { 0, 1, 2 };
        Array.Sort(order, (i, j) => Math.Abs(v[j]).CompareTo(Math.Abs(v[i])));

        string Name(int i) => v[i] >= 0 ? Positive[i] : Negative[i];

        if (Math.Abs(v[order[0]]) >= 0.9)
            return Name(order[0]) + "向き";
        return $"{Name(order[0])}と{Name(order[1])}のあいだ";
    }
}
