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
            "sleep" => Sleep(arguments),
            "stream" => Stream(standardOutput, standardError, arguments),
            "spawn-grandchild" => SpawnGrandchild(arguments),
            "print-env" => PrintEnvironment(standardOutput, arguments),
            "exit" => int.Parse(arguments[0], CultureInfo.InvariantCulture),
            _ => 99,
        };
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
