using System;
using NAudio.Wave;

namespace OuterVoice;

/// <summary>
/// Plays clips with NAudio (the audio library most Dalamud plugins use).
/// Supports .mp3 and .wav. One clip at a time; a new clip stops the old one.
/// </summary>
public sealed class VoicePlayer : IDisposable
{
    private readonly object gate = new();
    private WaveOutEvent? output;
    private AudioFileReader? reader;
    private int currentPriority;

    public bool IsPlaying
    {
        get
        {
            lock (gate)
                return output?.PlaybackState == PlaybackState.Playing;
        }
    }

    /// <summary>Priority of the line currently playing (0 when idle).</summary>
    public int CurrentPriority => IsPlaying ? currentPriority : 0;

    /// <summary>Plays a clip (replacing anything playing). Returns its length, or zero if it failed.</summary>
    /// <param name="maxLength">Refuse clips longer than this (friends' packs), or null for no limit.</param>
    /// <param name="limitLoudness">Run the clip through a peak limiter.</param>
    public TimeSpan Play(string path, int volume, int priority, TimeSpan? maxLength = null, bool limitLoudness = false)
    {
        lock (gate)
        {
            StopInternal();
            try
            {
                reader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0, 100) / 100f };
                if (maxLength.HasValue && reader.TotalTime > maxLength.Value)
                {
                    Plugin.Log.Info($"Skipped a {reader.TotalTime.TotalSeconds:0}s clip (limit {maxLength.Value.TotalSeconds:0}s): {path}");
                    StopInternal();
                    return TimeSpan.Zero;
                }

                output = new WaveOutEvent();
                ISampleProvider samples = reader;
                if (limitLoudness)
                    samples = new LoudnessLimiter(samples);
                output.Init(samples);
                output.Play();
                currentPriority = priority;
                return reader.TotalTime;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Couldn't play audio file: {path}");
                StopInternal();
                return TimeSpan.Zero;
            }
        }
    }

    public void Stop()
    {
        lock (gate)
            StopInternal();
        Plugin.Instance?.Lips.Stop();
    }

    private void StopInternal()
    {
        output?.Stop();
        output?.Dispose();
        reader?.Dispose();
        output = null;
        reader = null;
        currentPriority = 0;
    }

    public void Dispose() => Stop();
}
