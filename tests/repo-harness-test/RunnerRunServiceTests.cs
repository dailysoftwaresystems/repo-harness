using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// A predefined runner gets the same witnesses, bounds and verdicts every other leg-running command
/// gets. These pin the three places that stop being true quietly: a program nobody declared running
/// halfway through a file, a credential reaching a log or an argument list, and an excusal that
/// nothing re-measured turning a regression green.
/// </summary>
public sealed class RunnerRunServiceTests
{
    private const string Leg = "lin-gcc-release";
    private const string RunId = "20260916-100000-0a1b2c3d";
    private const string Secret = "not-a-real-credential-9f3a";

    [Fact]
    public async Task AnActionFileNamingAnUndeclaredProgram_IsRefusedWithNothingRun()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: first
                run: |
                  dotnet --version
              - name: second
                run: |
                  curl https://example.invalid
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("curl", refusal.Message, StringComparison.Ordinal);

        // The refusal covers the whole file, so the first step — which is allowed — never ran
        // either. A file that fails on its fourth step has already changed the tree.
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", RunId)));
    }

    /// <summary>
    /// A phase derived from another was rebuilt member by member, and the two guard keys were not
    /// among the members copied — so an action file that declared inputs ran with both guards off
    /// while its own text said they were on. Nothing said they had been skipped.
    /// </summary>
    /// <remarks>
    /// Observed through the refusal a step asking for contention gets when the run reaches no leg:
    /// that refusal can only fire if the key survived the copy. Drop it again and this run is
    /// allowed, having watched nothing.
    /// </remarks>
    [Fact]
    public async Task AGuardedStep_KeepsItsGuard_ThroughAnActionThatDeclaresInputs()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            inputs:
              corpusRoot:
                default: corpus
            steps:
              - name: read
                uses: harness/read-inputs
              - name: measure
                watchContention: true
                run: |
                  dotnet --version
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("watchContention", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same file without inputs, which took a different path through the code: this is the one
    /// that always worked, and it is here so the pair says which half was broken.
    /// </summary>
    [Fact]
    public async Task AGuardedStep_KeepsItsGuard_WithNoInputsDeclared()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: measure
                watchContention: true
                run: |
                  dotnet --version
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
    }

    /// <summary>
    /// A run line using the shell is an ordinary action file, and this tool owns none of its braces.
    /// Refusing them turned working files away at load, before the first step.
    /// </summary>
    [Fact]
    public async Task AnActionFileUsingShellVariables_IsNotRefused()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: measure
                run: |
                  dotnet --version ${HOME}
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        // Runs, rather than being refused over a brace group belonging to the shell. Whether the
        // child succeeds is not what this pins.
        var result = await Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
    }

    /// <summary>
    /// A step produces something, a later step consumes it by name, what the run asked to keep
    /// survives, and the working space goes. Without this an action is a wrapper around one
    /// program: a round trip whose first half runs here and second half elsewhere has nowhere to
    /// put the thing being carried.
    /// </summary>
    [Fact]
    public async Task AStepsOutput_IsThereForTheNextStep_Persisted_AndItsWorkingSpaceRemoved()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, $$"""
            name: corpus
            steps:
              - name: pack
                outputs:
                  - payload.txt
                persist: true
                run: |
                  "{{Child}}" "{{Exec}}" "{{Assembly}}" "{stepBuild}/payload.txt" carried
              - name: consume
                run: |
                  "{{Child}}" "{{Exec}}" "{{Assembly}}" "{actionBuild}/pack/copied.txt" seen
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = Path.GetFileNameWithoutExtension(Child) });

        var runner = new RunnerConfig
        {
            Action = "corpus/corpus.yml",
            Env = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "write-file" },
        };

        var result = await Service(factory).RunAsync(
            config,
            Request(temp, runner),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);

        var action = Path.Combine(temp.Path, ".harness-config", "runner", "actions", "corpus");

        // Kept, under the run and the step that produced it.
        var kept = Path.Combine(action, "artifacts", RunId, Leg, "pack", "payload.txt");
        Assert.True(File.Exists(kept), $"expected '{kept}' to have been kept");
        Assert.Equal("carried", await File.ReadAllTextAsync(kept, TestContext.Current.CancellationToken));

        // And the working space is gone, including what the second step wrote there and never
        // asked to keep.
        Assert.False(
            Directory.Exists(Path.Combine(action, "build", RunId, Leg)),
            "this run's working directory should have been removed");
    }

    /// <summary>
    /// A step that produced exactly what it declared and then failed keeps nothing. Carrying
    /// evidence out of work that did not pass is the misattribution this tool exists to refuse: a
    /// later step, on another host, would read a payload from a leg that never succeeded and have no
    /// way to know.
    /// </summary>
    [Fact]
    public async Task AStepThatFailed_KeepsNothing_EvenThoughItWroteWhatItDeclared()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // Writes payload.txt, then exits 7. The file is on disk; the step is not one that passed.
        await WriteActionAsync(factory, temp, $$"""
            name: corpus
            steps:
              - name: pack
                outputs:
                  - payload.txt
                persist: true
                run: |
                  "{{Child}}" "{{Exec}}" "{{Assembly}}" "{stepBuild}/payload.txt" carried 7
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = Path.GetFileNameWithoutExtension(Child) });

        var runner = new RunnerConfig
        {
            Action = "corpus/corpus.yml",
            Env = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "write-file" },
        };

        var result = await Service(factory).RunAsync(
            config,
            Request(temp, runner),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);

        var action = Path.Combine(temp.Path, ".harness-config", "runner", "actions", "corpus");

        Assert.False(
            File.Exists(Path.Combine(action, "artifacts", RunId, Leg, "pack", "payload.txt")),
            "a step that failed should have kept nothing, although it wrote the file");

        // And the working space is gone either way, so the file is not reachable there either.
        Assert.False(Directory.Exists(Path.Combine(action, "build", RunId, Leg)));
    }

    /// <summary>
    /// A run refuses to write its scratch where git would commit it, before a single step runs. The
    /// rules were written by init and nothing checked them afterwards, so a repository whose
    /// .gitignore predated them wrote a run's kept output where git status showed it. Both
    /// directories are asked about, and only the one git would not ignore is named.
    /// </summary>
    [Theory]
    [InlineData(false, false, "'build/' and 'artifacts/'")]
    [InlineData(true, false, "'artifacts/'")]
    [InlineData(false, true, "'build/'")]
    public async Task AnActionWhoseScratchGitWouldCommit_IsRefused_BeforeAnythingRuns(
        bool ignoreBuild,
        bool ignoreArtifacts,
        string named)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(
            factory,
            temp,
            $$"""
            name: corpus
            steps:
              - name: pack
                run: |
                  "{{Child}}" "{{Exec}}" "{{Assembly}}" 0
            """,
            ignoreBuild,
            ignoreArtifacts);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = Path.GetFileNameWithoutExtension(Child) });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"does not ignore this action's {named}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("init", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing has run", refusal.Message, StringComparison.Ordinal);

        // And nothing did: no working space was made for the run.
        Assert.False(Directory.Exists(Path.Combine(temp.Path, ".harness-config", "runner", "actions", "corpus", "build")));
    }

    /// <summary>
    /// A step that exits zero having written nothing it declared is unwitnessed, not passed. An
    /// exit code alone cannot tell that apart from work that was done.
    /// </summary>
    [Fact]
    public async Task AStepThatDeclaredAnOutputAndProducedNothing_IsUnwitnessed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, $$"""
            name: corpus
            steps:
              - name: pack
                outputs:
                  - payload.txt
                run: |
                  "{{Child}}" "{{Exec}}" "{{Assembly}}" 0
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = Path.GetFileNameWithoutExtension(Child) });

        var runner = new RunnerConfig
        {
            Action = "corpus/corpus.yml",
            Env = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "exit" },
        };

        var result = await Service(factory).RunAsync(
            config,
            Request(temp, runner),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains("without producing payload.txt", result.Verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A question git cannot answer is no answer either way, and no verdict on the code. It is about
    /// this leg's tree - a copy git will not trust, a worktree whose link is broken - so the leg cannot
    /// run there, before anything starts, in git's own words, which name the fix themselves; the legs
    /// on other trees still report. Raised as a refusal of the run, it ended every leg over one tree.
    /// </summary>
    [Fact]
    public async Task AnIgnoreQuestionGitCannotAnswer_LeavesTheLegUnavailable_InGitsOwnWords()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // No repository at all, so git cannot say what it would ignore.
        WriteActionFile(temp, """
            name: corpus
            steps:
              - name: pack
                run: |
                  dotnet --version
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Equal(LegVerdict.SkippedUnavailable, Verdicts.ForRefusal(refusal.ExitCode));
        Assert.Contains("git could not say whether this action's 'build/' is ignored", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing has run", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("not a git repository", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, ".harness-config", "runner", "actions", "corpus", "build")));
    }

    /// <summary>
    /// A kept directory holding a link keeps what it holds, and names the link it did not follow: a
    /// link can lead anywhere, and following it could copy half a disk or go round for ever, but a
    /// kept copy missing what its links led to - and saying nothing - is an incomplete copy a later
    /// sync carries to every host as if it were whole.
    /// </summary>
    [Fact]
    public async Task AKeptDirectoryHoldingALink_KeepsWhatItHolds_AndNamesTheLinkItDidNotFollow()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var outside = temp.WriteFile(Path.Combine("outside", "elsewhere.txt"), "x");
        var target = Path.GetDirectoryName(outside)!;

        try
        {
            Directory.CreateSymbolicLink(temp.Combine("can-link"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        await WriteActionAsync(factory, temp, $$"""
            name: corpus
            steps:
              - name: pack
                outputs:
                  - runs
                persist: true
                run: |
                  "{{Child}}" "{{Exec}}" "{{Assembly}}" "{stepBuild}/runs" "{{target}}"
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = Path.GetFileNameWithoutExtension(Child) });

        var runner = new RunnerConfig
        {
            Action = "corpus/corpus.yml",
            Env = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "link-directory" },
        };

        var result = await Service(factory).RunAsync(config, Request(temp, runner), TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);

        var kept = Path.Combine(temp.Path, ".harness-config", "runner", "actions", "corpus", "artifacts", RunId, Leg, "pack", "runs");

        Assert.True(File.Exists(Path.Combine(kept, "kept.txt")), "what the directory itself holds is kept");
        Assert.False(File.Exists(Path.Combine(kept, "latest", "elsewhere.txt")), "the link was followed");
        Assert.Contains(
            "'pack' asked to keep 'runs', which holds 1 directory link(s) that were not followed, so what they lead to is not in the kept copy: 'latest'",
            factory.StandardError.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A program named by a relative path is the leg's own tree's, as the policy that allowed it read
    /// it. Read against wherever this process started, it was the main checkout's copy for a leg on a
    /// worktree - or, as here, no file at all.
    /// </summary>
    [Fact]
    public async Task ARelativeProgram_IsReadFromTheLegsOwnTree_NotFromWhereThisProcessStarted()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        TestHost.StartableProgram(temp.Combine("tools"), "own-probe");

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: own
                run: |
                  tools/own-probe
            """);

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    /// <summary>
    /// And from the directory its step runs in, where the policy that allowed it read it too:
    /// './own-probe' in a step that runs in 'tools' is 'tools/own-probe'. Read from the tree root, it
    /// was a file that is not there - or somebody else's.
    /// </summary>
    [Fact]
    public async Task ARelativeProgram_IsReadFromTheDirectoryItsStepRunsIn()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        TestHost.StartableProgram(temp.Combine("tools"), "own-probe");

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: own
                workingDirectory: tools
                run: |
                  ./own-probe
            """);

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    /// <summary>
    /// A step's directory is judged where the run will really start the step, its placeholders
    /// filled in: './tool' in a step whose workingDirectory '{where}' is '../elsewhere' is outside
    /// the repository, and refused, saying where it was read. Read as written, it passed, and the
    /// step then started a program from wherever the input pointed.
    /// </summary>
    [Fact]
    public async Task AProgramInADirectoryAnInputNames_IsJudgedWhereItReallyIs()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            inputs:
              where:
                default: ../elsewhere
            steps:
              - name: outside
                workingDirectory: "{where}"
                run: |
                  ./tool
            """);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("'./tool' (read as '../elsewhere/tool') is not declared under 'tools' and is not a path inside the repository", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the other way: a program the text alone would place outside the repository is allowed
    /// where the filled-in directory keeps it inside, and starts from there.
    /// </summary>
    [Fact]
    public async Task AProgramReadFromADeeperDirectoryAnInputNames_IsAllowed_AndStarts()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        TestHost.StartableProgram(temp.Combine("tools"), "own-probe");
        Directory.CreateDirectory(temp.Combine("a", "b"));

        await WriteActionAsync(factory, temp, """
            name: corpus
            inputs:
              where:
                default: a/b
            steps:
              - name: deep
                workingDirectory: "{where}"
                run: |
                  ../../tools/own-probe
            """);

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    /// <summary>
    /// A program an input names is judged as the program that starts. Read as written,
    /// '{dir}/tool' was a path inside the repository while the step started one outside it.
    /// </summary>
    [Fact]
    public async Task AProgramAnInputNames_IsJudgedAsTheProgramThatStarts()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            inputs:
              dir:
                default: ../elsewhere
            steps:
              - name: outside
                run: |
                  {dir}/tool
            """);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("'{dir}/tool' (read as '../elsewhere/tool') is not declared under 'tools' and is not a path inside the repository", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And an input naming a declared tool starts that tool: judged as written, '{runner}' was a
    /// program nothing declared.
    /// </summary>
    [Fact]
    public async Task AProgramAnInputNames_ThatIsADeclaredTool_Runs()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            inputs:
              runner:
                default: dotnet
            steps:
              - name: version
                run: |
                  {runner} --version
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var result = await Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    /// <summary>
    /// A line whose names fill in to nothing - an empty value in the runner's .env - starts no program
    /// at all: refused before anything runs, naming the line, rather than handed to the start as a
    /// program with no name, which read as a defect in this tool.
    /// </summary>
    [Fact]
    public async Task ALineThatFillsInToNothing_IsRefused_NamingTheLine()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".env", "ci.env"), "WRAPPER=\n");

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: bench
                run: |
                  {WRAPPER} ./bench
            """);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("'{WRAPPER}' starts nothing once its names are filled in", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a runner's own phase whose program fills in to nothing is refused the same way, before the
    /// first step: it has no policy to judge its lines, and reached the start as a program with no
    /// name, which read as a defect in this tool.
    /// </summary>
    [Fact]
    public async Task ARunnersOwnPhaseThatFillsInToNothing_IsRefused_BeforeAnythingRuns()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".env", "ci.env"), "WRAPPER=\n");

        var runner = new RunnerConfig
        {
            Phases = [new RunnerPhase { Name = "bench", Command = ["{WRAPPER}", "./bench"] }],
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("'bench' of runner 'corpus' starts nothing once its names are filled in", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step whose directory fills in to nothing, or whose names hold a character no path can, is
    /// refused naming the step before the first one runs: read as a path, either raised an error
    /// that read as a defect in this tool.
    /// </summary>
    [Theory]
    [InlineData("{WORKDIR}", "./bench", "runs in no directory once its names are filled in")]
    [InlineData(null, "./be\0nch", "holding a NUL character")]
    [InlineData("sub\0dir", "./bench", "holding a NUL character")]
    public async Task AStepNamingWhatNoPathCanHold_IsRefused_BeforeAnythingRuns(string? workingDirectory, string program, string expected)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".env", "ci.env"), "WORKDIR=\n");

        var runner = new RunnerConfig
        {
            Phases = [new RunnerPhase { Name = "bench", Command = [program], WorkingDirectory = workingDirectory }],
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("'bench' of runner 'corpus'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(expected, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host's own environment reaches every step, beneath everything the runner declares: a name
    /// only the host sets is the host's, and one the runner's values, its own environment or the
    /// step's also set is theirs.
    /// </summary>
    [Fact]
    public async Task TheHostsEnvironment_ReachesEveryStep_BeneathWhatTheRunnerDeclares()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".env", "ci.env"), "RH_VALUE=value\n");

        var step = Phase("step", "print-env", ["RH_STEP"], successPattern: "^step$");
        step.Env["RH_STEP"] = "step";

        var runner = new RunnerConfig
        {
            Env = { ["RH_RUNNER"] = "runner" },
            Phases =
            [
                Phase("host", "print-env", ["RH_HOST"], successPattern: "^host$"),
                Phase("value", "print-env", ["RH_VALUE"], successPattern: "^value$"),
                Phase("runner", "print-env", ["RH_RUNNER"], successPattern: "^runner$"),
                step,
            ],
        };

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, runner) with
            {
                HostEnvironment = new Dictionary<string, string>
                {
                    ["RH_HOST"] = "host",
                    ["RH_VALUE"] = "host",
                    ["RH_RUNNER"] = "host",
                    ["RH_STEP"] = "host",
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.Equal(4, result.Phases.Count);
    }

    private static string Child => TestHost.DotnetExecutable;

    private const string Exec = "exec";

    private static string Assembly => TestHost.AssemblyPath;

    [Fact]
    public async Task AStepThatPutsASecretInItsArguments_IsRefusedWithoutQuotingIt()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        WriteSecret(temp);

        var runner = new RunnerConfig
        {
            Phases =
            [
                Phase("harmless", "echo-args", ["one"]),
                Phase("leak", "echo-args", [Secret]),
            ],
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);

        // Checked over the whole runner, so the harmless step before it never ran either: a run
        // that refuses on its second step has already changed the tree.
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", RunId)));

        // The argument list reaches the log header, the machine's process table and every error
        // that quotes the command. A refusal that named the value would be the leak it prevents.
        Assert.DoesNotContain(Secret, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretACommandPrinted_ReachesTheChildAndNothingElse()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        WriteSecret(temp);

        // The child prints the variable it was handed, which is the one path a secret can still
        // take into a log: a harness cannot stop a program echoing its own environment.
        var runner = new RunnerConfig
        {
            Phases = [Phase("print", "print-env", ["HARNESS_TOKEN"], successPattern: "nothing matches this")],
        };

        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken);

        var log = await File.ReadAllTextAsync(result.Phases[0].LogFile, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Secret, log, StringComparison.Ordinal);
        Assert.Contains(ActionValues.Mask, log, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Verdict.Detail, StringComparison.Ordinal);

        // The child did receive the real value; a masked credential would fail later and somewhere
        // else, where the failure says nothing about what was wrong.
        Assert.DoesNotContain("<unset>", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpectedExceptionWhoseCheckIsNotConfirmed_LeavesTheFailureGenuine()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var runner = Excusable();
        var request = Request(temp, runner) with
        {
            InvokeRunner = (_, _) => Task.FromResult(RunOutcome.Failed(20, "the other runner failed too")),
        };

        var result = await Service(factory).RunAsync(Config(), request, TestContext.Current.CancellationToken);

        Assert.NotNull(result.ExpectedException);
        Assert.NotNull(result.Gate);
        Assert.False(result.Gate.Confirmed);

        // Unconfirmed, the failure stays genuine. An unconditional excusal hides the regression it
        // was written to explain, and hides it best on the day that regression appears.
        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
        Assert.False(result.Outcome.Success);
        Assert.DoesNotContain("a known confound", result.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpectedExceptionWhoseCheckConfirmsIt_ReportsTheDeclaredOutcome()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var request = Request(temp, Excusable()) with
        {
            InvokeRunner = (_, _) => Task.FromResult(RunOutcome.Ok("the other runner reproduced it")),
        };

        var result = await Service(factory).RunAsync(Config(), request, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Gate);
        Assert.True(result.Gate.Confirmed, string.Join("; ", result.Gate.Reasons));
        Assert.False(result.Gate.Unconditional);

        // The step counted is the failing step's own, inside its own window. A once-per-run sample
        // was measured excusing and charging the same failure on the same day.
        Assert.Contains("inside the window", result.Gate.Reasons[0], StringComparison.Ordinal);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.True(result.Outcome.Success);
        Assert.True(result.Outcome.Warning);
        Assert.Equal("a known confound", result.Outcome.Message);
    }

    [Fact]
    public async Task AnExpectedExceptionWithNothingWiredToInvokeItsCheck_ExcusesNothing()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // No InvokeRunner: the check cannot be run, so it cannot pass, so the failure stays genuine.
        var result = await Service(factory).RunAsync(
            Config(),
            Request(temp, Excusable()),
            TestContext.Current.CancellationToken);

        Assert.False(result.Gate!.Confirmed);
        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task ARunResumedInASecondSegment_ReportsTheUnionAcrossBoth()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var service = Service(factory);
        var token = TestContext.Current.CancellationToken;

        var aborting = new RunnerConfig
        {
            Phases =
            [
                Phase("step-1", "echo-args", ["one"]),
                Phase("step-2", "exit", ["7"]),
                Phase("step-3", "echo-args", ["three"]),
            ],
        };

        var first = await service.RunAsync(Config(), Request(temp, aborting), token);

        Assert.Equal(LegVerdict.Failed, first.Verdict.Verdict);
        Assert.Equal(2, first.Phases.Count);

        // The second attempt skips what the first carried to an outcome, failures included: a suite
        // that ran two thirds of itself, aborted and finished the rest did the whole suite once.
        var second = await service.RunAsync(
            Config(),
            Request(temp, aborting) with { SegmentId = "s2" },
            token);

        Assert.Single(second.Phases);
        Assert.Equal("step-3", second.Phases[0].Phase);

        Assert.Equal(["step-1", "step-2", "step-3"], second.Union.Keys.Order(StringComparer.Ordinal));
        Assert.True(second.Union["step-1"].Success);
        Assert.False(second.Union["step-2"].Success);
        Assert.True(second.Union["step-3"].Success);

        // Every step this attempt ran passed, and the run is still red: reporting only the third of
        // the suite that happened to run last is how a resumed run reports a failure as green.
        Assert.Equal(LegVerdict.Failed, second.Verdict.Verdict);
        Assert.Contains("step-2", second.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActionFilesStepsRun_AndItsPredefinedActionsAreNamedRatherThanPassedOver()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // The program is the bare name of a declared tool; the assembly it runs is an argument, and
        // it is quoted because the splitter honours double quotes and nothing else, so a path with a
        // space in it stays one token.
        await WriteActionAsync(factory, temp, $"""
            name: corpus
            steps:
              - name: fetch
                uses: harness/checkout
              - name: measure
                successPattern: measured
                run: |
                  dotnet exec "{TestHost.AssemblyPath.Replace('\\', '/')}" measured
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var runner = new RunnerConfig
        {
            Action = "corpus/corpus.yml",
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TestHost.ChildModeVariable] = "echo-args",
            },
        };

        var result = await Service(factory).RunAsync(config, Request(temp, runner), TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.Single(result.Phases);
        Assert.Equal(["fetch (harness/checkout)"], result.PerformedActions);
    }

    [Fact]
    public async Task RequireBuild_IsReportedRatherThanActedOn()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var runner = new RunnerConfig
        {
            RequireBuild = true,
            Phases = [Phase("measure", "echo-args", ["measured"], successPattern: "measured")],
        };

        var result = await Service(factory).RunAsync(Config(), Request(temp, runner), TestContext.Current.CancellationToken);

        // Syncing and building belong to the orchestrator: a service that did either itself would do
        // it once per leg on a tree that legs share.
        Assert.True(result.RequireBuild);
        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task TwoStepsSharingAName_AreRefused()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var runner = new RunnerConfig
        {
            Phases =
            [
                Phase("measure", "echo-args", ["one"]),
                Phase("measure", "echo-args", ["two"]),
            ],
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            Config(),
            Request(temp, runner),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("measure", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepNamedWithASlash_StillWritesItsOwnLogFile()
    {
        // A step whose run block holds several lines is named "step (1/2)", and that slash is a
        // directory separator on every platform this tool runs on.
        Assert.Equal("build (1-2)", RunnerRunService.LogNameFor("build (1/2)"));
        Assert.Equal("build (2-2)", RunnerRunService.LogNameFor("build (2/2)"));
    }

    [Fact]
    public void AFailureThatNamedNoException_CarriesANameAnEntryCanDeclare()
    {
        Assert.Equal(RunnerRunService.StepFailureType, RunnerRunService.FailureTypeIn("ctest exited 7"));
        Assert.Equal("System.IO.IOException", RunnerRunService.FailureTypeIn("Unhandled: System.IO.IOException: gone"));
    }

    /// <summary>
    /// The production half of matching a run check against what a step printed. Without this the
    /// gate has nothing to match and the feature does nothing.
    /// </summary>
    [Fact]
    public async Task APassingRun_CarriesWhatItsStepsPrinted_OnItsOutcome()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: measure
                run: |
                  dotnet --version
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var result = await Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.NotNull(result.Outcome.Output);
        Assert.Contains(result.Outcome.Output!, result.Outcome.Texts);
    }

    /// <summary>
    /// A step naming runOn runs on a leg of a system it names, and on no other. Left out, it is left
    /// out before anything reads it - here a program nobody declared, which the policy refuses the
    /// whole file over where the step runs - and the leg says so as it runs and on its line.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStepNamingRunOn_RunsOnlyOnTheSystemsItNames(bool onThisSystem)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: everywhere
                run: |
                  dotnet --version
              - name: fetch
                runOn: [windows, macos]
                run: |
                  curl https://example.invalid
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var request = Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }) with
        {
            Identity = Identity(onThisSystem ? "windows" : "linux"),
        };

        if (onThisSystem)
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(
                () => Service(factory).RunAsync(config, request, TestContext.Current.CancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("curl", refusal.Message, StringComparison.Ordinal);

            return;
        }

        var result = await Service(factory).RunAsync(config, request, TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
        Assert.Equal(["fetch"], result.Entry.SkippedSteps);
        Assert.Contains(
            $"run: {Leg}: skipped 'fetch', which runs on windows, macos only",
            factory.StandardOutput.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg on whose system no step runs would pass having run nothing, so it is refused with
    /// nothing run, naming the leg and its system. <c>run</c> refuses it before any host is
    /// measured; this is the same refusal where a leg reaches the runner anyway.
    /// </summary>
    [Fact]
    public async Task ALegOnWhoseSystemNoStepRuns_IsRefused_NotPassed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: msvc
                runOn: [windows]
                run: |
                  dotnet --version
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }) with { Identity = Identity("linux") },
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"leg '{Leg}' (linux)", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("would run nothing and pass", refusal.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", RunId)));
    }

    /// <summary>
    /// A step naming runOn needs a leg's operating system to be chosen by, and a run reaching no leg
    /// has none: refused rather than guessed, as a step asking for contention is.
    /// </summary>
    [Fact]
    public async Task AStepNamingRunOn_IsRefused_WhereTheRunReachesNoLeg()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        await WriteActionAsync(factory, temp, """
            name: corpus
            steps:
              - name: measure
                runOn: [linux]
                run: |
                  dotnet --version
            """);

        var config = Config();
        config.Tools.Add(new ToolConfig { Name = "dotnet" });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory).RunAsync(
            config,
            Request(temp, new RunnerConfig { Action = "corpus/corpus.yml" }),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("names runOn", refusal.Message, StringComparison.Ordinal);
    }

    private static LegIdentity Identity(string os)
        => new(Leg, os, "x86_64", "gcc", "release", "gcc-release", "local", RunId);

    private static RunnerRunService Service(HarnessFactory factory)
        => new(
            new PhaseRunner(factory.ProcessRunner, factory.FileSystem, factory.Output),
            new ActionFileParser(factory.FileSystem, factory.Output, factory.Platform),
            new ActionToolPolicy(factory.Platform),
            new ActionValuesReader(factory.FileSystem, factory.Output),
            new ExpectedExceptionMatcher(factory.Output),
            new RunCheckGate(factory.Output),
            new RunSegments(factory.FileSystem, factory.Output),
            new PredefinedActionRunner(factory.GitClient, factory.Output),
            new InputFingerprint(factory.FileSystem, factory.Platform),
            new ProcessSampler(factory.ProcessTable, factory.Platform, factory.Output),
            factory.GitClient,
            factory.Platform,
            factory.FileSystem,
            factory.Output);

    private static HarnessConfig Config() => new()
    {
        Defaults = new HarnessDefaults { StallSeconds = 0 },
    };

    private static RunnerRunRequest Request(TempDirectory temp, RunnerConfig runner) => new()
    {
        RunnerName = "corpus",
        Runner = runner,
        Leg = Leg,
        Layout = new HarnessLayout(temp.Path, temp.Path),
        RunId = RunId,
        SegmentId = "s1",
        TreeRoot = temp.Path,
        ResolvedLegs = [Leg],
    };

    /// <summary>
    /// A phase that runs this assembly as a child in <paramref name="mode"/>, exactly as the process
    /// tests do, so what the step says and what it exits with are what the test decided.
    /// </summary>
    private static RunnerPhase Phase(
        string name,
        string mode,
        IReadOnlyList<string> arguments,
        string? successPattern = null)
        => new()
        {
            Name = name,
            Command = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, .. arguments],
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TestHost.ChildModeVariable] = mode,
            },
            SuccessPattern = successPattern,
        };

    /// <summary>
    /// A runner carrying an entry for the failure its only step produces, gated on one check.
    /// </summary>
    private static RunnerConfig Excusable() => new()
    {
        Phases = [Phase("measure", "exit", ["7"])],
        ExpectedExceptions =
        [
            new ExpectedException
            {
                ExceptionType = RunnerRunService.StepFailureType,
                Messages = ["exited 7"],
                Success = true,
                Warning = true,
                ResultCode = 0,
                Message = "a known confound",
                EarnedOn = "lin-gcc-release",
                EarnedAt = "2026-09-16",
                Mechanism = "the fixture server refuses the seventh connection of a session",
                Anchor = "D-TEST-RUNNER-GATE",
                RunChecks =
                [
                    new RunCheck
                    {
                        PredefinedRunner = "reproduce",
                        Expects = new RunCheckExpectation { Success = true },
                        MinStepsInFailureWindow = 1,
                        MinStepSeconds = 0,
                    },
                ],
            },
        ],
    };

    private static async Task WriteActionAsync(
        HarnessFactory factory,
        TempDirectory temp,
        string yaml,
        bool ignoreBuild = true,
        bool ignoreArtifacts = true)
    {
        WriteActionFile(temp, yaml);
        await RepositoryAsync(factory, temp, ignoreBuild, ignoreArtifacts);
    }

    private static void WriteActionFile(TempDirectory temp, string yaml)
        => temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "corpus", "corpus.yml"), yaml);

    /// <summary>
    /// Makes <paramref name="temp"/> a repository that ignores what a run writes, as init leaves one
    /// - or leaves out a rule, for a test about a repository whose .gitignore predates it. A run
    /// asks git before it writes an action's scratch, and a run always happens inside a repository.
    /// </summary>
    private static async Task RepositoryAsync(HarnessFactory factory, TempDirectory temp, bool ignoreBuild, bool ignoreArtifacts)
    {
        if (!Directory.Exists(temp.Combine(".git")))
        {
            await factory.RunGitAsync(temp.Path, ["init", "--quiet", "."], TestContext.Current.CancellationToken);
        }

        var rules = new List<string>();

        if (ignoreBuild)
        {
            rules.Add(HarnessLayout.ActionScratchIgnoreRule(HarnessLayout.ActionBuildDirectoryName));
        }

        if (ignoreArtifacts)
        {
            rules.Add(HarnessLayout.ActionScratchIgnoreRule(HarnessLayout.ActionArtifactsDirectoryName));
        }

        temp.WriteFile(".gitignore", string.Join("\n", rules) + "\n");
    }

    private static void WriteSecret(TempDirectory temp)
        => temp.WriteFile(
            Path.Combine(".harness-config", "runner", ".secrets", "ci.env"),
            $"HARNESS_TOKEN={Secret}\n");
}
