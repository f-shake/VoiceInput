using VoiceInput.Core.Asr;

namespace VoiceInput.Core.Tests;

public sealed class AudioLevelTests
{
    [Fact]
    public void 全零的PCM算静音()
    {
        Assert.Equal(0f, AudioLevel.FromPcm16Le(new byte[320]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 空缓冲或不足一个采样返回零(int length)
    {
        Assert.Equal(0f, AudioLevel.FromPcm16Le(new byte[length]));
    }

    [Fact]
    public void 满幅正弦接近满格()
    {
        var pcm = Sine(amplitude: 30000, samples: 1600);

        var level = AudioLevel.FromPcm16Le(pcm);

        Assert.True(level > 0.95f, $"实际 {level}");
    }

    [Fact]
    public void 中等音量落在区间中间()
    {
        var pcm = Sine(amplitude: 3000, samples: 1600);

        var level = AudioLevel.FromPcm16Le(pcm);

        Assert.InRange(level, 0.55f, 0.8f);
    }

    [Fact]
    public void 低于门限算静音()
    {
        // 20 的 RMS 约 -64 dBFS，低于 -60 的门限。
        Assert.Equal(0f, AudioLevel.ToNormalized(20));
    }

    [Fact]
    public void 超过上限截到一()
    {
        Assert.Equal(1f, AudioLevel.ToNormalized(40000));
    }

    [Fact]
    public void 显示增益在静音时保留底值()
    {
        Assert.Equal(0.15f, AudioLevel.ToDisplayGain(0));
    }

    [Fact]
    public void 显示增益在满格时饱和到一()
    {
        Assert.Equal(1f, AudioLevel.ToDisplayGain(1));
    }

    [Fact]
    public void 显示增益随电平单调不减()
    {
        var previous = -1f;
        for (var step = 0; step <= 100; step++)
        {
            var level = step / 100f;
            var gain = AudioLevel.ToDisplayGain(level);
            Assert.True(gain >= previous, $"level={level} 时增益反而变小了");
            previous = gain;
        }
    }

    [Fact]
    public void 显示增益抬高了正常说话的音量区()
    {
        // 旧曲线 0.15 + 0.85 × level：0.25 → 0.363、0.56 → 0.626。
        Assert.True(AudioLevel.ToDisplayGain(0.25f) > 0.5f);
        Assert.InRange(AudioLevel.ToDisplayGain(0.56f), 0.74f, 0.76f);
    }

    [Fact]
    public void 显示增益钳制越界电平()
    {
        Assert.Equal(0.15f, AudioLevel.ToDisplayGain(-1));
        Assert.Equal(1f, AudioLevel.ToDisplayGain(2));
    }

    private static byte[] Sine(short amplitude, int samples)
    {
        var bytes = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            var value = (short)(amplitude * Math.Sin(2 * Math.PI * index / 40.0));
            bytes[index * 2] = (byte)(value & 0xFF);
            bytes[(index * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }
}
