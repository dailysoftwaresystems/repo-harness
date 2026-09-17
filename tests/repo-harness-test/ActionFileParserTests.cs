using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>What a runner's action file may say, and what it is refused for saying.</summary>
public sealed class ActionFileParserTests
{
    [Fact]
    public void Parse_ReadsAWholeFile()
    {
        var action = Parse("""
            name: corpus
            description: builds and runs the corpus
            inputs:
              corpusRoot:
                default: corpus
                required: true
                description: where the corpus lives
            steps:
              - name: fetch
                uses: harness/checkout
                ref: main
              - name: build
                run: cmake --build build
                workingDirectory: sub
                env:
                  CC: clang
                successPattern: "^Built target"
                stallSeconds: 300
                continueOnError: true
            """);

        Assert.Equal("corpus", action.Name);
        Assert.Equal("builds and runs the corpus", action.Description);

        var input = Assert.Single(action.Inputs);
        Assert.Equal("corpusRoot", input.Name);
        Assert.Equal("corpus", input.Default);
        Assert.True(input.Required);

        Assert.Equal(2, action.Steps.Count);
        Assert.Equal(PredefinedAction.Checkout, action.Steps[0].Uses);
        Assert.Equal("main", action.Steps[0].Reference);
        Assert.Empty(action.Steps[0].Commands);

        var build = action.Steps[1];
        Assert.Equal(PredefinedAction.None, build.Uses);
        Assert.Equal(["cmake", "--build", "build"], Assert.Single(build.Commands).Arguments);
        Assert.Equal("sub", build.WorkingDirectory);
        Assert.Equal("clang", build.Env["CC"]);
        Assert.Equal("^Built target", build.SuccessPattern);
        Assert.Equal(300, build.StallSeconds);
        Assert.True(build.ContinueOnError);
    }

    [Fact]
    public void Parse_TrimsEveryLineAndSkipsBlanksAndComments_SoIndentationCannotChangeWhatRuns()
    {
        var action = Parse("""
            steps:
              - name: build
                run: |
                  cmake --build build

                      # the comment is not a program

                        cmake --install build
            """);

        var commands = Assert.Single(action.Steps).Commands;

        Assert.Equal(2, commands.Count);
        Assert.Equal(["cmake", "--build", "build"], commands[0].Arguments);
        Assert.Equal(["cmake", "--install", "build"], commands[1].Arguments);
    }

    [Fact]
    public void Parse_KeepsAQuotedArgumentWhole_AndStripsTheQuotes()
    {
        var action = Parse("""
            steps:
              - name: build
                run: cmake --build "my build dir" --target all
            """);

        Assert.Equal(
            ["cmake", "--build", "my build dir", "--target", "all"],
            Assert.Single(Assert.Single(action.Steps).Commands).Arguments);
    }

    [Fact]
    public void Parse_LeavesSingleQuotesLiteral_BecauseTheSplitterHonoursDoubleQuotesOnly()
    {
        // Measured on System.CommandLine 2.0.12: 'my dir' splits to ['my and dir'. The quotes are
        // ordinary characters, so a single-quoted path is two arguments, neither of them a path.
        var action = Parse("""
            steps:
              - name: build
                run: cmake --build 'my dir'
            """);

        Assert.Equal(
            ["cmake", "--build", "'my", "dir'"],
            Assert.Single(Assert.Single(action.Steps).Commands).Arguments);
    }

