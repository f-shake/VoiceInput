using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceInput.Core.Config;
using VoiceInput.Core.Pipeline;

namespace VoiceInput.App.Platform;

/// <summary>
/// 三种正弦短音。状态条被别的窗口挡住时，声音是唯一还能提示“它听见了”的通道，
/// 所以开始音比 Python 版的 60 毫秒长得多。
/// </summary>
public sealed class ToneFeedback : IFeedback
{
    private const int SampleRate = 44100;
    private const double Gain = 0.2;

    private readonly Func<bool> _enabled;

    /// <summary>开关做成取值的委托：设置在运行中被改掉时不用重建这个对象。</summary>
    public ToneFeedback(Func<bool> enabled) => _enabled = enabled;

    public void Play(ToneKind kind)
    {
        if (!_enabled())
        {
            return;
        }

        var (frequency, milliseconds) = kind switch
        {
            ToneKind.Start => (1200, 200),
            ToneKind.Stop => (800, 120),
            _ => (400, 300),
        };

        Task.Run(() =>
        {
            try
            {
                PlayTone(frequency, milliseconds);
            }
            catch (Exception)
            {
                // 没声卡或被静音的机器不该让听写失败。
            }
        });
    }

    private static void PlayTone(int frequency, int milliseconds)
    {
        var generator = new SignalGenerator(SampleRate, 1)
        {
            Frequency = frequency,
            Gain = Gain,
            Type = SignalGeneratorType.Sin,
        };

        var limited = generator.Take(TimeSpan.FromMilliseconds(milliseconds));
        var provider = new SampleToWaveProvider16(limited);

        using var output = new WaveOutEvent();
        using var finished = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => finished.Set();
        output.Init(provider);
        output.Play();
        finished.Wait(TimeSpan.FromMilliseconds(milliseconds + 500));
    }
}
