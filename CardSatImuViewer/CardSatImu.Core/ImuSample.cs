using System.Globalization;

namespace CardSatImu.Core;

/// <summary>
/// 基板から届く 1 行分のデータ。
/// 形式：Q,w,x,y,z,roll,pitch,yaw（クォータニオンと確認用オイラー角[度]）
/// </summary>
public readonly record struct ImuSample(Quat Q, double Roll, double Pitch, double Yaw)
{
    public static bool TryParse(string line, out ImuSample sample)
    {
        sample = default;
        if (!line.StartsWith("Q,", StringComparison.Ordinal))
            return false;

        string[] parts = line.Split(',');
        // 先頭の "Q" + 7 個の数値。将来フィールドが増えても先頭 7 個だけ読む
        if (parts.Length < 8)
            return false;

        var v = new double[7];
        for (int i = 0; i < 7; i++)
        {
            if (!double.TryParse(parts[i + 1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])
                || !double.IsFinite(v[i]))
                return false;
        }

        var q = new Quat(v[0], v[1], v[2], v[3]);
        // 正規化されていないものは多少なら許すが、明らかに壊れた値は捨てる
        double n = q.Norm;
        if (n < 0.5 || n > 1.5)
            return false;

        sample = new ImuSample(q.Normalized(), v[4], v[5], v[6]);
        return true;
    }
}