    [Fact]
    public void Parse_Refuses_AnUnbalancedDoubleQuote_RatherThanLettingTheLineBeTruncated()
    {
        // The measurement this refusal exists for: SplitCommandLine returns [dotnet, build] for
        // this line and drops '-c Release' with no error, so the build would run, exit zero, and
        // have built the wrong configuration.
        Assert.Equal(
            ["dotnet", "build"],
            System.CommandLine.Parsing.CommandLineParser
                .SplitCommandLine("""dotnet build "unterminated -c Release""")
                .ToArray());

        var exception = Refused("""
            steps:
              - name: build
                run: |
                  dotnet build "unterminated -c Release
            """);

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains(
            """line 4: 'dotnet build "unterminated -c Release' has an unbalanced double quote.""",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "drops everything after an unterminated quote without a word",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_AnUnknownTopLevelKey()
    {
        var exception = Refused("""
            runs-on: ubuntu-latest
            steps:
              - name: build
                run: cmake --build build
            """);

        Assert.Contains("'runs-on' is not a top-level key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Known: 'name', 'description', 'inputs', 'steps'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_AnUnknownStepKey_BecauseAnIgnoredKeyIsARuleNobodyApplied()
    {
        var exception = Refused("""
            steps:
              - name: build
                run: cmake --build build
                working-directory: sub
            """);

        Assert.Contains("'working-directory' is not a step key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'workingDirectory'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_AStepThatDeclaresBothUsesAndRun()
    {
        var exception = Refused("""
            steps:
              - name: build
                uses: harness/checkout
                run: cmake --build build
            """);

        Assert.Contains("declares both 'uses' and 'run'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_AStepThatDeclaresNeither()
    {
        var exception = Refused("""
            steps:
              - name: nothing
                workingDirectory: sub
            """);

        Assert.Contains("declares neither 'uses' nor 'run'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_AnUnknownUses_NamingWhatIsAvailable()
    {
        var exception = Refused("""
            steps:
              - name: fetch
                uses: actions/checkout@v4
            """);

        Assert.Contains("'actions/checkout@v4' is not a predefined action", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'harness/checkout', 'harness/read-inputs'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_ARefOnAnActionThatDoesNotTakeOne()
    {
        var exception = Refused("""
            steps:
              - name: values
                uses: harness/read-inputs
                ref: main
            """);

        Assert.Contains("'ref' applies only to 'harness/checkout'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_EmptyOrMissingSteps()
    {
        Assert.Contains("'steps' is empty", Refused("steps: []").Message, StringComparison.Ordinal);
        Assert.Contains("declares no 'steps'", Refused("name: nothing").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_ARunBlockWithNoCommandInIt()
    {
        var exception = Refused("""
            steps:
              - name: build
                run: |
                  # only a comment

            """);

        Assert.Contains("holds no command", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_ASuccessPatternThatIsEmptyOrDoesNotCompile()
    {
        Assert.Contains(
            "'successPattern' is empty",
            Refused("""
                steps:
                  - name: build
                    run: cmake --build build
                    successPattern: ""
                """).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "'successPattern' does not compile",
            Refused("""
                steps:
                  - name: build
                    run: cmake --build build
                    successPattern: "Built ([0-9]+"
                """).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_ANegativeOrNonNumericStallBound()
    {
        Assert.Contains(
            "a stall bound is never negative",
            Refused("""
                steps:
                  - name: build
                    run: cmake --build build
                    stallSeconds: -1
                """).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "not a whole number of seconds",
            Refused("""
                steps:
                  - name: build
                    run: cmake --build build
                    stallSeconds: soon
                """).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsEveryProblemTogether()
    {
        var exception = Refused("""
            runs-on: ubuntu-latest
            steps:
              - name: one
                run: cmake --build "build
              - name: two
                stallSeconds: -5
                run: cmake --install build
            """);

        Assert.Contains("has 3 problem(s)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'runs-on'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unbalanced double quote", exception.Message, StringComparison.Ordinal);
        Assert.Contains("never negative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_YamlThatDoesNotParse()
    {
        var exception = Refused("steps:\n  - name: build\n   run: x\n");

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("not valid YAML", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToPhases_CarriesEveryFieldThePhaseContractDependsOn()
    {
        // An action file replaces 'phases', so a field that could not survive this conversion is a
        // field the verdict contract silently lost.
        var action = Parse("""
            steps:
              - name: build
                run: |
                  cmake --build build
                  cmake --install build
                workingDirectory: sub
                env:
                  CC: clang
                successPattern: "^Built"
                stallSeconds: 120
                continueOnError: true
            """);

        var phases = action.ToPhases();

        Assert.Equal(2, phases.Count);
        Assert.Equal("build (1/2)", phases[0].Name);
        Assert.Equal("build (2/2)", phases[1].Name);

        foreach (var phase in phases)
        {
            Assert.Equal("sub", phase.WorkingDirectory);
            Assert.Equal("clang", phase.Env["CC"]);
            Assert.Equal(120, phase.StallSeconds);
            Assert.True(phase.ContinueOnError);
        }

        // The witness belongs to the step, so it is checked against the step's last command: the one
        // whose finishing means the step did its work. Copied to every line, a step would have to
        // make each of its commands print the same evidence, which no honest sequence does.
        Assert.Null(phases[0].SuccessPattern);
        Assert.Equal("^Built", phases[1].SuccessPattern);

        Assert.Equal(["cmake", "--build", "build"], phases[0].Command);
        Assert.Equal("build", Assert.Single(Parse("""
            steps:
              - name: build
                run: cmake --build build
            """).ToPhases()).Name);
    }

    /// <summary>
    /// The measured default, kept: a step with neither key runs where every step ran before this
    /// layout existed. The new directory reads as though a step ran beside its own file, and a
    /// default that quietly became that would have broken every root-relative line already written.
    /// </summary>
    [Fact]
    public void AStepThatNamesNoRoot_StillRunsAtTheTreeRoot()
    {
        var phase = Assert.Single(Parse("""
            steps:
              - name: build
                run: cmake --build build
            """).ToPhases());

        Assert.Null(phase.WorkingDirectory);
    }

    [Fact]
    public void AStepThatNamesNoRoot_KeepsItsOwnPathRelativeToTheTree()
    {
        var phase = Assert.Single(Parse("""
            steps:
              - name: build
                workingDirectory: sub
                run: cmake --build build
            """).ToPhases());

        Assert.Equal("sub", phase.WorkingDirectory);
    }

    [Theory]
    [InlineData("tree", null, null)]
    [InlineData("tree", "sub", "sub")]
    [InlineData("harness", null, ".harness-config")]
    [InlineData("harness", "sub", ".harness-config/sub")]
    [InlineData("action", null, ".harness-config/runner/actions/build")]
    [InlineData("action", "lib", ".harness-config/runner/actions/build/lib")]
    public void AStepResolvesItsWorkingDirectoryAgainstTheRootItNames(string root, string? path, string? expected)
    {
        var declared = path is null ? string.Empty : $"\n    workingDirectory: {path}";

        var phase = Assert.Single(Parse($"""
            steps:
              - name: build
                workingDirectoryRoot: {root}{declared}
                run: cmake --build build
            """).ToPhases());

        // Compared with separators normalised: the value is built with the platform's own, and what
        // matters is which directory it names, not which slash this machine writes it with.
        Assert.Equal(expected, phase.WorkingDirectory?.Replace('\\', '/'));
    }

    /// <summary>
    /// A rooted second argument makes <c>Path.Combine</c> discard the first, so a step could name a
    /// root, be composed against it, and run somewhere else entirely with nothing refusing it.
    /// </summary>
    [Theory]
    [InlineData("C:\\Windows\\System32", "absolute path")]
    [InlineData("/etc", "absolute path")]
    [InlineData("\\\\server\\share", "absolute path")]
    [InlineData("../../../../Windows", "climbs out")]
    [InlineData("lib/../../../elsewhere", "climbs out")]
    public void Parse_Refuses_AWorkingDirectoryThatLeavesTheRootItNames(string path, string expected)
    {
        var exception = Refused($"""
            steps:
              - name: build
                workingDirectoryRoot: action
                workingDirectory: {path}
                run: cmake --build build
            """);

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rule with no root declared, where the default tree root is the one being left.
    /// </summary>
    [Fact]
    public void Parse_Refuses_AWorkingDirectoryThatLeavesTheTreeRoot()
    {
        var exception = Refused("""
            steps:
              - name: build
                workingDirectory: ../../elsewhere
                run: cmake --build build
            """);

        Assert.Contains("climbs out", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A predefined action is performed by the harness, never started as a program, so it has no
    /// working directory. Accepted silently, the key would be a rule nobody applied — the same
    /// reason 'ref' is refused on an action that does not take one.
    /// </summary>
    [Theory]
    [InlineData("workingDirectory: sub")]
    [InlineData("workingDirectoryRoot: action")]
    public void Parse_Refuses_AWorkingDirectoryOnAStepThatUsesAPredefinedAction(string declared)
    {
        var exception = Refused($"""
            steps:
              - name: checkout
                uses: harness/checkout
                ref: main
                {declared}
            """);

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("applies only to a step's 'run' block", exception.Message, StringComparison.Ordinal);
        Assert.Contains("harness/checkout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Refuses_AWorkingDirectoryRootThatIsNotOne_NamingWhatIsAvailable()
    {
        var exception = Refused("""
            steps:
              - name: build
                workingDirectoryRoot: repository
                run: cmake --build build
            """);

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("'repository' is not a working directory root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'tree', 'harness', 'action'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_ReadsAnActionFromItsOwnDirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            Path.Combine("build", "build.yml"),
            "steps:\n  - name: build\n    run: cmake --build build\n");

        var action = await CreateParser()
            .LoadAsync(temp.Path, "build/build.yml", TestContext.Current.CancellationToken);

        Assert.Equal("build", Assert.Single(action.Steps).Name);
        Assert.Equal("build", action.DirectoryName);
    }

    [Fact]
    public async Task LoadAsync_ReadsAnActionSpelledWithTheOtherExtension()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            Path.Combine("build", "build.yaml"),
            "steps:\n  - name: build\n    run: cmake --build build\n");

        var action = await CreateParser()
            .LoadAsync(temp.Path, "build/build.yaml", TestContext.Current.CancellationToken);

        Assert.Equal("build", Assert.Single(action.Steps).Name);
    }

    /// <summary>
    /// The layout this replaced. Refused rather than accepted alongside the new one: two spellings
    /// would mean two places to look for one action, and the supporting files a flat directory
    /// cannot own are the reason the directory exists.
    /// </summary>
    [Fact]
    public async Task LoadAsync_Refuses_TheFlatSpelling_NamingThePathItExpected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("build.yml", "steps:\n  - name: build\n    run: cmake --build build\n");

        var exception = await Refused(temp, "build.yml");

        Assert.Contains("build/build.yml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_Refuses_AFileThatDoesNotCarryItsDirectorysName_NamingBoth()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            Path.Combine("probe", "steps.yml"),
            "steps:\n  - name: build\n    run: cmake --build build\n");

        var exception = await Refused(temp, "probe/steps.yml");

        Assert.Contains("'probe'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'steps.yml'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("probe/probe.yml", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.yml")]
    [InlineData("a/../../outside.yml")]
    [InlineData("../actions-evil/x.yml")]
    [InlineData("build/nested/build.yml")]
    [InlineData("build/build.txt")]
    [InlineData("./build/build.yml")]
    public async Task LoadAsync_Refuses_APathThatDoesNotNameAnActionInsideTheDirectory(string action)
    {
        using var temp = new TempDirectory();

        _ = await Refused(temp, action);
    }

    [Fact]
    public async Task LoadAsync_Refuses_AnAbsolutePath()
    {
        using var temp = new TempDirectory();
        var elsewhere = temp.WriteFile(
            Path.Combine("elsewhere", "steal.yml"),
            "steps:\n  - name: build\n    run: cmake --build build\n");

        _ = await Refused(temp, elsewhere);
    }

    /// <summary>
    /// A sibling directory whose name merely starts with the actions directory's, reached the only
    /// way a legal spelling can reach anything outside: through a link.
    /// </summary>
    /// <remarks>
    /// Spelled with '..' this never reaches the containment check at all — the spelling rule refuses
    /// it first, and the test would then pass with that check deleted. A link is what makes the
    /// boundary do the work: 'actions-evil' shares every character of 'actions' and differs only
    /// after it, so a prefix test that did not stop at a directory separator would call this file
    /// contained, and it is one nobody reviewing the actions directory ever sees.
    /// </remarks>
    [Fact]
    public async Task LoadAsync_Refuses_ALinkLeadingToASiblingDirectorySharingThePrefix()
    {
        using var temp = new TempDirectory();
        var actions = temp.Combine("actions");
        var outside = temp.WriteFile(
            Path.Combine("actions-evil", "probe.yml"),
            "steps:\n  - name: build\n    run: cmake --build build\n");

        Directory.CreateDirectory(Path.Combine(actions, "probe"));

        try
        {
            File.CreateSymbolicLink(Path.Combine(actions, "probe", "probe.yml"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => CreateParser().LoadAsync(
                actions,
                "probe/probe.yml",
                TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("actions-evil", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spelling is legal and the file is where it should be; only following the link says
    /// otherwise. Nothing but the file system knows this, which is why the check survives into the
    /// parser rather than living only in the configuration reader.
    /// </summary>
    [Fact]
    public async Task LoadAsync_Refuses_ALinkInsideTheDirectory_ThatLeadsOutOfIt()
    {
        using var temp = new TempDirectory();
        var outside = temp.WriteFile(
            Path.Combine("outside", "probe.yml"),
            "steps:\n  - name: build\n    run: cmake --build build\n");

        Directory.CreateDirectory(temp.Combine("actions", "probe"));

        try
        {
            File.CreateSymbolicLink(temp.Combine("actions", "probe", "probe.yml"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => CreateParser().LoadAsync(
                temp.Combine("actions"),
                "probe/probe.yml",
                TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("outside", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step asks for the guards the build and test verbs carry, and gets them off unless it does.
    /// </summary>
    [Fact]
    public void AStepAsksForItsGuards_AndHasNoneUnlessItDoes()
    {
        var asked = Parse("""
            name: build
            steps:
              - name: compile
                run: |
                  cmake --build .
                watchContention: true
                requireInputsUnmoved: true
            """).Steps[0];

        Assert.True(asked.WatchContention);
        Assert.True(asked.RequireInputsUnmoved);

        var silent = Parse("""
            name: build
            steps:
              - name: compile
                run: |
                  cmake --build .
            """).Steps[0];

        Assert.False(silent.WatchContention);
        Assert.False(silent.RequireInputsUnmoved);
    }

    /// <summary>
    /// Anything that is not exactly true or false is refused, never read as false. These keys turn
    /// guards on, so a misspelling read as "off" is a step that looks guarded in the file and is
    /// not \u2014 which is the failure the guards exist to make impossible.
    /// </summary>
    [Theory]
    [InlineData("watchContention")]
    [InlineData("requireInputsUnmoved")]
    public void AGuardKeyThatSaysSomethingElse_IsRefused_NotReadAsOff(string key)
    {
        var refusal = Refused($"""
            name: build
            steps:
              - name: compile
                run: |
                  cmake --build .
                {key}: yes
            """);

        Assert.Contains(key, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'true', 'false'", refusal.Message, StringComparison.Ordinal);
    }

    private static ActionFileParser CreateParser()
        => new(
            new PhysicalFileSystem(FilePermissionsFactory.Create()),
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false),
            new HostPlatform());

    private static ActionFile Parse(string text)
        => CreateParser().Parse(Path.Combine("actions", "build", "build.yml"), text);

    private static HarnessException Refused(string text)
        => Assert.Throws<HarnessException>(() => Parse(text));

    /// <summary>
    /// Asserts <paramref name="action"/> is refused when read from <paramref name="temp"/>, and
    /// returns the refusal so a caller can assert on what it said.
    /// </summary>
    private static async Task<HarnessException> Refused(TempDirectory temp, string action)
    {
        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => CreateParser().LoadAsync(temp.Path, action, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);

        return exception;
    }
}
