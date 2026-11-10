using CardSatImu.Core;

namespace CardSatAttitudeGame;

public enum Difficulty { Easy, Normal, Hard, Practice }

public enum GamePhase { Lobby, Countdown, Playing, Result }

/// <summary>難易度ごとの設定</summary>
public sealed record DifficultySpec(
    Difficulty Id,
    string Name,
    string Description,
    double ToleranceDeg,     // この角度以内なら「合っている」
    bool DiscreteTargets,    // true：90° 刻みの姿勢、false：ランダムな姿勢
    double TimeLimitSec,     // 0 なら時間制限なし
    bool ShowTargetHint)     // 目標の姿勢を言葉でも表示するか
{
    public bool IsRanked => TimeLimitSec > 0;

    public static readonly DifficultySpec Easy =
        new(Difficulty.Easy, "やさしい", "90° 刻みの姿勢　許容 20°", 20, true, 60, true);
    public static readonly DifficultySpec Normal =
        new(Difficulty.Normal, "ふつう", "ランダムな姿勢　許容 15°", 15, false, 60, true);
    public static readonly DifficultySpec Hard =
        new(Difficulty.Hard, "むずかしい", "ランダムな姿勢　許容 10°　ヒントなし", 10, false, 60, false);
    public static readonly DifficultySpec Practice =
        new(Difficulty.Practice, "れんしゅう", "時間制限なし　90° 刻み　許容 20°", 20, true, 0, true);

    public static DifficultySpec Of(Difficulty d) => d switch
    {
        Difficulty.Easy => Easy,
        Difficulty.Normal => Normal,
        Difficulty.Hard => Hard,
        _ => Practice,
    };
}

/// <summary>Update の戻り値。1 フレームで複数起こりうるのでフラグにする</summary>
[Flags]
public enum GameEvents
{
    None = 0,
    CountdownTick = 1 << 0,     // カウントダウンの数字が変わった
    CountdownDone = 1 << 1,     // GO
    NewTarget = 1 << 2,         // 新しい目標が出た
    LockStarted = 1 << 3,       // 許容角に入った
    LockLost = 1 << 4,          // 許容角から出た
    Docked = 1 << 5,            // 1 秒キープして成功
    TimeWarningTick = 1 << 6,   // 残り 5 秒以下で 1 秒ごと
    Finished = 1 << 7,          // 時間切れ
}

/// <summary>
/// ゲームの進行（画面には触らない）。毎フレーム Update(dt, 現在の姿勢) を呼ぶ。
/// 姿勢はすべて「基準の向き（カウントダウン終了時の姿勢）」に対する回転で扱う。
/// </summary>
public sealed class GameEngine
{
    public const double HoldSeconds = 1.0;
    public const double CountdownSeconds = 3.0;
    public const double WarningSeconds = 10.0;

    private readonly TargetGenerator _targets = new();
    private double _holdTime;
    private double _targetStart;

    public GamePhase Phase { get; private set; } = GamePhase.Lobby;
    public DifficultySpec Spec { get; private set; } = DifficultySpec.Easy;
    public Quat Target { get; private set; } = Quat.Identity;
    public int TargetNumber { get; private set; }
    public double ErrorDeg { get; private set; } = 180;
    public bool IsLocked => _holdTime > 0;
    public double HoldProgress => Math.Clamp(_holdTime / HoldSeconds, 0, 1);
    public int Docked { get; private set; }
    public double Elapsed { get; private set; }
    public double CountdownLeft { get; private set; }
    public List<double> DockTimes { get; } = new();

    public double TimeLeft => Spec.TimeLimitSec > 0
        ? Math.Max(0, Spec.TimeLimitSec - Elapsed)
        : double.PositiveInfinity;

    public double AverageDockSeconds => DockTimes.Count > 0 ? DockTimes.Average() : 0;
    public double BestDockSeconds => DockTimes.Count > 0 ? DockTimes.Min() : 0;

    /// <summary>カウントダウンを始める。カウント中の目標は「基準の向き」（単位回転）</summary>
    public void Start(DifficultySpec spec)
    {
        Spec = spec;
        Phase = GamePhase.Countdown;
        CountdownLeft = CountdownSeconds;
        Target = Quat.Identity;
        TargetNumber = 0;
        Docked = 0;
        Elapsed = 0;
        ErrorDeg = 180;
        _holdTime = 0;
        DockTimes.Clear();
    }

