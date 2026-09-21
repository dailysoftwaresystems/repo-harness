using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// Setting up a developer environment on the machine that runs the leg: vcvarsall.bat is run once
/// for the leg's processor, over the host's own environment, and what it changed - and nothing else -
/// is what every process of the leg starts with. A vcvarsall.bat that fails, says it failed, or sets
/// up another processor is refused, naming why, rather than handed to a build as a working compiler.
/// </summary>
public sealed class DeveloperEnvironmentProviderTests
{
    private static readonly DeveloperEnvironmentConfig VisualStudio = new() { Kind = DeveloperEnvironmentKinds.VisualStudio };

    private static readonly Dictionary<string, string> NoHostEnvironment = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What vcvarsall.bat added or changed is set up - its tools ahead on PATH, INCLUDE, LIB, the
    /// tools version, the processor - and nothing it left alone: not the host's own variables, not
    /// cmd.exe's, and not what the harness handed the batch file.
    /// </summary>
    [Fact]
    public async Task WhatVcvarsallChanged_IsSetUp_AndNothingElse()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CCACHE_DIR"] = @"D:\cache" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", host, TestContext.Current.CancellationToken);

        Assert.Null(setUp.Failure);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = visualStudio.BinFor("x64") + Path.PathSeparator + ScriptedVisualStudio.ShellPath,
                ["INCLUDE"] = visualStudio.Include,
                ["LIB"] = Path.Combine(visualStudio.InstallationPath, "VC", "Tools", "MSVC", ScriptedVisualStudio.ToolsVersion, "lib", "x64"),
                ["VCToolsVersion"] = ScriptedVisualStudio.ToolsVersion,
                ["VSCMD_ARG_TGT_ARCH"] = "x64",
                ["VSCMD_VER"] = "18.0.0",
            },
            setUp.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(
            new DeveloperEnvironmentFact("vs", visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, ScriptedVisualStudio.ToolsVersion, "amd64"),
            setUp.Fact);
        Assert.Equal(
            $"developer environment: vs (Visual Studio {ScriptedVisualStudio.InstallationVersion}, MSVC {ScriptedVisualStudio.ToolsVersion}, amd64)",
            setUp.Fact!.Describe());
    }

    /// <summary>
    /// The batch file is started by a relative path from a directory of its own, under the host's
    /// environment, with vcvarsall.bat and the architecture handed over in variables rather than in
    /// its text; the directory is gone afterwards.
    /// </summary>
    [Fact]
    public async Task TheCapture_RunsUnderTheHostsEnvironment_AndLeavesNothingBehind()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CCACHE_DIR"] = @"D:\cache" };

        await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", host, TestContext.Current.CancellationToken);

        var capture = Assert.Single(visualStudio.Captures);
        Assert.Equal(["/d", "/u", "/c", @".\capture.bat"], capture.Arguments);
        Assert.Equal(DeveloperEnvironmentProvider.CaptureBudget, capture.Timeout);
        Assert.Equal(@"D:\cache", capture.Environment["CCACHE_DIR"]);
        Assert.Equal(
            Path.Combine(visualStudio.InstallationPath, "VC", "Auxiliary", "Build", "vcvarsall.bat"),
            capture.Environment[DeveloperEnvironmentProvider.ScriptVariable]);
        Assert.Equal("amd64", capture.Environment[DeveloperEnvironmentProvider.ArchitectureVariable]);
        Assert.False(Directory.Exists(capture.WorkingDirectory), "the capture's directory is removed once read");
    }

    /// <summary>
    /// A PATH the host declares is the one vcvarsall.bat puts its tools ahead of, so a program only the
    /// host's PATH holds is still found.
    /// </summary>
    [Fact]
    public async Task APathTheHostDeclares_IsKept_BehindVisualStudiosTools()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"D:\tools" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", host, TestContext.Current.CancellationToken);

        Assert.Equal(visualStudio.BinFor("x64") + Path.PathSeparator + @"D:\tools", setUp.Environment["PATH"]);
    }

    /// <summary>
    /// A leg built for another processor than the machine's is set up to cross-compile, and held to
    /// the processor vcvarsall.bat says it set up.
    /// </summary>
    [Fact]
    public async Task ALegForAnotherProcessor_IsSetUpToCrossCompile()
    {
        using var visualStudio = new ScriptedVisualStudio();

        var setUp = await visualStudio.Provider("x86_64").SetUpAsync("vs", visualStudio.Found, "arm64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Null(setUp.Failure);
        Assert.Equal("amd64_arm64", setUp.Fact!.Architecture);
        Assert.Equal("arm64", setUp.Environment["VSCMD_ARG_TGT_ARCH"]);
        Assert.StartsWith(visualStudio.BinFor("arm64") + Path.PathSeparator, setUp.Environment["PATH"], StringComparison.Ordinal);
    }

    /// <summary>
    /// vcvarsall.bat runs once for every leg sharing an instance, a processor and a host environment -
    /// the same whatever order or spelling the host's variables come in - and again for each that
    /// differs.
    /// </summary>
    [Fact]
    public async Task LegsSharingAnInstanceAProcessorAndAnEnvironment_ShareOneRun()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var provider = visualStudio.Provider();
        var token = TestContext.Current.CancellationToken;

        var first = await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, token);
        var again = await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }, token);

        Assert.Same(first, again);
        Assert.Single(visualStudio.Captures);

        await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", new Dictionary<string, string> { ["A"] = "1", ["B"] = "3" }, token);
        await provider.SetUpAsync("vs", visualStudio.Found, "arm64", new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, token);

        Assert.Equal(3, visualStudio.Captures.Count);
    }

    /// <summary>
    /// Two environments set up from one instance are each their own: what a leg's line names, and what
    /// a refusal names, is the environment its toolchain names.
    /// </summary>
    [Fact]
    public async Task TwoEnvironmentsFromOneInstance_AreEachSetUpUnderTheirOwnName()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var provider = visualStudio.Provider();
        var token = TestContext.Current.CancellationToken;

        var first = await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, token);
        var second = await provider.SetUpAsync("buildTools", visualStudio.Found, "x86_64", NoHostEnvironment, token);

        Assert.Equal("vs", first.Fact!.Name);
        Assert.Equal("buildTools", second.Fact!.Name);
    }

    /// <summary>
    /// A capture directory that cannot be removed afterwards - a scanner still holding a file in it -
    /// is said, and fails nothing: what vcvarsall.bat set was read before.
    /// </summary>
    [Fact]
    public async Task ACaptureDirectoryThatCannotBeRemoved_IsSaid_AndFailsNothing()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var harness = new HarnessFactory();
        var held = new HeldFiles(new PhysicalFileSystem(FilePermissionsFactory.Create()));

        var setUp = await visualStudio.Provider(fileSystem: held, output: harness.Output)
            .SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        var scratch = Assert.Single(visualStudio.Captures).WorkingDirectory!;

        try
        {
            Assert.Null(setUp.Failure);
            Assert.Contains(
                $"developer-environment: WARN - developer environment 'vs': '{scratch}' could not be removed: {HeldFiles.Said}",
                harness.StandardError.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A capture the caller that started it gave up on is not what the next caller gets: that one
    /// runs its own, and is set up. The caller that gave up is told it was stopped.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACaptureAnotherCallerStopped_IsRunAgain_ForACallerThatStillWantsIt()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var visualStudio = new ScriptedVisualStudio
        {
            Holding = async (index, stopping) =>
            {
                if (index == 0)
                {
                    firstStarted.SetResult();
                    await Task.Delay(Timeout.Infinite, stopping);
                }
            },
        };

        var provider = visualStudio.Provider();
        using var givingUp = new CancellationTokenSource();

        var abandoned = provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, givingUp.Token);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var wanted = provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);
        await givingUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        var setUp = await wanted;

        Assert.Null(setUp.Failure);
        Assert.Equal(2, visualStudio.Captures.Count);
    }

    /// <summary>
    /// What is set up is the instance the survey found, and nothing else: vswhere is not asked again,
    /// and asked to set up where the survey found none, the provider refuses the call outright - a leg
    /// is only ever placed where one was found.
    /// </summary>
    [Fact]
    public async Task OnlyTheInstanceTheSurveyFound_IsSetUp()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var provider = visualStudio.Provider();

        await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Empty(visualStudio.Probes);
        Assert.Single(visualStudio.Captures);

        foreach (var none in new[]
        {
            DeveloperEnvironmentCheck.Nowhere("no instance"),
            DeveloperEnvironmentCheck.Unreadable("vswhere did not answer"),
            new DeveloperEnvironmentCheck(DeveloperEnvironmentFound.Installed, null, null, null),
            new DeveloperEnvironmentCheck(DeveloperEnvironmentFound.Installed, null, string.Empty, null),
            new DeveloperEnvironmentCheck(DeveloperEnvironmentFound.Nowhere, "gone", visualStudio.InstallationPath, null),
        })
        {
            // Refused as the call is made, before any of it starts.
            Assert.Throws<ArgumentException>(
                "found",
                () => { _ = provider.SetUpAsync("vs", none, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken); });
        }

        Assert.Single(visualStudio.Captures);
    }

    /// <summary>A processor vcvarsall.bat has no word for is refused, naming both processors, and nothing is run.</summary>
    [Fact]
    public async Task AProcessorVcvarsallHasNoWordFor_IsRefused()
    {
        using var visualStudio = new ScriptedVisualStudio();

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "riscv64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs' has no vcvarsall.bat architecture for a riscv64 leg on a x86_64 host", setUp.Failure);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>An instance without vcvarsall.bat is refused, naming the file looked for.</summary>
    [Fact]
    public async Task AnInstanceWithoutVcvarsall_IsRefused_NamingTheFile()
    {
        using var empty = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio();

        var setUp = await visualStudio.Provider().SetUpAsync(
            "vs",
            DeveloperEnvironmentCheck.Installed(empty.Path, null),
            "x86_64",
            NoHostEnvironment,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            $"developer environment 'vs' found Visual Studio at '{empty.Path}', which has no "
            + $"'{Path.Combine(empty.Path, "VC", "Auxiliary", "Build", "vcvarsall.bat")}'",
            setUp.Failure);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>A vcvarsall.bat that exits non-zero is refused with its exit code and what it said.</summary>
    [Fact]
    public async Task AVcvarsallThatExitsNonZero_IsRefused_WithWhatItSaid()
    {
        using var visualStudio = new ScriptedVisualStudio
        {
            ExitCode = "1",
            Log = Encoding.Unicode.GetBytes("[ERROR:vcvarsall.bat] Invalid argument found : amd64x\r\n"),
        };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 exited 1: [ERROR:vcvarsall.bat] Invalid argument found : amd64x", setUp.Failure);
    }

    /// <summary>One that exits non-zero having printed nothing says that, rather than trailing off after its exit code.</summary>
    [Fact]
    public async Task AVcvarsallThatExitsNonZero_HavingPrintedNothing_SaysSo()
    {
        using var visualStudio = new ScriptedVisualStudio { ExitCode = "1" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 exited 1 and printed nothing", setUp.Failure);
    }

    /// <summary>
    /// vcvarsall.bat can report a failure and exit 0. Its own lines are cmd.exe's UTF-16, what the
    /// programs it starts print is the console's code page, and an error in either is refused.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnErrorVcvarsallReports_IsRefused_ThoughItExitedZero(bool unicode)
    {
        const string Said = "[ERROR:vcvars.bat] Toolset directory for version '14.99' was not found.";

        using var visualStudio = new ScriptedVisualStudio
        {
            Log = (unicode ? Encoding.Unicode : Encoding.ASCII).GetBytes("**********\r\n" + Said + "\r\n"),
        };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal($"developer environment 'vs': vcvarsall.bat amd64 reported an error: ********** / {Said}", setUp.Failure);
    }

    /// <summary>An environment set up for another processor builds code for that one, and is refused.</summary>
    [Fact]
    public async Task AnEnvironmentForAnotherProcessor_IsRefused()
    {
        using var visualStudio = new ScriptedVisualStudio { ReportedTarget = "x86" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 set up VSCMD_ARG_TGT_ARCH 'x86', where 'x64' was asked for", setUp.Failure);
    }

    /// <summary>A batch file cmd.exe never ran is refused with what cmd.exe said.</summary>
    [Fact]
    public async Task ABatchFileThatNeverRan_IsRefused_WithWhatCmdSaid()
    {
        using var visualStudio = new ScriptedVisualStudio { WritesNothing = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal(
            "developer environment 'vs': vcvarsall.bat amd64 could not be run: cmd.exe (exit 1): The system cannot find the path specified.",
            setUp.Failure);
    }

    /// <summary>
    /// A batch file that stopped before listing what vcvarsall.bat left is refused as one that could not
    /// be run, never read as an environment with nothing in it.
    /// </summary>
    [Fact]
    public async Task ABatchFileThatStoppedBeforeItsLastListing_IsRefused()
    {
        using var visualStudio = new ScriptedVisualStudio { WritesNoListingAfter = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.StartsWith("developer environment 'vs': vcvarsall.bat amd64 could not be run: ", setUp.Failure, StringComparison.Ordinal);
    }

    /// <summary>A vcvarsall.bat that outlives its budget is refused, naming the budget.</summary>
    [Fact]
    public async Task AVcvarsallThatNeverFinishes_IsRefused_NamingTheBudget()
    {
        using var visualStudio = new ScriptedVisualStudio { TimesOut = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 did not finish within 120 seconds", setUp.Failure);
    }

    /// <summary>A shell that will not start is refused, naming the shell.</summary>
    [Fact]
    public async Task AShellThatWillNotStart_IsRefused_NamingTheShell()
    {
        using var visualStudio = new ScriptedVisualStudio { ShellMissing = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        var shell = Assert.Single(visualStudio.Captures).FileName;
        Assert.Equal(
            $"developer environment 'vs': vcvarsall.bat amd64 could not be run, because '{shell}' could not be started: "
            + new ExecutableNotFoundException(shell).Message,
            setUp.Failure);
    }

    /// <summary>
    /// A capture that cannot be written - a temporary directory this user cannot write, a disk that is
    /// full - fails the setup naming the directory, rather than escaping as a defect in this tool.
    /// </summary>
    [Fact]
    public async Task ACaptureThatCannotBeWritten_FailsTheSetup_NamingTheDirectory()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var unwritable = new UnwritableScratch(new PhysicalFileSystem(FilePermissionsFactory.Create()));

        var setUp = await visualStudio.Provider(fileSystem: unwritable)
            .SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.True(setUp.HasFailed);
        Assert.StartsWith("developer environment 'vs': the capture of vcvarsall.bat amd64 in '", setUp.Failure, StringComparison.Ordinal);
        Assert.EndsWith($"' could not be written or read: {UnwritableScratch.Said}", setUp.Failure, StringComparison.Ordinal);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>
    /// The batch file reaches vcvarsall.bat however its instance's path is spelled - with a percent
    /// pair, a caret, an exclamation mark, an ampersand, spaces and parentheses - since call expands
    /// the path once and reads it as nothing but a path. Run by this machine's own cmd.exe; skipped
    /// where there is none.
    /// </summary>
    [Fact]
    public async Task TheCapture_ReachesAnInstanceWhateverItsPathHolds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe runs the capture on Windows alone.");

        using var temp = new TempDirectory();
        var platform = HostDoubles.Platform(PlatformId.Windows, "x86_64");
        var processes = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());
        var provider = new DeveloperEnvironmentProvider(platform, processes, new PhysicalFileSystem(FilePermissionsFactory.Create()), new HarnessFactory().Output);

        foreach (var name in new[] { "p%OS%q", "a^b", "x!y", "r&d (x86)" })
        {
            temp.WriteFile(
                Path.Combine(name, "VC", "Auxiliary", "Build", "vcvarsall.bat"),
                "@echo off\r\nset \"VSCMD_ARG_TGT_ARCH=x64\"\r\nset \"VCToolsVersion=14.99.00000\"\r\nexit /b 0\r\n");

            var setUp = await provider.SetUpAsync(
                "vs",
                DeveloperEnvironmentCheck.Installed(temp.Combine(name), "18.0.0"),
                "x86_64",
                NoHostEnvironment,
                TestContext.Current.CancellationToken);

            Assert.False(setUp.HasFailed, $"{name}: {setUp.Failure}");
            Assert.Equal("14.99.00000", setUp.Environment["VCToolsVersion"]);
        }
    }

    /// <summary>A file system in which no directory can be made, as where the temporary directory cannot be written.</summary>
    private sealed class UnwritableScratch(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public const string Said = "Access to the path is denied.";

        public override void CreateDirectory(string path) => throw new UnauthorizedAccessException(Said);
    }

    /// <summary>A file system whose directories cannot be removed, as while a scanner holds a file in one.</summary>
    private sealed class HeldFiles(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public const string Said = "The process cannot access the file because it is being used by another process.";

        public override void DeleteDirectory(string path) => throw new IOException(Said);
    }

    /// <summary>
    /// The argument vcvarsall.bat takes: the machine's processor alone for a native build, and the two
    /// joined to cross-compile; none for a processor it has no word for.
    /// </summary>
    [Theory]
    [InlineData("x86_64", "x86_64", "amd64", "x64")]
    [InlineData("arm64", "arm64", "arm64", "arm64")]
    [InlineData("x86_64", "arm64", "amd64_arm64", "arm64")]
    [InlineData("arm64", "x86_64", "arm64_amd64", "x64")]
    [InlineData("x86_64", "x86", "amd64_x86", "x86")]
    [InlineData("x86_64", "riscv64", null, null)]
    [InlineData("riscv64", "x86_64", null, null)]
    public void TheArchitecture_IsTheMachinesProcessorThenTheLegs(string host, string target, string? architecture, string? reported)
    {
        Assert.Equal(architecture, VisualStudioArchitecture.For(host, target));

        if (architecture is not null)
        {
            Assert.Equal(reported, VisualStudioArchitecture.Target(architecture));
        }
    }

    /// <summary>What <c>set</c> lists is read one variable to a line, splitting at the first '=' only.</summary>
    [Fact]
    public void WhatSetLists_IsReadOneVariableToALine()
    {
        var variables = DeveloperEnvironmentProvider.Variables("A=1\r\nB=x=y\r\n=C:=C:\\\r\nnothing\r\nc=\n");

        Assert.Equal(
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "x=y", ["c"] = string.Empty },
            variables.ToDictionary(StringComparer.Ordinal));
        Assert.Equal("1", variables["a"]);
    }

    /// <summary>
    /// Visual Studio as it is installed on this machine: cl is on the PATH it sets up, beside INCLUDE
    /// and LIB, for this machine's own processor. Skipped where this machine has no Visual Studio with
    /// the C++ build tools.
    /// </summary>
    [Fact]
    public async Task TheVisualStudioOnThisMachine_SetsUpCl()
    {
        var platform = new HostPlatform();
        var processes = new ProcessRunner(platform, FilePermissionsFactory.Create());
        var probe = new DeveloperEnvironmentProbe(platform, processes);
        var token = TestContext.Current.CancellationToken;

        var found = await probe.CheckAsync(VisualStudio, token);

        Assert.SkipUnless(found.CanSetUp, $"This machine has no Visual Studio with the C++ build tools: {found.Reason}");

        var provider = new DeveloperEnvironmentProvider(platform, processes, new PhysicalFileSystem(FilePermissionsFactory.Create()), new HarnessFactory().Output);
        var setUp = await provider.SetUpAsync("vs", found, platform.Processor, NoHostEnvironment, token);

        Assert.Null(setUp.Failure);
        Assert.False(string.IsNullOrEmpty(setUp.Environment["INCLUDE"]));
        Assert.False(string.IsNullOrEmpty(setUp.Environment["LIB"]));
        Assert.Contains(
            setUp.Environment["PATH"].Split(';', StringSplitOptions.RemoveEmptyEntries),
            directory => File.Exists(Path.Combine(directory, "cl.exe")));
        Assert.Equal(VisualStudioArchitecture.For(platform.Processor, platform.Processor), setUp.Fact!.Architecture);
        Assert.NotEqual("unknown", setUp.Fact.ToolsVersion);
        Assert.Equal(found.InstallationPath, setUp.Fact.InstallationPath);
    }
}
