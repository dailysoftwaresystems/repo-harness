using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RepoHarness.Tests;

/// <summary>
/// Behaviours this assembly performs when started as a child process. They give the
/// process tests a program whose timing, output and descendants are exactly known,
/// without depending on a shell or on any other tool being installed.
/// </summary>
internal static class TestChild
{
    /// <summary>
    /// Output is written as UTF-8 explicitly, as git writes it. The console's own encoding
    /// on Windows is a legacy code page, which would make a test of the runner's decoding
    /// measure this child instead.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static int Run(string mode, string[] arguments)
    {
        using var standardOutput = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true };
        using var standardError = new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true };

        return mode switch
        {
            "echo-args" => EchoArguments(standardOutput, arguments),
            "echo-stdin" => EchoStandardInput(standardOutput),
            "read-line-then-watch" => ReadLineThenWatch(standardOutput, arguments),
            "sleep" => Sleep(arguments),
            "stream" => Stream(standardOutput, standardError, arguments),
            "spawn-grandchild" => SpawnGrandchild(arguments),
            "print-env" => PrintEnvironment(standardOutput, arguments),
            "write-file" => WriteFile(arguments),
            "link-directory" => LinkDirectory(arguments),
            "exit" => int.Parse(arguments[0], CultureInfo.InvariantCulture),
            _ => 99,
        };
    }

    /// <summary>
    /// Writes <c>arguments[1]</c> into the file <c>arguments[0]</c> names, for a step that has to
    /// actually produce something.
    /// </summary>
    /// <remarks>
    /// Exits zero either way. A step that declares an output and does not write it is the case the
    /// harness has to notice by looking, so this child must be able to succeed at doing nothing.
    /// </remarks>
    private static int WriteFile(string[] arguments)
    {
        if (arguments.Length >= 2)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(arguments[0]) ?? ".");
            File.WriteAllText(arguments[0], arguments[1]);
        }

        // An optional exit code, so a step can produce exactly what it declared and still fail.
        // That is the case a caller has to tell apart from a step that produced nothing.
        return arguments.Length >= 3 ? int.Parse(arguments[2], CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Makes <c>arguments[0]</c> a directory holding one file and a link, <c>latest</c>, to the
    /// directory <c>arguments[1]</c>: what a step that keeps a directory of runs with a pointer to the
    /// newest one produces.
    /// </summary>
    private static int LinkDirectory(string[] arguments)
    {
        Directory.CreateDirectory(arguments[0]);
        File.WriteAllText(Path.Combine(arguments[0], "kept.txt"), "kept");
        Directory.CreateSymbolicLink(Path.Combine(arguments[0], "latest"), arguments[1]);
        return 0;
    }

    /// <summary>Writes each argument on its own line between brackets, so an empty one is visible.</summary>
    private static int EchoArguments(TextWriter output, string[] arguments)
    {
        foreach (var argument in arguments)
        {
            output.Write("[" + argument + "]\n");
        }

        return 0;
    }

    /// <summary>
    /// Reads standard input to its end and writes it back between brackets. Read as UTF-8 bytes,
    /// for the reason output is written that way.
    /// </summary>
    private static int EchoStandardInput(TextWriter output)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        output.Write("[" + input.ReadToEnd() + "]\n");
        return 0;
    }

    /// <summary>
    /// Reads one line and writes it back, then waits the given milliseconds for the end of its input:
    /// "ended" when the parent closed it, "held" when it was still open after the wait. The reader is
    /// deliberately left undisposed, since a read may still be pending when this process exits.
    /// </summary>
    private static int ReadLineThenWatch(TextWriter output, string[] arguments)
    {
        var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        output.Write("[" + input.ReadLine() + "]\n");

        var reading = Task.Run(input.Read);
        var ended = reading.Wait(int.Parse(arguments[0], CultureInfo.InvariantCulture)) && reading.Result < 0;

        output.Write(ended ? "ended\n" : "held\n");
        return 0;
    }

    private static int Sleep(string[] arguments)
    {
        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>
    /// Writes one line, then waits for the parent to prove it received that line while
    /// this process is still running. If output were delivered only at exit, the signal
    /// would never arrive, and the exit code says so.
    /// </summary>
    private static int Stream(TextWriter output, TextWriter error, string[] arguments)
    {
        var signal = arguments[0];

        output.Write("first\n");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(signal))
        {
            if (DateTime.UtcNow > deadline)
            {
                return 3;
            }

            Thread.Sleep(20);
        }

        error.Write("problem\n");
        output.Write("second\n");
        return 0;
    }

    /// <summary>Starts a sleeping grandchild, records its process id, then sleeps as well.</summary>
    private static int SpawnGrandchild(string[] arguments)
    {
        var processIdFile = arguments[0];
        var sleepMilliseconds = arguments[1];

        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add(sleepMilliseconds);
        start.Environment[TestHost.ChildModeVariable] = "sleep";

        using var grandchild = Process.Start(start)
            ?? throw new InvalidOperationException("The grandchild process did not start.");

        // Written to a temporary name and renamed, so the parent never reads a half-written id.
        var partial = processIdFile + ".partial";
        File.WriteAllText(partial, grandchild.Id.ToString(CultureInfo.InvariantCulture));
        File.Move(partial, processIdFile);

        Thread.Sleep(int.Parse(sleepMilliseconds, CultureInfo.InvariantCulture));
        return 0;
    }

    private static int PrintEnvironment(TextWriter output, string[] arguments)
    {
        output.Write((Environment.GetEnvironmentVariable(arguments[0]) ?? "<unset>") + "\n");
        return 0;
    }
}
