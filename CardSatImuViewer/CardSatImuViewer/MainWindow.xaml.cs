using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CardSatImu.Core;

namespace CardSatImuViewer;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 200;
    private const int MaxMessagesPerFrame = 50;

    // カメラ：手前斜め上から原点を見る。ホイールで距離だけ変える
    private static readonly Vector3D CameraDirection = Normalize(new Vector3D(0, -0.62, -1.0));
    private const double MinCameraDistance = 80;
    private const double MaxCameraDistance = 800;
    private double _cameraDistance = 230;

    private readonly ObservableCollection<string> _log = new();
    private readonly MatrixTransform3D _boardTransform = new(Matrix3D.Identity);
    private readonly Stopwatch _rateWatch = new();

    private SerialReader? _reader;
    private Quat _lastQ = Quat.Identity;
    private bool _hasSample;
    private Quat? _reference;          // 姿勢リセット時の q（null なら地上座標のまま表示）
    private long _rateLastCount;

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = _log;
        SetupScene();
        SetupLegend();
        UpdateCamera();
        RefreshPorts();
        UpdateReferenceText();
        UpdateUiState();

        CompositionTarget.Rendering += OnRendering;
    }

    // =====================================================================
    // 3D シーン
    // =====================================================================

    private void SetupScene()
    {
        // 地上の目安グリッド（回転しない）
        Viewport.Children.Add(new ModelVisual3D { Content = BoardModel.CreateGroundGrid() });

        // 基板（機体座標で作ったモデルを、毎フレーム BodyToWpf の行列で置く）
        var board = new ModelVisual3D
        {
            Content = BoardModel.CreateBoard(),
            Transform = _boardTransform,
        };
        Viewport.Children.Add(board);

        ApplyOrientation();
    }

    private void SetupLegend()
    {
        static SolidColorBrush B(Color c) => new(c);
        SwatchAxisX.Background = B(BoardModel.AxisXColor);
        SwatchAxisY.Background = B(BoardModel.AxisYColor);
        SwatchAxisZ.Background = B(BoardModel.AxisZColor);
        SwatchEdgeA.Background = B(BoardModel.EdgeAColor);
        SwatchEdgeB.Background = B(BoardModel.EdgeBColor);
        SwatchEdgeUsb.Background = B(BoardModel.EdgeUsbColor);
        SwatchTop.Background = B(BoardModel.RealTopColor);
        SwatchBottom.Background = B(BoardModel.RealBottomColor);
    }

    private void UpdateCamera()
    {
        Camera.Position = new Point3D(0, 0, 0) - CameraDirection * _cameraDistance;
        Camera.LookDirection = CameraDirection * _cameraDistance;
    }

    private void ViewportHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _cameraDistance *= e.Delta > 0 ? 0.9 : 1.0 / 0.9;
        _cameraDistance = Math.Clamp(_cameraDistance, MinCameraDistance, MaxCameraDistance);
        UpdateCamera();
        e.Handled = true;
    }

    /// <summary>
    /// 表示する回転を決めて基板に適用する。
    ///   基準なし：q（機体→地上）をそのまま表示。傾きは重力基準、ヨーは成り行き。
    ///   基準あり：q_rel = q_ref* ⊗ q（機体→リセット時の機体）を表示。
    ///             リセットした瞬間は単位回転＝「部品面が上・A 列が奥・USB-C が左」になる。
    /// </summary>
    private void ApplyOrientation()
    {
        Quat q = _reference is { } r ? (r.Conjugate() * _lastQ).Normalized() : _lastQ;
        _boardTransform.Matrix = CoordinateMapping.BodyToWpf(q);
    }

    // =====================================================================
    // 毎フレーム（UI スレッド）：受信スレッドが溜めたデータを取り込む
    // =====================================================================

    private void OnRendering(object? sender, EventArgs e)
    {
        var reader = _reader;
        if (reader is null)
            return;

        for (int i = 0; i < MaxMessagesPerFrame && reader.Messages.TryDequeue(out var msg); i++)
            AddLog(msg);

        if (reader.TryTakeLatest(out var s))
        {
            _lastQ = s.Q;
            bool first = !_hasSample;
            _hasSample = true;

            RollText.Text = FormatAngle(s.Roll);
            PitchText.Text = FormatAngle(s.Pitch);
            YawText.Text = FormatAngle(s.Yaw);
            QuatText.Text = $"{s.Q.W,7:F4} {s.Q.X,7:F4}\n{s.Q.Y,7:F4} {s.Q.Z,7:F4}";

            ApplyOrientation();
            if (first)
                UpdateUiState();
        }

        // 受信レート（0.5 秒ごと）
        double elapsed = _rateWatch.Elapsed.TotalSeconds;
        if (elapsed >= 0.5)
        {
            long count = reader.SampleCount;
            double hz = (count - _rateLastCount) / elapsed;
            _rateLastCount = count;
            _rateWatch.Restart();
            RateText.Text = $"{hz,5:F1} Hz   受信 {count}   不正行 {reader.BadLineCount}";
        }
    }

    private static string FormatAngle(double deg) => $"{deg,7:F1}°";

    // =====================================================================
    // 接続・切断
    // =====================================================================

    private void RefreshPorts()
    {
        string? current = PortCombo.SelectedItem as string;
        var ports = SerialPort.GetPortNames()
            .Distinct()
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PortCombo.ItemsSource = ports;
        if (current is not null && ports.Contains(current))
            PortCombo.SelectedItem = current;
        else if (ports.Count > 0)
            PortCombo.SelectedIndex = ports.Count - 1; // 後から挿した USB 機器は番号が大きいことが多い
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reader is null)
            Connect();
        else
            Disconnect("切断しました");
    }

    private void Connect()
    {
        if (PortCombo.SelectedItem is not string port)
        {
            StatusText.Text = "COM ポートを選んでください";
            return;
        }

        var reader = new SerialReader(port);
        reader.Faulted += ex =>
            Dispatcher.InvokeAsync(() =>
            {
                // 受信スレッドからの通知。今の接続のものだけ処理する
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
            StatusText.Text = $"{port} を開けませんでした：{ex.Message}";
            AddLog($"[PC] {port} を開けませんでした：{ex.Message}");
            return;
        }

        _reader = reader;
        _hasSample = false;
        _reference = null;   // 基板側のフィルタがやり直しになっている可能性があるので基準は消す
        _rateLastCount = 0;
        _rateWatch.Restart();
        StatusText.Text = $"{port} に接続（115200 bps）";
        AddLog($"[PC] {port} に接続しました");
        UpdateReferenceText();
        UpdateUiState();
    }

    private void Disconnect(string reason)
    {
        var reader = _reader;
        if (reader is null)
            return;

        _reader = null;
        reader.Dispose();

        // 残っていたメッセージを吐き出しておく
        while (reader.Messages.TryDequeue(out var msg))
            AddLog(msg);

        StatusText.Text = reason;
        RateText.Text = "";
        AddLog($"[PC] {reason}");
        UpdateUiState();
    }

    // =====================================================================
    // 姿勢リセット・零点校正
    // =====================================================================

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasSample)
            return;
        _reference = _lastQ;
        ApplyOrientation();
        UpdateReferenceText();
        AddLog("[PC] 姿勢リセット：今の姿勢を基準（水平・正面）にしました");
    }

    private void CalibButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reader is null)
            return;
        try
        {
            _reader.Send("c");
            AddLog("[PC] 零点校正コマンド 'c' を送信（基板を静止させてください）");
        }
        catch (Exception ex)
        {
            AddLog($"[PC] 送信に失敗しました：{ex.Message}");
        }
    }

    // =====================================================================
    // 補助
    // =====================================================================

    private void UpdateUiState()
    {
        bool connected = _reader is not null;
        ConnectButton.Content = connected ? "切断" : "接続";
        PortCombo.IsEnabled = !connected;
        RefreshButton.IsEnabled = !connected;
        CalibButton.IsEnabled = connected;
        ResetButton.IsEnabled = connected && _hasSample;
    }

    private void UpdateReferenceText()
    {
        ReferenceText.Text = _reference is null
            ? "基準：未設定（地上座標で表示。ヨーは成り行き）"
            : "基準：姿勢リセット時の姿勢";
    }

    private void AddLog(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (_log.Count > MaxLogLines)
            _log.RemoveAt(0);
        LogList.ScrollIntoView(_log[^1]);
    }

    private static Vector3D Normalize(Vector3D v)
    {
        v.Normalize();
        return v;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        Disconnect("終了");
    }
}
