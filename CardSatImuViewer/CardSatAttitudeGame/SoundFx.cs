using System.IO;
using System.Windows.Media;

namespace CardSatAttitudeGame;

/// <summary>
/// 効果音。追加の NuGet を使わないよう、起動時に波形を計算して WAV を一時フォルダへ書き、
/// WPF の MediaPlayer で鳴らす。
/// </summary>
public sealed class SoundFx
{
    private const int SampleRate = 44100;

    private readonly Dictionary<string, MediaPlayer> _players = new();

    public bool Enabled { get; set; } = true;

    public SoundFx()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CardSatAttitudeGame");
        try
        {
            Directory.CreateDirectory(dir);
            Create(dir, "count", Notes((880, 0.16)), 0.45);
            Create(dir, "go", Sweep(660, 1320, 0.32), 0.5);
            Create(dir, "lock", Notes((523, 0.07)), 0.35);
            Create(dir, "dock", Notes((784, 0.09), (1047, 0.09), (1568, 0.30)), 0.55);
            Create(dir, "tick", Notes((1245, 0.06)), 0.4);
            Create(dir, "finish", Notes((523, 0.14), (659, 0.14), (784, 0.14), (1047, 0.55)), 0.55);
            Create(dir, "select", Notes((1400, 0.03)), 0.25);
        }
        catch
        {
            // 音が出せなくてもゲームは続ける
        }
    }

    public void Play(string name)
    {
        if (!Enabled || !_players.TryGetValue(name, out var player))
            return;
        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    private void Create(string dir, string name, float[] samples, double volume)
    {
        string path = Path.Combine(dir, name + ".wav");
        try
        {
            WriteWav(path, samples);
        }
        catch (IOException)
        {
            // 別のゲームが起動中でファイルが使用中なら、既存のファイルを使う
            if (!File.Exists(path))
                return;
        }
        var player = new MediaPlayer { Volume = volume };
        player.Open(new Uri(path, UriKind.Absolute));
        _players[name] = player;
    }

    // ------------------------------------------------------------------
    // 波形の生成
    // ------------------------------------------------------------------

    /// <summary>音を順に並べる。少し倍音を混ぜて電子音らしくする</summary>
    private static float[] Notes(params (double freq, double sec)[] notes)
    {
        var buf = new List<float>();
        foreach (var (freq, sec) in notes)
        {
            int n = (int)(sec * SampleRate);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SampleRate;
                double env = Envelope(i, n);
                double v = Math.Sin(2 * Math.PI * freq * t)
                         + 0.30 * Math.Sin(2 * Math.PI * freq * 2 * t)
                         + 0.12 * Math.Sin(2 * Math.PI * freq * 3 * t);
                buf.Add((float)(v * env / 1.42));
            }
        }
        return buf.ToArray();
    }

    /// <summary>周波数を滑らかに上げる（GO の音）</summary>
    private static float[] Sweep(double f0, double f1, double sec)
    {
        int n = (int)(sec * SampleRate);
        var buf = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double k = (double)i / n;
            double f = f0 * Math.Pow(f1 / f0, k);
            phase += 2 * Math.PI * f / SampleRate;
            buf[i] = (float)((Math.Sin(phase) + 0.25 * Math.Sin(2 * phase)) * Envelope(i, n) / 1.25);
        }
        return buf;
    }

    /// <summary>立ち上がり 4 ms、その後ゆるやかに減衰（プツッという音を防ぐ）</summary>
    private static double Envelope(int i, int n)
    {
        int attack = (int)(0.004 * SampleRate);
        double a = i < attack ? (double)i / attack : 1.0;
        double k = (double)i / n;
        double decay = Math.Exp(-3.0 * k);
        double release = k > 0.9 ? (1 - k) / 0.1 : 1.0;
        return a * decay * release;
    }

    private static void WriteWav(string path, float[] samples)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                 // fmt チャンクの大きさ
        w.Write((short)1);           // PCM
        w.Write((short)1);           // モノラル
        w.Write(SampleRate);
        w.Write(SampleRate * 2);     // バイト/秒
        w.Write((short)2);           // ブロック境界
        w.Write((short)16);          // ビット数
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (float s in samples)
            w.Write((short)Math.Clamp(s * 32000f, -32767f, 32767f));
    }
}
