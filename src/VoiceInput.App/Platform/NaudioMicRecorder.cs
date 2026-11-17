using NAudio.Wave;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Config;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;

namespace VoiceInput.App.Platform;

/// <summary>
/// 16k 单声道 16 位采集，按块累积，到上限截断，最后包一层 WAV 头交给 ASR。
/// 设备不接受 16k 时直接报错而不是自己重采样：重采样那条路径没有测试覆盖，
/// 出错时的现象比直接报错难查得多。
/// </summary>
public sealed class NaudioMicRecorder : IRecorder
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BytesPerSecond = SampleRate * BitsPerSample / 8 * Channels;

    private readonly AppConfig _config;

    /// <summary>
    /// 最近一块音频的音量（0~1）。状态条每帧来取一次，用来画随声音起伏的波形——
    /// 这是“麦克风真的在收音”的唯一可信证据，所以必须是实测值，不能自己编动画。
    /// </summary>
    private volatile float _level;

    public NaudioMicRecorder(AppConfig config) => _config = config;

    /// <summary>0 表示安静或没在录。</summary>
    public float Level => _level;

    public Recording Record(CancellationToken stopToken)
    {
        _level = 0;
        // -1 才是 WAVE_MAPPER（系统默认设备）；0 是 0 号设备，
        // 大多数机器上都不是用户以为的那个麦克风。
        var deviceNumber = _config.DeviceNumber ?? -1;
        var maxBytes = BytesPerSecond * _config.MaxSeconds;
        var buffer = new MemoryStream(BytesPerSecond * 2);
        using var stopped = new ManualResetEventSlim(false);

        using var capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3,
        };

        capture.DataAvailable += (_, args) =>
        {
            // 音量先在采集线程上算好：状态条那边每 33 毫秒取一次，不能让它去遍历 PCM。
            _level = AudioLevel.FromPcm16Le(args.Buffer.AsSpan(0, args.BytesRecorded));
            lock (buffer)
            {
                if (buffer.Length < maxBytes)
                {
                    buffer.Write(args.Buffer, 0, args.BytesRecorded);
                }
            }
        };
        Exception? stopError = null;
        capture.RecordingStopped += (_, args) =>
        {
            stopError = args.Exception;
            stopped.Set();
        };

        try
        {
            capture.StartRecording();
        }
        catch (Exception ex)
        {
            throw new VoiceInputException(
                ErrorCodes.DeviceUnavailable,
                ErrorCatalog.DeviceUnavailable,
                detail: $"设备 {deviceNumber} 打不开：{ex.Message}",
                inner: ex);
        }

        var truncated = false;
        while (!stopToken.IsCancellationRequested)
        {
            lock (buffer)
            {
                if (buffer.Length >= maxBytes)
                {
                    truncated = true;
                    break;
                }
            }

            stopToken.WaitHandle.WaitOne(20);
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception)
        {
            // 已经拿到数据了就继续，停止失败不该让整段录音作废。
        }

        stopped.Wait(TimeSpan.FromSeconds(2));
        _level = 0;

        if (stopError is not null)
        {
            // 设备掉线时 WinMM 会把异常塞进 StoppedEventArgs；继续上传半截音频
            // 只会得到一句莫名其妙的“没有识别到语音内容”。
            throw new VoiceInputException(
                ErrorCodes.RecordingInterrupted,
                ErrorCatalog.RecordingInterrupted,
                detail: stopError.Message,
                inner: stopError);
        }

        byte[] pcm;
        lock (buffer)
        {
            pcm = buffer.ToArray();
        }

        return new Recording(WrapWav(pcm), pcm.Length / (double)BytesPerSecond, truncated);
    }

    private static byte[] WrapWav(byte[] pcm)
    {
        using var wav = new MemoryStream(pcm.Length + 44);
        using (var writer = new BinaryWriter(wav, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var byteRate = BytesPerSecond;
            writer.Write("RIFF"u8);
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(byteRate);
            writer.Write((short)(Channels * BitsPerSample / 8));
            writer.Write((short)BitsPerSample);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }

        return wav.ToArray();
    }

    /// <summary>日志里列可用设备用；打不开设备时靠它让人知道该改哪个编号。</summary>
    public static IReadOnlyList<string> DescribeDevices()
    {
        var lines = new List<string>();
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            try
            {
                var caps = WaveInEvent.GetCapabilities(index);
                lines.Add($"{index}: {caps.ProductName}（{caps.Channels} 声道）");
            }
            catch (Exception ex)
            {
                lines.Add($"{index}: 读取设备信息失败，{ex.Message}");
            }
        }

        return lines;
    }
}
