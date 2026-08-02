namespace NoSilence.Tests;

public class CommandLineOptionsTests
{
    [Fact]
    public void NoArguments_StartsInTheTray()
    {
        Assert.True(CommandLineOptions.TryParse([], out CommandLineOptions options, out string? error));
        Assert.Null(error);
        Assert.Equal(AppCommand.Tray, options.Command);
        Assert.False(options.IsConsoleCommand);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void HelpIsRecognisedInEveryCommonSpelling(string arg)
    {
        Assert.True(CommandLineOptions.TryParse([arg], out CommandLineOptions options, out _));
        Assert.Equal(AppCommand.Help, options.Command);
    }

    [Fact]
    public void DiagnoseCollectsItsOptions()
    {
        Assert.True(CommandLineOptions.TryParse(
            ["--diagnose", "--seconds", "300", "--jsonl", @"C:\tmp\session.jsonl", "--play"],
            out CommandLineOptions options,
            out string? error));

        Assert.Null(error);
        Assert.Equal(AppCommand.Diagnose, options.Command);
        Assert.Equal(TimeSpan.FromSeconds(300), options.Duration);
        Assert.Equal(@"C:\tmp\session.jsonl", options.SnapshotPath);
        Assert.True(options.PlayWhileDiagnosing);
        Assert.True(options.IsConsoleCommand);
    }

    /// <summary>
    /// v1 printed "Wrong number of parameters provided" and then carried on to throw
    /// IndexOutOfRangeException. A missing value must be a clean, explained failure.
    /// </summary>
    [Fact]
    public void MissingValue_FailsWithAnExplanationInsteadOfCrashing()
    {
        Assert.False(CommandLineOptions.TryParse(["--seconds"], out _, out string? error));
        Assert.Contains("expects a value", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownArgument_IsRejectedRatherThanIgnored()
    {
        Assert.False(CommandLineOptions.TryParse(["--volume", "20"], out _, out string? error));
        Assert.Contains("--volume", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonNumericSeconds_IsRejected()
    {
        Assert.False(CommandLineOptions.TryParse(["--diagnose", "--seconds", "soon"], out _, out string? error));
        Assert.Contains("--seconds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayWithoutAPath_IsRejected()
    {
        Assert.False(CommandLineOptions.TryParse(["--replay"], out _, out string? error));
        Assert.NotNull(error);
    }
}
