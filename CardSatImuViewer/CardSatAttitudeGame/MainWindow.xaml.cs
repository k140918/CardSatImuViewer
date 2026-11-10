using System.Diagnostics;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using CardSatImu.Core;

namespace CardSatAttitudeGame;

/// <summary>ランキング 1 行分の表示用データ</summary>
public sealed record RankingRow(string Rank, string Name, string Docked, string Average, Brush RankBrush, Brush RowBrush);

public partial class MainWindow : Window
{
    // ---- 色 ----
    private static readonly Color Cyan = Color.FromRgb(0x3D, 0xE0, 0xFF);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB5, 0x47);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x3D);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x4F, 0x64);
    private static readonly Color Green = Color.FromRgb(0x4D, 0xFF, 0xA6);
    private static readonly Color GridColor = Color.FromRgb(0x1C, 0x5E, 0x78);

    private static readonly Brush CyanBrush = Frozen(Cyan);
    private static readonly Brush AmberBrush = Frozen(Amber);
    private static readonly Brush RedBrush = Frozen(Red);
    private static readonly Brush GreenBrush = Frozen(Green);
    private static readonly Brush WhiteBrush = Frozen(Color.FromRgb(0xE6, 0xF4, 0xFF));
    private static readonly Brush SubBrush = Frozen(Color.FromRgb(0x8F, 0xB3, 0xC9));
    private static readonly Brush GoldBrush = Frozen(Color.FromRgb(0xFF, 0xD3, 0x6B));
    private static readonly Brush SilverBrush = Frozen(Color.FromRgb(0xD8, 0xE3, 0xEC));
    private static readonly Brush BronzeBrush = Frozen(Color.FromRgb(0xE0, 0x9A, 0x64));
    private static readonly Brush RowBrushA = Frozen(Color.FromArgb(0x22, 0x3D, 0xE0, 0xFF));
    private static readonly Brush RowBrushB = Frozen(Color.FromArgb(0x0C, 0x3D, 0xE0, 0xFF));

    // ---- ゲーム ----
    private readonly GameEngine _engine = new();
    private readonly RankingStore _ranking = new();
    private readonly SoundFx _sound = new();
    private readonly AttitudeView _targetView = new(GridColor, 90);
    private readonly AttitudeView _liveView = new(GridColor, 70);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Difficulty _difficulty = Difficulty.Easy;
    private bool _ready;

    // ---- 通信 ----
    private SerialReader? _reader;
    private string? _selectedPort;
    private Quat _rawQ = Quat.Identity;
    private Quat? _reference;            // 基準の向き（カウントダウン終了時の q）
    private double _lastSampleTime = double.NegativeInfinity;
    private long _rateCount;
    private double _rateStart;
    private double _rateHz;
    private string _lastBoardMessage = "";

    // ---- 演出 ----
    private readonly BlurEffect _hudBlur = new() { Radius = 7 };
    private readonly ScaleTransform _scoreScale = new(1, 1);
    private readonly ScaleTransform _countScale = new(1, 1);
    private readonly ScaleTransform _bannerScale = new(1, 1);
    private readonly ScaleTransform _resultScale = new(1, 1);
    private readonly DropShadowEffect _errorGlow = new() { Color = Cyan, ShadowDepth = 0, BlurRadius = 18, Opacity = 0.9 };
    private readonly DropShadowEffect _timerGlow = new() { Color = Cyan, ShadowDepth = 0, BlurRadius = 14, Opacity = 0.9 };
    private readonly DropShadowEffect _startGlow = new() { Color = Orange, ShadowDepth = 0, BlurRadius = 16, Opacity = 0.9 };
    private double _lastFrame;
    private double _lastLobbyUpdate;
    private bool _timerWarning;

    // ---- 全画面 ----
    private bool _fullScreen;
    private WindowState _savedState;
    private WindowStyle _savedStyle;

    public MainWindow()
    {
        InitializeComponent();
        Focusable = true;

        TargetViewHost.Child = _targetView.Viewport;
        LiveViewHost.Child = _liveView.Viewport;
        ScoreText.RenderTransform = _scoreScale;
        CountdownText.RenderTransform = _countScale;
        DockedBanner.RenderTransform = _bannerScale;
        ResultCard.RenderTransform = _resultScale;
        ErrorText.Effect = _errorGlow;
        TimerText.Effect = _timerGlow;
        StartButton.Effect = _startGlow;
        _startGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(10, 34, TimeSpan.FromSeconds(1.1))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

        _ready = true;
        RefreshPorts();
        ShowLobby();

        CompositionTarget.Rendering += OnRendering;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private string PilotName => string.IsNullOrWhiteSpace(NameBox.Text) ? "ななしのパイロット" : NameBox.Text.Trim();

    // =====================================================================
    // 毎フレームの処理（UI スレッド）
    // =====================================================================

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;

        PollSerial(now);
        UpdateLinkIndicator(now);

        // 基準の向きに対する今の姿勢（基準がまだなければ地上座標のまま）
        Quat rel = _reference is { } r ? (r.Conjugate() * _rawQ).Normalized() : _rawQ;
        _liveView.SetOrientation(rel);
        LiveHintText.Text = AttitudeDescriber.Describe(rel);

        GameEvents ev = _engine.Update(dt, rel);
        if (ev != GameEvents.None)
            HandleEvents(ev);

        UpdateGauges();

        if (_engine.Phase == GamePhase.Lobby && now - _lastLobbyUpdate > 0.2)
        {
            _lastLobbyUpdate = now;
            UpdateLobbyState(now);
        }
    }

    private void PollSerial(double now)
    {
        if (_reader is not { } reader)
            return;

        while (reader.Messages.TryDequeue(out var msg))
            _lastBoardMessage = msg;

        if (reader.TryTakeLatest(out var s))
        {
            _rawQ = s.Q;
            _lastSampleTime = now;
        }

        if (now - _rateStart >= 1.0)
        {
            long count = reader.SampleCount;
            _rateHz = (count - _rateCount) / (now - _rateStart);
            _rateCount = count;
            _rateStart = now;
        }
    }

    private bool IsStreaming(double now) => _reader is not null && now - _lastSampleTime < 1.0;

    private void UpdateLinkIndicator(double now)
    {
        string text;
        Brush led;
        if (_reader is null)
        {
            text = "NO LINK";
            led = RedBrush;
        }
        else if (!IsStreaming(now))
        {
            text = "NO DATA";
            led = AmberBrush;
        }
        else
        {
            text = $"LINK  {_rateHz:F0} Hz";
            led = GreenBrush;
        }

        if (LinkText.Text != text)
            LinkText.Text = text;
        if (!ReferenceEquals(LinkLed.Fill, led))
            LinkLed.Fill = led;
    }

    // =====================================================================
    // ゲームの出来事に応じた演出
    // =====================================================================

    private void HandleEvents(GameEvents ev)
    {
        if (ev.HasFlag(GameEvents.CountdownTick))
        {
            ShowCountdown(((int)Math.Ceiling(_engine.CountdownLeft)).ToString());
            _sound.Play("count");
        }

        if (ev.HasFlag(GameEvents.CountdownDone))
        {
            // 0 になった瞬間の向きを基準にする
            _reference = _rawQ;
            ShowCountdown("GO!");
            _sound.Play("go");
            InstructionBanner.Visibility = Visibility.Collapsed;
            FadeIn(CenterReadout, 0.4);
            MessageText.Text = "ミッション開始！ 左の「目標の姿勢」と同じ向きに CardSat を動かしてください。";
        }

        if (ev.HasFlag(GameEvents.Docked))
            OnDocked();

        if (ev.HasFlag(GameEvents.NewTarget))
            OnNewTarget();

        if (ev.HasFlag(GameEvents.LockStarted))
            _sound.Play("lock");

        if (ev.HasFlag(GameEvents.TimeWarningTick))
            _sound.Play("tick");

        if (ev.HasFlag(GameEvents.Finished))
            ShowResult();
    }

    private void OnNewTarget()
    {
        _targetView.SetOrientation(_engine.Target);
        TargetNoText.Text = $"TARGET #{_engine.TargetNumber:00}";
        TargetHintText.Text = _engine.Spec.ShowTargetHint
            ? AttitudeDescriber.Describe(_engine.Target)
            : "ヒントなし（むずかしい）";

        // 目標パネルが一瞬オレンジに光ってからシアンに戻る
        var flash = new SolidColorBrush(Orange);
        TargetPanel.BorderBrush = flash;
        flash.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Orange, Cyan, TimeSpan.FromSeconds(0.9)));

        FadeIn(TargetViewHost, 0.45);
    }

    private void OnDocked()
    {
        _sound.Play("dock");
        double sec = _engine.DockTimes.Count > 0 ? _engine.DockTimes[^1] : 0;

        ScoreText.Text = $"{_engine.Docked:00}";
        var pop = new DoubleAnimation(1.7, 1.0, TimeSpan.FromSeconds(0.45))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        _scoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _scoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        FlashRect.BeginAnimation(OpacityProperty, new DoubleAnimation(0.28, 0, TimeSpan.FromSeconds(0.6)));

        DockedBannerSub.Text = $"#{_engine.Docked}　{sec:F1} 秒";
        DockedBanner.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.08))),
                new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7))),
                new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))),
            },
        });
        var grow = new DoubleAnimation(0.7, 1.08, TimeSpan.FromSeconds(1.2))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _bannerScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _bannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        MessageText.Text = $"ドッキング成功！（{sec:F1} 秒）　次の目標が出ました。";
    }

    private void ShowCountdown(string text)
    {
        CountdownText.Text = text;
        CountdownText.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.55))),
                new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.95))),
            },
        });
        var shrink = new DoubleAnimation(1.8, 1.0, TimeSpan.FromSeconds(0.5))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _countScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _countScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    private static void FadeIn(UIElement element, double seconds)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>ズレのリング・数値・状態表示・残り時間</summary>
    private void UpdateGauges()
    {
        GamePhase phase = _engine.Phase;
        if (phase is not (GamePhase.Playing or GamePhase.Countdown))
            return;

        DifficultySpec spec = _engine.Spec;
        double err = _engine.ErrorDeg;
        double tol = spec.ToleranceDeg;

        Color c = err <= tol ? Green
                : err <= tol * 2 ? Cyan
                : err <= 60 ? Amber
                : Red;
        Brush brush = err <= tol ? GreenBrush
                    : err <= tol * 2 ? CyanBrush
                    : err <= 60 ? AmberBrush
                    : RedBrush;

        ErrorText.Text = $"{err:F1}°";
        ErrorText.Foreground = brush;
        _errorGlow.Color = c;
        AccuracyRing.Value = Math.Clamp(1 - err / 180.0, 0, 1);
        AccuracyRing.RingBrush = brush;
        HoldRing.Value = phase == GamePhase.Playing ? _engine.HoldProgress : 0;

        if (phase == GamePhase.Countdown)
        {
            SetStatus("STAND BY", AmberBrush, "基準の向きに置いて待ってね");
        }
        else if (_engine.IsLocked)
        {
            SetStatus("LOCKING…", GreenBrush, "そのまま 1 秒キープ！");
            LivePanel.BorderBrush = GreenBrush;
        }
        else
        {
            if (err <= tol * 2)
                SetStatus("CLOSE", CyanBrush, "もうすこし！");
            else
                SetStatus("ALIGNING", AmberBrush, "目標の向きに合わせよう");
            if (!ReferenceEquals(LivePanel.BorderBrush, CyanBrush))
                LivePanel.BorderBrush = CyanBrush;
        }

        // 残り時間
        if (spec.TimeLimitSec > 0)
        {
            double left = phase == GamePhase.Countdown ? spec.TimeLimitSec : _engine.TimeLeft;
            int s = (int)Math.Ceiling(left);
            TimerText.Text = $"{s / 60:00}:{s % 60:00}";
            if (phase == GamePhase.Playing && left <= GameEngine.WarningSeconds && !_timerWarning)
                StartTimerWarning();
        }
        else
        {
            int s = (int)_engine.Elapsed;
            TimerText.Text = $"{s / 60:00}:{s % 60:00}";
        }
    }

    private void SetStatus(string text, Brush brush, string sub)
    {
        if (StatusText.Text != text)
        {
            StatusText.Text = text;
            StatusText.Foreground = brush;
            if (StatusText.Effect is DropShadowEffect glow && brush is SolidColorBrush sb)
            {
                // スタイルの共有エフェクトは凍結されているので、色を変えるときは作り直す
                StatusText.Effect = new DropShadowEffect { Color = sb.Color, ShadowDepth = 0, BlurRadius = glow.BlurRadius, Opacity = glow.Opacity };
            }
        }
        if (StatusSubText.Text != sub)
            StatusSubText.Text = sub;
    }

    private void StartTimerWarning()
    {
        _timerWarning = true;
        TimerText.Foreground = RedBrush;
        _timerGlow.Color = Red;
        TimerText.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(0.5))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopTimerWarning()
    {
        _timerWarning = false;
        TimerText.BeginAnimation(OpacityProperty, null);
        TimerText.Opacity = 1;
        TimerText.Foreground = WhiteBrush;
        _timerGlow.Color = Cyan;
    }

    // =====================================================================
    // 画面の切り替え
    // =====================================================================

    private void ShowLobby()
    {
        _engine.Abort();
        StopTimerWarning();

        LobbyOverlay.Visibility = Visibility.Visible;
        ResultOverlay.Visibility = Visibility.Collapsed;
        InstructionBanner.Visibility = Visibility.Collapsed;
        HudLayer.Effect = _hudBlur;

        _targetView.SetOrientation(Quat.Identity);
        TargetNoText.Text = "REFERENCE";
        TargetHintText.Text = "基準の向き：部品面が上・A 列が奥・USB-C が左";
        ScoreText.Text = "00";
        ErrorText.Text = "---";
        ErrorText.Foreground = WhiteBrush;
        _errorGlow.Color = Cyan;
        AccuracyRing.Value = 0;
        HoldRing.Value = 0;
        CenterReadout.BeginAnimation(OpacityProperty, null);
        CenterReadout.Opacity = 1;
        LivePanel.BorderBrush = CyanBrush;
        SetStatus("STANDBY", CyanBrush, "");
        MessageText.Text = "ブリーフィング：01〜04 を順に進めて MISSION START を押してください。";
        KeyHelpText.Text = "Enter：スタート　F11：全画面";

        UpdateDifficultyPreview();
        UpdateRanking();
        UpdateLobbyState(_clock.Elapsed.TotalSeconds);
    }

    private void StartGame()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (!IsStreaming(now))
        {
            ShowLobby();
            return;
        }

        DifficultySpec spec = DifficultySpec.Of(_difficulty);
        _engine.Start(spec);

        // 名前欄にフォーカスが残っていると、Space などが入力されてしまうのでウィンドウへ移す
        Focus();

        LobbyOverlay.Visibility = Visibility.Collapsed;
        ResultOverlay.Visibility = Visibility.Collapsed;
        InstructionBanner.Visibility = Visibility.Visible;
        HudLayer.Effect = null;
        StopTimerWarning();

        _targetView.SetOrientation(Quat.Identity);
        TargetNoText.Text = "REFERENCE";
        TargetHintText.Text = "この向きに置いて待ってね";
        TargetPanel.BorderBrush = CyanBrush;
        ScoreText.Text = "00";
        ModeText.Text = $"{spec.Name}　／　{PilotName}";
        ToleranceText.Text = $"許容 {spec.ToleranceDeg:F0}°";
        CenterReadout.BeginAnimation(OpacityProperty, null);
        CenterReadout.Opacity = 0;

        MessageText.Text = "カウントダウン中に、基板を基準の向き（部品面を上・A 列を奥・USB-C を左）に置いてください。";
        KeyHelpText.Text = spec.IsRanked
            ? "Esc：中断してタイトルへ"
            : "Space：次の目標へ　Esc：おわる";

        ShowCountdown(((int)Math.Ceiling(GameEngine.CountdownSeconds)).ToString());
        _sound.Play("count");
    }

    private void ShowResult()
    {
        _sound.Play("finish");
        StopTimerWarning();

        DifficultySpec spec = _engine.Spec;
        double avg = _engine.AverageDockSeconds;
        double best = _engine.BestDockSeconds;

        ResultScoreText.Text = _engine.Docked.ToString();
        ResultModeText.Text = $"難易度：{spec.Name}　パイロット：{PilotName}";
        ResultAvgText.Text = _engine.Docked > 0 ? $"{avg:F1} 秒" : "---";
        ResultBestText.Text = _engine.Docked > 0 ? $"{best:F1} 秒" : "---";

        if (spec.IsRanked)
        {
            int rank = _ranking.Add(new RankingEntry(PilotName, spec.Id, _engine.Docked, avg, DateTime.Now));
            ResultRankText.Text = rank == 1
                ? "★ NEW RECORD！ ランキング 1 位 ★"
                : rank <= RankingStore.TopCount
                    ? $"ランキング {rank} 位にランクイン！"
                    : $"今回の順位：{rank} 位";
        }
        else
        {
            ResultRankText.Text = "れんしゅうモード（ランキングには記録されません）";
        }

        ResultOverlay.Visibility = Visibility.Visible;
        HudLayer.Effect = _hudBlur;
        ResultCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35)));
        var pop = new DoubleAnimation(0.85, 1.0, TimeSpan.FromSeconds(0.45))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        };
        _resultScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _resultScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        MessageText.Text = "ミッション終了。おつかれさまでした！";
        KeyHelpText.Text = "Enter：もう一度　Esc：タイトルへ";
        UpdateRanking();
    }

    // =====================================================================
    // ロビーの表示
    // =====================================================================

    private void UpdateLobbyState(double now)
    {
        bool connected = _reader is not null;
        bool streaming = IsStreaming(now);

        ConnectButton.Content = connected ? "切断" : "接続";
        RefreshButton.IsEnabled = !connected;
        PortList.IsEnabled = !connected;
        CalibButton.IsEnabled = connected;
        StartButton.IsEnabled = streaming;
        _startGlow.Opacity = streaming ? 0.9 : 0;

        string status = !connected
            ? "COM ポートを選んで「接続」を押してください。"
            : streaming
                ? $"{_reader!.PortName} と通信中（{_rateHz:F0} Hz）"
                : $"{_reader!.PortName} に接続しましたが、データが届いていません。";
        if (connected && _lastBoardMessage.Length > 0)
            status += $"\n基板：{_lastBoardMessage}";
        if (LinkStatusText.Text != status)
            LinkStatusText.Text = status;

        string hint = !connected ? "まず 01 で接続してください"
                    : !streaming ? "基板からのデータを待っています…"
                    : "準備 OK！";
        if (StartHint.Text != hint)
            StartHint.Text = hint;
    }

    private void UpdateDifficultyPreview()
    {
        DifficultySpec spec = DifficultySpec.Of(_difficulty);
        ModeText.Text = spec.Name;
        ToleranceText.Text = $"許容 {spec.ToleranceDeg:F0}°";
        if (spec.TimeLimitSec > 0)
        {
            int s = (int)spec.TimeLimitSec;
            TimerText.Text = $"{s / 60:00}:{s % 60:00}";
        }
        else
        {
            TimerText.Text = "FREE";
        }
    }

    private void UpdateRanking()
    {
        DifficultySpec spec = DifficultySpec.Of(_difficulty);
        RankingTitle.Text = $"{spec.Name}　TOP {RankingStore.TopCount}";

        if (!spec.IsRanked)
        {
            RankingList.ItemsSource = null;
            RankingNote.Text = "れんしゅうモードはランキングに記録されません。";
            return;
        }

        var rows = _ranking.Top(_difficulty)
            .Select((e, i) => new RankingRow(
                $"{i + 1}",
                e.Name,
                $"{e.Docked}",
                e.Docked > 0 ? $"{e.AverageSeconds:F1}s" : "---",
                i switch { 0 => GoldBrush, 1 => SilverBrush, 2 => BronzeBrush, _ => SubBrush },
                i % 2 == 0 ? RowBrushA : RowBrushB))
            .ToList();

        RankingList.ItemsSource = rows;
        RankingNote.Text = rows.Count == 0
            ? "まだ記録がありません。最初のパイロットになろう！"
            : "成功回数が多い順（同じなら平均時間が短い順）";
    }

    // =====================================================================
    // 接続
    // =====================================================================

    private void RefreshPorts()
    {
        PortList.Children.Clear();
        var ports = SerialPort.GetPortNames()
            .Distinct()
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ports.Count == 0)
        {
            PortList.Children.Add(new TextBlock
            {
                Text = "COM ポートが見つかりません。USB をつないで「更新」を押してください。",
                Style = (Style)FindResource("SubText"),
            });
            _selectedPort = null;
            return;
        }

        if (_selectedPort is null || !ports.Contains(_selectedPort))
            _selectedPort = ports[^1]; // 後から挿した USB 機器は番号が大きいことが多い

        foreach (string port in ports)
        {
            var chip = new RadioButton
            {
                Content = port,
                GroupName = "Port",
                Style = (Style)FindResource("PortChip"),
                IsChecked = port == _selectedPort,
            };
            chip.Checked += (_, _) =>
            {
                _selectedPort = port;
                _sound.Play("select");
            };
            PortList.Children.Add(chip);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reader is null)
            Connect();
        else
            Disconnect("切断しました。");
    }

    private void Connect()
    {
        if (_selectedPort is not { } port)
        {
            LinkStatusText.Text = "COM ポートを選んでください。";
            return;
        }

        var reader = new SerialReader(port);
        reader.Faulted += ex =>
            Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_reader, reader))
                    Disconnect($"{port} との通信が切れました：{ex.Message}");
            });

        try
        {
            reader.Start();
        }
        catch (Exception ex)
        {
            reader.Dispose();
            LinkStatusText.Text = $"{port} を開けませんでした：{ex.Message}";
            return;
        }

        _reader = reader;
        _reference = null;
        _lastBoardMessage = "";
        _rateCount = 0;
        _rateStart = _clock.Elapsed.TotalSeconds;
        _sound.Play("select");
        UpdateLobbyState(_rateStart);
    }

    private void Disconnect(string reason)
    {
        var reader = _reader;
        if (reader is null)
            return;
        _reader = null;
        reader.Dispose();
        _lastSampleTime = double.NegativeInfinity;
        MessageText.Text = reason;
        if (_engine.Phase == GamePhase.Lobby)
            UpdateLobbyState(_clock.Elapsed.TotalSeconds);
    }

    private void CalibButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reader is null)
            return;
        try
        {
            _reader.Send("c");
            _sound.Play("select");
            MessageText.Text = "零点校正コマンドを送りました。基板を動かさずに待ってください。";
        }
        catch (Exception ex)
        {
            MessageText.Text = $"送信に失敗しました：{ex.Message}";
        }
    }

    // =====================================================================
    // ボタン・キー操作
    // =====================================================================

    private void Difficulty_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out Difficulty d))
            _difficulty = d;

        // InitializeComponent 中（他の要素がまだ無い）は何もしない
        if (!_ready)
            return;

        _sound.Play("select");
        UpdateDifficultyPreview();
        UpdateRanking();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e) => StartGame();

    private void Retry_Click(object sender, RoutedEventArgs e) => StartGame();

    private void ToTitle_Click(object sender, RoutedEventArgs e) => ShowLobby();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        switch (_engine.Phase)
        {
            case GamePhase.Lobby:
                if (e.Key == Key.Enter && StartButton.IsEnabled)
                {
                    StartGame();
                    e.Handled = true;
                }
                break;

            case GamePhase.Countdown:
            case GamePhase.Playing:
                if (e.Key == Key.Escape)
                {
                    if (_engine.Spec.IsRanked)
                    {
                        ShowLobby();
                    }
                    else
                    {
                        _engine.Finish();
                        ShowResult();
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.Space && !_engine.Spec.IsRanked)
                {
                    GameEvents ev = _engine.Skip();
                    if (ev != GameEvents.None)
                        HandleEvents(ev);
                    e.Handled = true;
                }
                break;

            case GamePhase.Result:
                if (e.Key == Key.Enter)
                {
                    StartGame();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    ShowLobby();
                    e.Handled = true;
                }
                break;
        }
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedState = WindowState;
            _savedStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;   // いったん戻してから最大化するとタスクバーまで覆える
            WindowState = WindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            WindowStyle = _savedStyle;
            WindowState = _savedState;
            _fullScreen = false;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        Disconnect("終了");
    }
}
