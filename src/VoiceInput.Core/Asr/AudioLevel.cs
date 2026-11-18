namespace VoiceInput.Core.Asr;

/// <summary>
/// 把一段 16 位单声道 PCM 换算成 0~1 的音量，供状态条画波形用。
///
/// 用 dBFS 而不是线性 RMS：人耳是对数的，线性值在正常说话时长期贴着 0.05，波形
/// 看着像没动。-60 dBFS 以下算静音，-6 dBFS 以上算满格。
/// </summary>
public static class AudioLevel
{
    public const double FloorDb = -60;
    public const double CeilingDb = -6;

    /// <summary>静音时的条高增益：条子还留着，用户才看得出“正在录”。</summary>
    public const double SilenceGain = 0.15;

    /// <summary>
    /// 增益曲线的 gamma。小于 1 是专门抬高低音区：正常说话的 level 只有 0.5 上下，
    /// 线性映射会让波形长期贴在圆的下半截，看起来像“只有很大声才会跳”。
    /// </summary>
    public const double DisplayGamma = 0.6;

    public static float FromPcm16Le(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2)
        {
            return 0;
        }

        double sum = 0;
        var samples = 0;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | (pcm[index + 1] << 8));
            sum += (double)sample * sample;
            samples++;
        }

        return samples == 0 ? 0 : ToNormalized(Math.Sqrt(sum / samples));
    }

    public static float ToNormalized(double rms)
    {
        if (rms <= 0)
        {
            return 0;
        }

        var db = 20 * Math.Log10(rms / 32768.0);
        var ratio = (db - FloorDb) / (CeilingDb - FloorDb);
        return (float)Math.Clamp(ratio, 0, 1);
    }

    /// <summary>
    /// 电平 → 状态条竖条的高度增益（0.15~1）。单独抽出来是为了能直接测：
    /// 曲线形状一变，状态条的观感就跟着变，靠手工截图看是看不出拐点的。
    /// </summary>
    public static float ToDisplayGain(float level)
    {
        var clamped = Math.Clamp(level, 0f, 1f);
        return (float)(SilenceGain + ((1 - SilenceGain) * Math.Pow(clamped, DisplayGamma)));
    }
}
