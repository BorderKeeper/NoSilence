using NoSilence.Audio;

namespace NoSilence.Tests;

public class DeviceRetryPolicyTests
{
    /// <summary>
    /// A missing device is the normal state of affairs when the TV is off. It must re-check
    /// at a steady interval rather than escalating, or turning the TV on after an hour would
    /// leave you waiting half a minute for music.
    /// </summary>
    [Fact]
    public void MissingDevice_RetriesAtAConstantInterval()
    {
        var policy = new DeviceRetryPolicy { MissingRetryMs = 5000 };

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(5000, policy.NextDelayAfterMissingDevice());
        }
    }

    [Fact]
    public void MissingDevice_DoesNotCountAsAFailure()
    {
        var policy = new DeviceRetryPolicy();

        policy.NextDelayAfterMissingDevice();
        policy.NextDelayAfterMissingDevice();

        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.Equal(policy.InitialBackoffMs, policy.CurrentBackoffMs);
    }

    [Fact]
    public void OpenFailure_BacksOffExponentiallyAndCaps()
    {
        var policy = new DeviceRetryPolicy { InitialBackoffMs = 1000, MaxBackoffMs = 30000 };

        Assert.Equal(1000, policy.NextDelayAfterOpenFailure());
        Assert.Equal(2000, policy.NextDelayAfterOpenFailure());
        Assert.Equal(4000, policy.NextDelayAfterOpenFailure());
        Assert.Equal(8000, policy.NextDelayAfterOpenFailure());
        Assert.Equal(16000, policy.NextDelayAfterOpenFailure());
        Assert.Equal(30000, policy.NextDelayAfterOpenFailure());
        Assert.Equal(30000, policy.NextDelayAfterOpenFailure());
    }

    [Fact]
    public void OpenFailure_NeverExceedsTheCapWhateverTheSettings()
    {
        var policy = new DeviceRetryPolicy { InitialBackoffMs = 7000, MaxBackoffMs = 10000 };

        for (int i = 0; i < 20; i++)
        {
            Assert.True(policy.NextDelayAfterOpenFailure() <= 10000);
        }
    }

    [Fact]
    public void Reset_ClearsTheBackoffSoTheNextOutageStartsFast()
    {
        var policy = new DeviceRetryPolicy();

        for (int i = 0; i < 6; i++)
        {
            policy.NextDelayAfterOpenFailure();
        }

        Assert.Equal(6, policy.ConsecutiveFailures);

        policy.Reset();

        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.Equal(policy.InitialBackoffMs, policy.NextDelayAfterOpenFailure());
    }

    /// <summary>
    /// Windows reports a freshly attached HDMI endpoint as Active before the driver will
    /// accept an Initialize call, so there has to be a non-zero settle delay — a zero here
    /// is the classic wake-up spin loop.
    /// </summary>
    [Fact]
    public void SettleDelays_AreNonZeroAndResumeIsTheLonger()
    {
        var policy = new DeviceRetryPolicy();

        Assert.True(policy.SettleMs > 0);
        Assert.True(policy.ResumeSettleMs >= policy.SettleMs);
    }
}
