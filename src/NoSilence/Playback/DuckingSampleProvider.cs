using NAudio.Wave;

namespace NoSilence.Playback;

/// <summary>
/// Applies the detection engine's gain as a sample-accurate ramp.
/// </summary>
/// <remarks>
/// This is what replaces v1's <c>Pause()</c> / <c>Play()</c>. Two reasons it matters:
/// <list type="bullet">
/// <item>A 400 ms fade at 44.1 kHz is 17,640 discrete steps, so there is no click and no
/// zipper noise — an abrupt gain change is audible, a ramp is not.</item>
/// <item>The output device never changes transport state, so it is never in a position
/// where <c>Play()</c> has to be called on a stopped device. v1 did exactly that, once
/// every 500 ms, forever, whenever a track ended.</item>
/// </list>
/// </remarks>
internal sealed class DuckingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _sampleRate;

    private float _currentGain = 1f;
    private volatile float _targetGain = 1f;
    private volatile int _fadeMs;

    public DuckingSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sampleRate = source.WaveFormat.SampleRate;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The gain actually being applied right now. Moves towards the target each frame.</summary>
    public float CurrentGain => _currentGain;

    public float TargetGain => _targetGain;

    public bool IsAtTarget => Math.Abs(_currentGain - _targetGain) < 0.0005f;

    /// <summary>
    /// Requests a new gain. Safe to call from any thread; the ramp happens on the audio
    /// thread. A <paramref name="fadeMs"/> of zero jumps immediately.
    /// </summary>
    public void SetTarget(float gain, int fadeMs)
    {
        _targetGain = Math.Clamp(gain, 0f, 1f);
        _fadeMs = Math.Max(0, fadeMs);
    }

    /// <summary>Sets the gain with no ramp at all — used when (re)opening a device.</summary>
    public void Reset(float gain)
    {
        _targetGain = Math.Clamp(gain, 0f, 1f);
        _fadeMs = 0;
        _currentGain = _targetGain;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0)
        {
            return 0;
        }

        float target = _targetGain;
        float current = _currentGain;

        // Fast path: unity gain and nothing to ramp towards, so leave the samples alone.
        if (current == 1f && target == 1f)
        {
            return read;
        }

        int channels = WaveFormat.Channels;
        int fadeMs = _fadeMs;
        float step = fadeMs <= 0
            ? float.MaxValue
            : 1f / (fadeMs / 1000f * _sampleRate);

        int end = offset + read;
        for (int i = offset; i < end;)
        {
            if (current < target)
            {
                current = Math.Min(target, current + step);
            }
            else if (current > target)
            {
                current = Math.Max(target, current - step);
            }

            // One gain value per frame, applied to every channel, so the stereo image
            // cannot drift while fading.
            for (int c = 0; c < channels && i < end; c++, i++)
            {
                buffer[i] *= current;
            }
        }

        _currentGain = current;
        return read;
    }
}
