using System.Text;

namespace RepoHarness.Cli;

/// <summary>Makes standard output and standard error UTF-8 for the life of the process.</summary>
/// <remarks>
/// Anchor statuses are emoji, and paths and registry text can hold any character. Linux and macOS
/// already write UTF-8; the Windows console's default code page cannot represent these characters,
/// so they would print as question marks on Windows alone. The console's own encoding is put back on
/// exit, because the code page belongs to the console window, which outlives this process.
/// </remarks>
internal sealed class ConsoleEncoding : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Encoding? _original;

    private ConsoleEncoding(Encoding? original)
    {
        _original = original;
    }

    internal static ConsoleEncoding UseUtf8()
    {
        try
        {
            var original = Console.OutputEncoding;
            Console.OutputEncoding = Utf8NoBom;
            return new ConsoleEncoding(original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // There is no console to configure, as for a process started without one. The streams
            // are still written as UTF-8, directly.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true });
            return new ConsoleEncoding(null);
        }
    }

    public void Dispose()
    {
        if (_original is null)
        {
            return;
        }

        try
        {
            Console.OutputEncoding = _original;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The console went away first; there is nothing left to restore.
        }
    }
}
