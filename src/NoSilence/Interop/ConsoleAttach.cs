using System.Text;

namespace NoSilence.Interop;

/// <summary>
/// NoSilence is a <c>WinExe</c>, so it starts with no console. The diagnostic commands
/// (<c>--list-devices</c>, <c>--diagnose</c>, <c>--replay</c>) need one. Attaching to the
/// parent console makes output land in the shell that launched us — including when it is
/// redirected to a file — and we only allocate a fresh window if there is nothing to attach to.
/// </summary>
internal static class ConsoleAttach
{
    private static bool _allocated;
    private static bool _ensured;

    public static void EnsureConsole()
    {
        if (_ensured)
        {
            return;
        }

        _ensured = true;

        // Already redirected (`NoSilence --list-devices > out.txt`, or launched with piped
        // handles): we have a perfectly good stdout. Attaching a console here would steal
        // the output back from the file the user asked for.
        if (Console.IsOutputRedirected)
        {
            return;
        }

        if (NativeMethods.GetConsoleWindow() == 0)
        {
            if (!NativeMethods.AttachConsole(NativeMethods.AttachParentProcess))
            {
                _allocated = NativeMethods.AllocConsole();
            }
        }

        // The Console class caches the (invalid) streams it captured at startup, so they
        // have to be rebuilt against the handles we just acquired.
        RebindStreams();
    }

    /// <summary>
    /// Keeps a console we allocated ourselves open until the user acknowledges it —
    /// otherwise the window vanishes with the process and the output is unreadable.
    /// </summary>
    public static void PauseIfOwnConsole()
    {
        if (!_allocated)
        {
            return;
        }

        Console.WriteLine();
        Console.Write("Press Enter to close...");
        Console.ReadLine();
    }

    private static void RebindStreams()
    {
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(stdout);

            var stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
            Console.SetError(stderr);

            Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
        }
        catch (IOException)
        {
            // No usable console (service context, detached process). Callers degrade to logging.
        }
    }
}
