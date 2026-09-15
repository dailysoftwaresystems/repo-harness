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
    public async Task RunAsync_WritesStandardInput_AndClosesIt()
    {
        // The child reads its input to the end, so it can finish only once the input is closed.
        // The text carries what a request to another host carries: spaces, quotes, non-ASCII.
        const string Input = "{\"arguments\":[\"with space\",\"quote\\\"inside\",\"João\"]}\nsecond line";

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-stdin") with { StandardInput = Input, Timeout = TimeSpan.FromSeconds(60) },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut, "The child never saw the end of its input.");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[" + Input + "]", result.StandardOutput.TrimEnd('\n'));
    }

    [Fact]
    public async Task RunAsync_GivesAChildAnInputThatEndsAtOnce_WhenTheRequestHasNone()
    {
        // Never this process's own input: a child reading it would take what was meant for the harness, and on
        // Windows a child inheriting an input this process is reading at that moment can hang as it starts.
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-stdin") with { Timeout = TimeSpan.FromSeconds(60) },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut, "The child was left reading an input that never ended.");
        Assert.Equal("[]", result.StandardOutput.TrimEnd('\n'));
    }

    [Theory]
    [InlineData(false, "ended")]
    [InlineData(true, "held")]
    public async Task RunAsync_ClosesStandardInputOnceWritten_UnlessAskedToHoldItOpen(bool hold, string expected)
    {
        // A child that watches for the end of its input learns from it that this process has gone, which
        // only means something if the input stays open while this process is still there.
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("read-line-then-watch", "1500") with
            {
                StandardInput = "request\n",
                HoldStandardInputOpen = hold,
                Timeout = TimeSpan.FromSeconds(60),
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.Equal(["[request]", expected], Lines(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_ReportsAMissingExecutable_AsNotFound()
    {
        await Assert.ThrowsAsync<ExecutableNotFoundException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = "definitely-not-a-real-tool-xyzzy" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_ReportsAFileThatIsNotAProgram_AsUnableToStart_RatherThanAsMissing()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile(OperatingSystem.IsWindows() ? "not-a-program.exe" : "not-a-program", "plain text\n");
        MakeExecutable(file);

        // Exactly this type: "not found" would send the reader after a file that is there.
        var exception = await Assert.ThrowsAsync<ProgramStartException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = file },
            TestContext.Current.CancellationToken));

        Assert.Contains("could not be started", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task RunAsync_ReportsAScriptWhoseInterpreterIsMissing_AsUnableToStart_OnLinuxAndMacOs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows starts no script through the line naming its interpreter.");

        using var temp = new TempDirectory();
        var script = temp.WriteFile("script", "#!/definitely/not/an/interpreter\n");
        MakeExecutable(script);

        // The system reports the interpreter missing with the same error as a missing program.
        var exception = await Assert.ThrowsAsync<ProgramStartException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = script },
            TestContext.Current.CancellationToken));

        Assert.Contains("interpreter or loader", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NeverStartsAProgram_FromBesideTheRunningExecutable()
    {
        // Left to the runtime, a name is looked for beside the running executable, and then in the current
        // directory, before PATH. This test's own executable is beside it and on no PATH, so a runner that
        // looked there would start it. The current directory is not changed to exercise that lookup too:
        // every test in the run shares it.
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
        Assert.SkipWhen(name.Length == 0 || CreateRunner().FindExecutable(name) is not null, $"'{name}' is on PATH in this run.");

        await Assert.ThrowsAsync<ExecutableNotFoundException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = name, Arguments = ["--help"], Timeout = TimeSpan.FromSeconds(60) },
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
    public void FindExecutable_OnWindows_FindsOnlyTheProgramThatWouldStart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows decides by extension what a name starts.");

        using var temp = new TempDirectory();
        temp.WriteFile("tool.cmd", "@echo off\r\n");

        // Starting "tool" never runs tool.cmd: Windows adds .exe to a name without an extension, and
        // nothing else.
        Assert.Null(CreateRunner().FindExecutable(temp.Combine("tool")));

        var program = temp.WriteFile("tool.exe", string.Empty);
        var found = CreateRunner().FindExecutable(temp.Combine("tool"));

        Assert.NotNull(found);
        PathAssert.Same(program, found);
    }

    [Fact]
    public void ProgramOnPath_TakesTheFirstPathDirectoryThatHasTheProgram()
    {
        var first = Path.Combine(TestHost.TemporaryRoot, "first");
        var second = Path.Combine(TestHost.TemporaryRoot, "second");
        var third = Path.Combine(TestHost.TemporaryRoot, "third");
        string[] present = [Path.Combine(second, "tool"), Path.Combine(third, "tool")];

        var found = ProcessRunner.ProgramOnPath(
            "tool",
            string.Join(Path.PathSeparator, first, second, third),
            windows: false,
            path => present.Contains(path));

        Assert.Equal(Path.Combine(second, "tool"), found);
    }

    [Fact]
    public void ProgramOnPath_NeverLooksInTheCurrentDirectory_OrBesideTheRunningExecutable()
    {
        // Both are where the runtime would look before PATH; the current directory is usually the
        // repository, so a file committed there under a tool's name would run instead of the tool.
        string[] present = [Path.Combine(Environment.CurrentDirectory, "tool"), Path.Combine(AppContext.BaseDirectory, "tool")];

        Assert.Null(ProcessRunner.ProgramOnPath("tool", Path.Combine(TestHost.TemporaryRoot, "bin"), windows: false, path => present.Contains(path)));
        Assert.Null(ProcessRunner.ProgramOnPath("tool", pathVariable: null, windows: false, path => present.Contains(path)));
    }

    [Theory]
    [InlineData("tool", "tool.exe")]
    [InlineData("tool.exe", "tool.exe")]
    [InlineData("tool.cmd", "tool.cmd")]
    public void ProgramOnPath_OnWindows_AddsOnlyExe_ToANameWithoutAnExtension(string name, string expected)
    {
        var directory = Path.Combine(TestHost.TemporaryRoot, "bin");
        string[] present = [Path.Combine(directory, "tool.bat"), Path.Combine(directory, "tool.cmd"), Path.Combine(directory, "tool.exe")];

        Assert.Equal(Path.Combine(directory, expected), ProcessRunner.ProgramOnPath(name, directory, windows: true, path => present.Contains(path)));
    }

    [Fact]
    public void ProgramOnPath_OnWindows_NeverFindsABatchFile_ForABareName()
    {
        // cmd.exe parses a batch file's arguments a second time, so arguments passed one by one would not
        // arrive as they were passed.
        var directory = Path.Combine(TestHost.TemporaryRoot, "bin");
        string[] present = [Path.Combine(directory, "tool.bat"), Path.Combine(directory, "tool.cmd")];

        Assert.Null(ProcessRunner.ProgramOnPath("tool", directory, windows: true, path => present.Contains(path)));
    }

    private static List<string> Lines(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

    /// <summary>Gives a file every execute bit on Linux and macOS; Windows decides by extension alone.</summary>
    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

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
