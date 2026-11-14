using SherpaOnnx;
using VoiceInput.Core.Asr;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;
using VoiceInput.Core.Text;

namespace VoiceInput.Providers.SenseVoice;

/// <summary>
/// 本地 SenseVoice 离线识别（sherpa-onnx 的 ONNX 版本，不需要 Python）。
///
/// 模型只在第一次用到时加载：加载要一两秒、还要占几百兆内存，程序常驻但不一定
/// 每次启动都会听写，提前加载纯粹是拖慢启动。
/// </summary>
public sealed class SenseVoiceTranscriber : ITranscriber, IDisposable
{
    private readonly SenseVoiceOptions _options;
    private readonly object _gate = new();
    private OfflineRecognizer? _recognizer;

    public SenseVoiceTranscriber(SenseVoiceOptions options) => _options = options;

    public Task<AsrResult> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken) =>
        Task.Run(() => Transcribe(wavBytes, cancellationToken), cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    private AsrResult Transcribe(byte[] wavBytes, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var (rate, samples) = Pcm16Wave.Read(wavBytes);

        if (samples.Length == 0)
        {
            return new AsrResult(string.Empty, 0, "sensevoice-local", 0);
        }

        string text;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognizer = EnsureRecognizer();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(rate, samples);
            recognizer.Decode(stream);
            text = stream.Result.Text ?? string.Empty;
        }

        return new AsrResult(
            TextNormalizer.Normalize(text),
            (int)(samples.Length * 1000.0 / rate),
            "sensevoice-local",
            (DateTime.UtcNow - started).TotalSeconds);
    }

    private OfflineRecognizer EnsureRecognizer()
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        if (!Directory.Exists(_options.ModelDirectory))
        {
            throw new VoiceInputException(
                ErrorCodes.LocalModelMissing,
                ErrorCatalog.LocalModelMissing,
                detail: $"SenseVoice 模型目录不存在：{_options.ModelDirectory}");
        }

        if (!File.Exists(_options.ModelPath))
        {
            throw new VoiceInputException(
                ErrorCodes.LocalModelMissing,
                ErrorCatalog.LocalModelMissing,
                detail: $"没有找到模型文件 {_options.ModelPath}");
        }

        if (!File.Exists(_options.TokensPath))
        {
            throw new VoiceInputException(
                ErrorCodes.LocalModelMissing,
                ErrorCatalog.LocalModelMissing,
                detail: $"没有找到 tokens 文件 {_options.TokensPath}");
        }

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.ModelConfig.Tokens = _options.TokensPath;
        config.ModelConfig.NumThreads = _options.NumThreads;
        config.ModelConfig.Provider = _options.Provider;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.SenseVoice.Model = _options.ModelPath;
        config.ModelConfig.SenseVoice.Language = _options.Language;
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = _options.UseInverseTextNormalization ? 1 : 0;

        _recognizer = new OfflineRecognizer(config);
        return _recognizer;
    }
}

/// <summary>插件参数，全部来自配置里的 providerOptions。</summary>
public sealed record SenseVoiceOptions(
    string ModelDirectory,
    string ModelPath,
    string TokensPath,
    int NumThreads,
    string Provider,
    string Language,
    bool UseInverseTextNormalization);

/// <summary>
/// 只认 16 位 PCM 的 WAV。我们自己录出来的一定是这种格式；遇到别的就报错，
/// 免得把浮点采样当整数解出噪声还以为是模型不准。
/// </summary>
internal static class Pcm16Wave
{
    public static (int Rate, float[] Samples) Read(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F')
        {
            throw new VoiceInputException(
                ErrorCodes.AudioRejected,
                ErrorCatalog.AudioRejected,
                detail: "不是 RIFF/WAV 数据");
        }

        var rate = 16000;
        var channels = 1;
        var bitsPerSample = 16;
        var dataOffset = -1;
        var dataLength = 0;

        var cursor = 12;
        while (cursor + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, cursor, 4);
            var size = BitConverter.ToInt32(wav, cursor + 4);
            var body = cursor + 8;

            if (id == "fmt " && body + 16 <= wav.Length)
            {
                var format = BitConverter.ToInt16(wav, body);
                channels = BitConverter.ToInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4);
                bitsPerSample = BitConverter.ToInt16(wav, body + 14);
                if (format != 1 || bitsPerSample != 16)
                {
                    throw new VoiceInputException(
                        ErrorCodes.AudioRejected,
                        ErrorCatalog.AudioRejected,
                        detail: $"只支持 16 位 PCM，实际 format={format} bits={bitsPerSample}");
                }
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = Math.Min(size, wav.Length - body);
                break;
            }

            if (size <= 0)
            {
                break;
            }

            cursor = body + size + (size % 2);
        }

        if (dataOffset < 0 || dataLength <= 0)
        {
            throw new VoiceInputException(
                ErrorCodes.AudioRejected,
                ErrorCatalog.AudioRejected,
                detail: "WAV 里没有 data 块");
        }

        var frameCount = dataLength / (2 * channels);
        var samples = new float[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            // 多声道只取第一声道：识别只需要一路。
            samples[i] = BitConverter.ToInt16(wav, dataOffset + (i * 2 * channels)) / 32768f;
        }

        return (rate, samples);
    }
}
