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

    public void Play(string path, int volume, int priority)
    {
        lock (gate)
        {
            StopInternal();
            try
            {
                reader = new AudioFileReader(path) { Volume = Math.Clamp(volume, 0, 100) / 100f };
                output = new WaveOutEvent();
                output.Init(reader);
                output.Play();
                currentPriority = priority;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Couldn't play audio file: {path}");
                StopInternal();
            }
        }
    }

    public void Stop()
    {
        lock (gate)
            StopInternal();
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
