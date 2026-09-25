using System.Diagnostics;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>How the one process the tool starts and never waits for is started.</summary>
public sealed class DetachedProcessLauncherTests
{
    /// <summary>
    /// On Windows the process a hold runs in is started from one command line, which the program started splits
    /// again: every argument must come back as it went in, above all a path with a space in it, as a tool installed
    /// for a user whose name holds one has.
    /// </summary>
    [Fact]
    public async Task AWindowsCommandLine_IsReadBackAsTheArgumentsItWasMadeFrom()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string[] arguments =
        [
            "host-hold", "with space", "", "quote\"inside", "trailing\\", "space and trailing\\",
            "backslashes\\\\\"then a quote", "tab\there", @"C:\Users\A User\.dotnet\tools\dssharness.dll",
        ];

        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            // Given as one string, the command line reaches the program as it is written.
            Arguments = $"exec \"{TestHost.AssemblyPath}\" {DetachedProcessLauncher.CommandLine(arguments)}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.Environment[TestHost.ChildModeVariable] = "echo-args";

        using var child = Process.Start(start)!;
        var output = await child.StandardOutput.ReadToEndAsync(cancellationToken);
        await child.WaitForExitAsync(cancellationToken);

        Assert.Equal([.. arguments.Select(argument => $"[{argument}]")], output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));
    }
}