    public void Abort() => Phase = GamePhase.Lobby;

    /// <summary>れんしゅうモードを終えて結果画面へ</summary>
    public void Finish()
    {
        if (Phase is GamePhase.Playing or GamePhase.Countdown)
            Phase = GamePhase.Result;
    }

    /// <summary>今の目標をとばす（れんしゅう用）</summary>
    public GameEvents Skip()
    {
        if (Phase != GamePhase.Playing)
            return GameEvents.None;
        NextTarget();
        return GameEvents.NewTarget;
    }

    public GameEvents Update(double dt, Quat current)
    {
        var ev = GameEvents.None;
        switch (Phase)
        {
            case GamePhase.Countdown:
            {
                int before = (int)Math.Ceiling(CountdownLeft);
                CountdownLeft -= dt;
                ErrorDeg = QuatUtil.AngleBetweenDeg(Target, current);
                if (CountdownLeft <= 0)
                {
                    CountdownLeft = 0;
                    Phase = GamePhase.Playing;
                    Elapsed = 0;
                    NextTarget();
                    ev |= GameEvents.CountdownDone | GameEvents.NewTarget;
                }
                else if ((int)Math.Ceiling(CountdownLeft) != before)
                {
                    ev |= GameEvents.CountdownTick;
                }
                break;
            }

            case GamePhase.Playing:
            {
                double leftBefore = TimeLeft;
                Elapsed += dt;
                ErrorDeg = QuatUtil.AngleBetweenDeg(Target, current);

                if (ErrorDeg <= Spec.ToleranceDeg)
                {
                    if (_holdTime <= 0)
                        ev |= GameEvents.LockStarted;
                    _holdTime += dt;
                }
                else if (_holdTime > 0)
                {
                    _holdTime = 0;
                    ev |= GameEvents.LockLost;
                }

                if (_holdTime >= HoldSeconds)
                {
                    Docked++;
                    DockTimes.Add(Elapsed - _targetStart);
                    NextTarget();
                    ev |= GameEvents.Docked | GameEvents.NewTarget;
                }

                if (Spec.TimeLimitSec > 0)
                {
                    double leftNow = TimeLeft;
                    if (leftNow <= 5 && Math.Ceiling(leftNow) != Math.Ceiling(leftBefore) && leftNow > 0)
                        ev |= GameEvents.TimeWarningTick;
                    if (leftNow <= 0)
                    {
                        Phase = GamePhase.Result;
                        ev |= GameEvents.Finished;
                    }
                }
                break;
            }
        }
        return ev;
    }

    private void NextTarget()
    {
        Target = _targets.Next(Spec.DiscreteTargets, Target);
        TargetNumber++;
        _targetStart = Elapsed;
        _holdTime = 0;
    }
}

/// <summary>目標の姿勢を作る</summary>
public sealed class TargetGenerator
{
    private const double MinSeparationDeg = 60;

    private readonly Random _rng = new();

    /// <summary>90° 刻みの 24 通りの姿勢（立方体の回転群）</summary>
    public static readonly IReadOnlyList<Quat> CubeRotations = BuildCubeRotations();

    public Quat Next(bool discrete, Quat previous)
    {
        Quat candidate = previous;
        for (int i = 0; i < 200; i++)
        {
            candidate = discrete
                ? CubeRotations[_rng.Next(CubeRotations.Count)]
                : QuatUtil.RandomUniform(_rng);
            if (QuatUtil.AngleBetweenDeg(candidate, previous) >= MinSeparationDeg)
                return candidate;
        }
        return candidate;
    }

    private static List<Quat> BuildCubeRotations()
    {
        var list = new List<Quat>();
        int[][] perms =
        {
            new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
        };
        foreach (int[] p in perms)
        {
            for (int signs = 0; signs < 8; signs++)
            {
                var m = new double[3, 3];
                for (int row = 0; row < 3; row++)
                    m[row, p[row]] = ((signs >> row) & 1) == 0 ? 1 : -1;

                if (Determinant(m) > 0)
                    list.Add(QuatUtil.FromRotationMatrix(m));
            }
        }
        return list;
    }

    private static double Determinant(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
        - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
        + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
}
