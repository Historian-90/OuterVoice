using System;
using NAudio.Wave;

namespace OuterVoice;

/// <summary>
/// A simple peak limiter: quickly turns down anything louder than the ceiling and eases back
/// afterwards. Used on friends' clips so a badly mastered (or deliberately loud) recording
/// can't blast your ears.
/// </summary>
public sealed class LoudnessLimiter : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly float ceiling;
    private readonly float release;
    private float gain = 1f;

    public LoudnessLimiter(ISampleProvider source, float ceiling = 0.5f)
    {
        this.source = source;
        this.ceiling = ceiling;
        // Recover about 63% of the way back to full volume in ~150 ms.
        release = 1f - MathF.Exp(-1f / (0.15f * source.WaveFormat.SampleRate * source.WaveFormat.Channels));
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        for (var i = offset; i < offset + read; i++)
        {
            var level = MathF.Abs(buffer[i]) * gain;
            if (level > ceiling)
                gain = ceiling / MathF.Abs(buffer[i]);     // instant attack
            else
                gain += (1f - gain) * release;             // smooth release

            buffer[i] *= gain;
        }
        return read;
    }
}
