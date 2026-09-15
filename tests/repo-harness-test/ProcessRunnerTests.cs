using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// The process runner is exercised against this test assembly started as a child, whose
/// output, timing and descendants each test controls exactly. No shell or other tool is
/// involved, so every test means the same thing on every platform.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static ProcessRunner CreateRunner() => new(new HostPlatform(), FilePermissionsFactory.Create());

    [Fact]
    public async Task RunAsync_PassesEveryArgumentThroughUnchanged()
    {
        // Each of these is mangled by at least one way of building a command line by hand.
        string[] arguments = ["plain", "with space", "", "quote\"inside", "trailing\\", "tab\there", "a'b"];

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-args", arguments),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments.Select(argument => "[" + argument + "]"), Lines(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_DecodesOutputAsUtf8_OnEveryPlatform()
    {
        // git writes paths as UTF-8. Decoded with the Windows console code page instead, a
        // repository under C:\Users\João would be reported at a path that does not exist.
        string[] arguments = ["João", "ação", "日本語"];

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-args", arguments),
            TestContext.Current.CancellationToken);

        Assert.Equal(arguments.Select(argument => "[" + argument + "]"), Lines(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_ReportsAFailingExitCode_WithoutThrowing()
    {
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("exit", "3"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_DeliversEachLineWhileTheProcessIsStillRunning()
    {
        using var temp = new TempDirectory();
        var signal = temp.Combine("signal");
        var errorLines = new List<string>();

        var request = TestHost.ChildRequest("stream", signal) with
        {
            // The child finishes only after this file appears, so it can succeed only if
            // "first" reached this callback while the child was still running.
            OnOutputLine = line =>
            {
                if (line == "first")
                {
                    File.WriteAllText(signal, "received");
                }
            },
            OnErrorLine = line =>
            {
                lock (errorLines)
                {
                    errorLines.Add(line);
                }
            },
        };

        var result = await CreateRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, "Output arrived only after the child exited (exit code 3 means it gave up waiting).");
        Assert.Equal(["first", "second"], Lines(result.StandardOutput));
        Assert.Equal(["problem"], Lines(result.StandardError));
        Assert.Equal(["problem"], errorLines);
    }

    [Fact]
    public async Task RunAsync_StopsAProcessThatExceedsItsBudget()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("sleep", "120000") with { Timeout = TimeSpan.FromMilliseconds(500) },
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"The budget was enforced only after {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsTheWholeProcessTree()
    {
        using var temp = new TempDirectory();
        var processIdFile = temp.Combine("grandchild.pid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = CreateRunner().RunAsync(
            TestHost.ChildRequest("spawn-grandchild", processIdFile, "120000"),
            cancellation.Token);

        var grandchildId = await WaitForProcessIdAsync(processIdFile, TestContext.Current.CancellationToken);
        using var grandchild = Process.GetProcessById(grandchildId);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // A grandchild left running keeps what it inherited busy, a build tree or the very
        // output pipe the run was reading, long after the run was abandoned.
        Assert.True(
            grandchild.WaitForExit(TimeSpan.FromSeconds(30)),
            $"Grandchild process {grandchildId} was still running after the run was cancelled.");
    }

    [Fact]
    public async Task RunAsync_AppliesEnvironmentOverrides()
    {
        const string Name = "REPO_HARNESS_TEST_APPLIED";
        var request = TestHost.ChildRequest("print-env", Name);

        var result = await CreateRunner().RunAsync(
            request with { Environment = WithVariable(request, Name, "applied") },
            TestContext.Current.CancellationToken);

        Assert.Equal("applied", result.TrimmedOutput);
    }

    [Fact]
    public async Task RunAsync_RemovesAnInheritedVariable_WhenGivenNull()
    {
        // A name no other test uses, so setting it on this process cannot affect them.
        const string Name = "REPO_HARNESS_TEST_REMOVED";
        Environment.SetEnvironmentVariable(Name, "inherited");

        try
        {
            var request = TestHost.ChildRequest("print-env", Name);

            var result = await CreateRunner().RunAsync(
                request with { Environment = WithVariable(request, Name, null) },
                TestContext.Current.CancellationToken);

            Assert.Equal("<unset>", result.TrimmedOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    [Fact]
    public async Task RunAsync_ReportsAMissingExecutable_AsNotFound()
    {
        await Assert.ThrowsAsync<ExecutableNotFoundException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = "definitely-not-a-real-tool-xyzzy" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_ReportsAMissingWorkingDirectory_AsThat_RatherThanAsAMissingExecutable()
    {
        // On Linux and macOS the operating system reports both with the same error number.
        using var temp = new TempDirectory();
        var missing = temp.Combine("gone");

        var exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => CreateRunner().RunAsync(
            TestHost.ChildRequest("exit", "0") with { WorkingDirectory = missing },
            TestContext.Current.CancellationToken));

        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindExecutable_LocatesGit()
    {
        // git is a hard prerequisite of the harness, so its absence would be a
        // meaningful failure rather than a flaky test.
        Assert.NotNull(CreateRunner().FindExecutable("git"));
    }

    [Fact]
    public void FindExecutable_ReturnsNull_ForAnUnknownCommand()
    {
        Assert.Null(CreateRunner().FindExecutable("definitely-not-a-real-tool-xyzzy"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void FindExecutable_RequiresAnExecuteBit_OnLinuxAndMacOs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no execute bit; the extension decides.");

        using var temp = new TempDirectory();
        var tool = temp.WriteFile("tool", "#!/bin/sh\n");

        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(CreateRunner().FindExecutable(tool));

        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var found = CreateRunner().FindExecutable(tool);

        Assert.NotNull(found);
        PathAssert.Same(tool, found);
    }

    [Fact]
    public void FindExecutable_ResolvesABareNameThroughPathext_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PATHEXT exists only on Windows.");

        using var temp = new TempDirectory();
        var script = temp.WriteFile("tool.cmd", "@echo off\r\n");

        var found = CreateRunner().FindExecutable(temp.Combine("tool"));

        Assert.NotNull(found);
        PathAssert.Same(script, found);
    }

    private static List<string> Lines(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

    private static Dictionary<string, string?> WithVariable(ProcessRequest request, string name, string? value)
        => new(request.Environment, StringComparer.Ordinal) { [name] = value };

    private static async Task<int> WaitForProcessIdAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)
                && int.TryParse(File.ReadAllText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"The child never recorded its grandchild's process id in '{path}'.");
        return 0;
    }
}
