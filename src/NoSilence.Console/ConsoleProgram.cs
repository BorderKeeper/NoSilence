namespace NoSilence.ConsoleHost;

/// <summary>
/// The console-subsystem entry point.
/// </summary>
/// <remarks>
/// Deliberately nothing but a stub. All behaviour lives in the tray build; this exists only
/// so the diagnostic commands have a process whose output a shell will wait for and display.
/// Run this for <c>--diagnose</c>, <c>--replay</c>, <c>--list-devices</c> and the television
/// commands; run <c>NoSilence.exe</c> for everything else.
/// </remarks>
internal static class ConsoleProgram
{
    [STAThread]
    private static int Main(string[] args) => NoSilence.Program.Run(args);
}
