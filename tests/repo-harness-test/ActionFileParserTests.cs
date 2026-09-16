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

    [Fact]
    public async Task LoadAsync_Refuses_AnActionNameThatIsAPath()
    {
        using var temp = new TempDirectory();
        var parser = CreateParser();

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => parser.LoadAsync(temp.Path, "../elsewhere/steal.yaml", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("is not a file name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheFileFromTheActionsDirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("build.yaml", "steps:\n  - name: build\n    run: cmake --build build\n");

        var action = await CreateParser().LoadAsync(temp.Path, "build.yaml", TestContext.Current.CancellationToken);

        Assert.Equal("build", Assert.Single(action.Steps).Name);
    }

    private static ActionFileParser CreateParser()
        => new(
            new PhysicalFileSystem(FilePermissionsFactory.Create()),
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));

    private static ActionFile Parse(string text)
        => CreateParser().Parse("actions/build.yaml", text);

    private static HarnessException Refused(string text)
        => Assert.Throws<HarnessException>(() => Parse(text));
}
