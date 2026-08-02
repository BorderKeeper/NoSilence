namespace NoSilence.Audio;

/// <summary>
/// Decides when to try the output device again. Pure and unit tested.
/// </summary>
/// <remarks>
/// Two failures that look alike but are not:
/// <list type="bullet">
/// <item><b>The device is absent.</b> Entirely expected — the TV is off. Retry at a steady,
/// modest interval and log nothing. Endpoint notifications normally beat the timer to it;
/// this is only a safety net for a notification that never arrives.</item>
/// <item><b>The device is there but would not open.</b> A real fault, so back off
/// exponentially. Windows fires a burst of notifications as an HDMI sink appears and the
/// endpoint is often not usable on the first one; opening too eagerly throws
/// AUDCLNT_E_DEVICE_IN_USE and is the classic cause of a wake-up spin loop.</item>
/// </list>
/// v1 had neither: it called Play() on a dead device every 500 ms, forever.
/// </remarks>
internal sealed class DeviceRetryPolicy
{
    /// <summary>Steady re-check while the device simply is not there.</summary>
    public int MissingRetryMs { get; init; } = 5000;

    public int InitialBackoffMs { get; init; } = 1000;

    public int MaxBackoffMs { get; init; } = 30000;

    /// <summary>
    /// How long to let a newly appeared endpoint settle before touching it. Windows reports
    /// the device as Active before the driver is ready to be initialised.
    /// </summary>
    public int SettleMs { get; init; } = 1500;

    /// <summary>Longer settle after waking from sleep, where the whole audio stack is re-enumerating.</summary>
    public int ResumeSettleMs { get; init; } = 3000;

    private int _backoffMs;

    /// <summary>Consecutive open failures since the last success. Surfaced in the UI.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>The delay that will be used by the next <see cref="NextDelayAfterOpenFailure"/>.</summary>
    public int CurrentBackoffMs => _backoffMs == 0 ? InitialBackoffMs : _backoffMs;

    public void Reset()
    {
        _backoffMs = 0;
        ConsecutiveFailures = 0;
    }

    /// <summary>Delay before looking for an absent device again. Constant, never escalating.</summary>
    public int NextDelayAfterMissingDevice() => MissingRetryMs;

    /// <summary>Delay after a device that exists refused to open. Doubles, capped.</summary>
    public int NextDelayAfterOpenFailure()
    {
        ConsecutiveFailures++;
        _backoffMs = _backoffMs == 0
            ? InitialBackoffMs
            : Math.Min(MaxBackoffMs, _backoffMs * 2);

        return _backoffMs;
    }
}
