namespace NoSilence.Detection;

/// <summary>Level conversions. Pure, and deliberately trivial so it can be trusted.</summary>
internal static class PeakMath
{
    /// <summary>Floor for the dBFS scale. Digital silence maps here rather than to -infinity.</summary>
    public const double MinDbfs = -100d;

    /// <summary>
    /// Converts a WASAPI peak (0..1) to dBFS.
    /// </summary>
    /// <remarks>
    /// Working in dB rather than raw amplitude is what makes a threshold meaningful. v1
    /// compared the peak against 0.0001f, which reads as a harmless-looking small number but
    /// is -80 dBFS — below the noise floor of dithered content, so in practice it meant
    /// "any non-zero sample at all".
    /// </remarks>
    public static double ToDbfs(double peak) => peak <= 0d
        ? MinDbfs
        : Math.Clamp(20d * Math.Log10(peak), MinDbfs, 0d);

    public static double FromDbfs(double dbfs) => dbfs <= MinDbfs ? 0d : Math.Pow(10d, dbfs / 20d);

    /// <summary>
    /// Smoothing factor for an exponential moving average with the given time constant.
    /// </summary>
    public static double EmaAlpha(int intervalMs, int timeConstantMs) =>
        timeConstantMs <= 0 ? 1d : 1d - Math.Exp(-(double)intervalMs / timeConstantMs);
}
