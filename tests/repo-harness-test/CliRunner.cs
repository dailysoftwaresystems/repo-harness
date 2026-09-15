using System.Reflection;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// Runs the real <c>repo-harness</c> CLI. End-to-end tests exercise the built program
/// rather than calling services directly, so argument parsing, dependency wiring and exit
/// codes are all covered by the same assertion.
/// </summary>
public static class CliRunner
{
    /// <summary>Longest a single CLI invocation may run before the test fails as hung.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static readonly Lazy<string> CliAssembly = new(LocateCliAssembly, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>The CLI assembly the tests run: the build a host's repo-harness is compared with.</summary>
    public static string CliAssemblyPath => CliAssembly.Value;

    /// <summary>Runs the CLI and returns its result.</summary>
    /// <param name="arguments">The command line after <c>repo-harness</c>.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <param name="workingDirectory">The directory the CLI starts in, or the test's own.</param>
    /// <param name="standardInput">Text the CLI reads on standard input, as a host's repo-harness reads a request.</param>
    public static async Task<ProcessResult> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        string? standardInput = null)
    {
        var runner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());

        var result = await runner.RunAsync(
            new ProcessRequest
            {
                FileName = TestHost.DotnetExecutable,
                Arguments = ["exec", CliAssembly.Value, .. arguments],
                WorkingDirectory = workingDirectory,
                StandardInput = standardInput,

                // Held open as the machine that reaches a host holds it: the end of a host's input is how the
                // host learns that machine has gone, so input closed at once would cancel the request.
                HoldStandardInputOpen = standardInput is not null,
                Timeout = Budget,
            },
            cancellationToken);

        // A hang reported as "exit code -1 was not 0" would send the reader after the
        // wrong failure.
        Assert.False(
            result.TimedOut,
            $"'repo-harness {string.Join(' ', arguments)}' did not finish within {Budget}.");

        return result;
    }

    /// <summary>
    /// Reads the CLI assembly's path from this assembly's metadata, where the build put it
    /// by asking the CLI project for its own output path. Guessing it from directory names
    /// breaks as soon as the output layout changes.
    /// </summary>
    private static string LocateCliAssembly()
    {
        var path = typeof(CliRunner).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoHarnessCliPath")
            ?.Value;

        Assert.False(
            string.IsNullOrEmpty(path),
            "This test assembly records no RepoHarnessCliPath; the EmbedCliPath build target did not run.");

        Assert.True(File.Exists(path), $"The CLI assembly is not at '{path}'. Build the solution first.");
        return path;
    }
}
