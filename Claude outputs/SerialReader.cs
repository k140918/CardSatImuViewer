using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;

namespace CardSatImuViewer;

/// <summary>
/// シリアルポートを専用スレッドで読み、最新の IMU データと '#' メッセージを保持する。
/// UI 側は TryTakeLatest / Messages を UI スレッドから取りにくる（このクラスは UI に触らない）。
/// </summary>
public sealed class SerialReader : IDisposable
{
    private const int MaxQueuedMessages = 500;

    private readonly SerialPort _port;
    private readonly object _sampleLock = new();
    private readonly object _writeLock = new();
    private Thread? _thread;
    private volatile bool _running;
    private ImuSample? _latest;
    private long _sampleCount;
    private long _badLineCount;

    /// <summary>'#' で始まる行など、ログに出すメッセージ</summary>
    public ConcurrentQueue<string> Messages { get; } = new();

    /// <summary>受信中にポートが使えなくなったとき（受信スレッドから呼ばれる）</summary>
    public event Action<Exception>? Faulted;

    public string PortName => _port.PortName;
    public long SampleCount => Interlocked.Read(ref _sampleCount);
    public long BadLineCount => Interlocked.Read(ref _badLineCount);

    public SerialReader(string portName, int baudRate = 115200)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            NewLine = "\n",
            Encoding = Encoding.UTF8,   // 基板の # メッセージは日本語（UTF-8）
            ReadTimeout = 500,
            WriteTimeout = 500,
            // USB CDC の実装によっては DTR が立っていないと基板側が送信しないため有効にする
            DtrEnable = true,
            RtsEnable = true,
        };
    }

    public void Start()
    {
        _port.Open();
        _port.DiscardInBuffer();
        _running = true;
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "SerialReader",
        };
        _thread.Start();
    }

    private void ReadLoop()
    {
        while (_running)
        {
            string line;
            try
            {
                line = _port.ReadLine();
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex)
            {
                // Dispose によるポートクローズなら正常終了、それ以外（ケーブル抜けなど）は通知
                if (_running)
                {
                    _running = false;
                    Faulted?.Invoke(ex);
                }
                break;
            }

            ProcessLine(line.Trim());
        }
    }

    private void ProcessLine(string line)
    {
        if (line.Length == 0)
            return;

        if (line[0] == '#')
        {
            EnqueueMessage(line);
            return;
        }

        if (ImuSample.TryParse(line, out var sample))
        {
            lock (_sampleLock)
                _latest = sample;
            Interlocked.Increment(ref _sampleCount);
            return;
        }

        Interlocked.Increment(ref _badLineCount);
        // 形式外の行も、何が来ているか分かるようにログへ出す（大量に出る場合は間引かれる）
        if (!line.StartsWith("Q,", StringComparison.Ordinal))
            EnqueueMessage("? " + line);
    }

    private void EnqueueMessage(string message)
    {
        Messages.Enqueue(message);
        while (Messages.Count > MaxQueuedMessages && Messages.TryDequeue(out _)) { }
    }

    /// <summary>前回呼び出し以降に新しいデータが届いていれば、その最新値を返す</summary>
    public bool TryTakeLatest(out ImuSample sample)
    {
        lock (_sampleLock)
        {
            if (_latest is { } s)
            {
                sample = s;
                _latest = null;
                return true;
            }
        }
        sample = default;
        return false;
    }

    /// <summary>基板へ文字列を送る（零点校正なら "c"）</summary>
    public void Send(string text)
    {
        lock (_writeLock)
            _port.Write(text);
    }

    public void Dispose()
    {
        _running = false;
        try { _port.Close(); } catch { /* 既に切断されている場合など */ }
        _thread?.Join(1000);
        _port.Dispose();
    }
}
