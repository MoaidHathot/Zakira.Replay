using System.Runtime.InteropServices;

namespace Zakira.Replay.Cli;

/// <summary>
/// Minimal ANSI text styling for human-readable CLI output.
///
/// The tool is deliberately dependency-light (see the aggressive runtime trimming in the
/// csproj), so instead of pulling in a console-UI framework we emit a small, well-understood
/// subset of SGR escape codes directly. Styling auto-disables when:
/// <list type="bullet">
///   <item>stdout is redirected or piped (so files/pipes stay free of escape codes),</item>
///   <item>the <c>NO_COLOR</c> environment variable is set (see https://no-color.org), or</item>
///   <item>the Windows console cannot be switched into virtual-terminal mode — otherwise the
///   escape codes would render as literal garbage such as <c>?[32m</c>.</item>
/// </list>
/// Each helper takes the resolved <paramref name="color"/> flag so rendering stays a pure
/// function of its inputs and unit tests can force color on or off deterministically.
/// </summary>
internal static class ConsoleStyle
{
    private const string Reset = "\u001b[0m";

    /// <summary>
    /// Whether the current process stdout can render ANSI colors. Evaluated once; the
    /// evaluation also switches the Windows console into virtual-terminal mode as a side effect.
    /// </summary>
    public static bool ColorEnabled { get; } = DetectColorSupport();

    public static string Green(string text, bool color) => Wrap(text, "32", color);

    public static string Red(string text, bool color) => Wrap(text, "31", color);

    public static string Yellow(string text, bool color) => Wrap(text, "33", color);

    public static string Cyan(string text, bool color) => Wrap(text, "36", color);

    public static string Bold(string text, bool color) => Wrap(text, "1", color);

    public static string Dim(string text, bool color) => Wrap(text, "2", color);

    private static string Wrap(string text, string sgr, bool color)
        => color && !string.IsNullOrEmpty(text) ? $"\u001b[{sgr}m{text}{Reset}" : text;

    private static bool DetectColorSupport()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        try
        {
            if (Console.IsOutputRedirected)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || TryEnableWindowsVirtualTerminal();
    }

    // --- Windows: enable ENABLE_VIRTUAL_TERMINAL_PROCESSING so ANSI codes render in conhost. ---

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    private static bool TryEnableWindowsVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == nint.Zero || handle == new nint(-1))
            {
                return false;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            if ((mode & EnableVirtualTerminalProcessing) != 0)
            {
                return true;
            }

            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
