using System.Globalization;
using System.Text.RegularExpressions;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Help is treated as documentation, so it is tested like documentation: every
/// command must appear in it, and every documented value must come from the code
/// that produces it rather than from prose someone has to remember to update.
/// </summary>
public sealed partial class HelpTests
{
    [Fact]
    public async Task EveryListedCommand_HasItsOwnWorkingHelp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listed = await ListCommandsAsync(cancellationToken);

        Assert.NotEmpty(listed);

        foreach (var command in listed)
        {
            var result = await CliRunner.RunAsync([command, "--help"], cancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(command, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryListedCommand_HasADescription()
    {
        var result = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        var lines = CommandLines(result.StandardOutput).ToList();
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            // "  name   description" - a command with no description is undocumented.
            var described = CommandLinePattern().Match(line);

            Assert.True(
                described.Success && described.Groups["description"].Value.Trim().Length > 10,
                $"command line has no usable description: '{line.Trim()}'");
        }
    }

    [Fact]
    public async Task ExitCodeTopic_DocumentsEverySharedExitCode()
    {
        var result = await CliRunner.RunAsync(
            ["help", "exit-codes"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);

        // Generated from the constants, so a new exit code cannot ship undocumented.
        foreach (var description in HarnessExit.All)
        {
            Assert.Contains(description.Name, result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(
                description.Code.ToString(CultureInfo.InvariantCulture),
                result.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConfigTopic_DocumentsCoresAndParallelLegs_WithTheRealDefault()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var setting in new[] { "buildCores", "testCores", "maxParallelLegs", "sanitizer", "hosts", "emulators", "developerEnvironments" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"({HarnessDefaults.DefaultCores.ToString(CultureInfo.InvariantCulture)} each)",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The config topic says a test count that differs is marked on its own, never as a timing, and
    /// how a set that differs on purpose is declared.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysATestCountThatDiffers_IsNoTimingMark_AndHowATestSetIsNamed()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "and test set is marked on its own - 'test count differs', and testCountDiffers and",
            "testCountNote in --json - never as a timing, and never changes a verdict either. A",
            "\"windows\": { \"testSet\": \"windows\" }",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says how test --filter, --exclude and --label reach a runner, what each is
    /// for ctest, that a label can now be chosen as well as left out, and how several exclusions reach
    /// a runner that would not leave out each of them given apart.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysHowTheTestSelectionReachesTheRunner()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "filterArg, excludeArg and labelArg, so one set of options serves every runner. For",
            "ctest, '-R' chooses tests by name, '-L' chooses them by label and '-LE' leaves a label",
            "apart - ctest leaves out only what every -LE matches - declares excludeJoin, and several",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The config topic says when a combination's build directory is kept and when it starts from
    /// clean, and what rebuildableFormats decides: a consumer found both only by reading the source.
    /// </summary>
    [Fact]
    public async Task ConfigTopic_SaysWhenABuildDirectoryIsKept_AndWhatCountsAsAnInput()
    {
        var result = await CliRunner.RunAsync(["help", "config"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "A combination's directory is kept between builds, and its build system decides",
            "  - an input that changed since that build began is dated no later than the newest",
            "  - nothing can say: no record of what it was built from, the files git tracks",
            "DEPENDS is not remade. A project's rebuildableFormats says which files are inputs,",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WorktreesTopic_QuotesTheLimitsFromTheCode()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);

        Assert.Contains(
            WorktreeSettings.DefaultMaxNameLength.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            HostPlatform.WindowsMaxPath.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);

        foreach (var setting in new[] { "worktrees.maxNameLength", "worktrees.pathBudgetReserve", "worktrees.pathLimit" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says what runOn takes - the systems quoted from the code - where a leg of
    /// another system says it left a step out, and that a leg left with no step is refused.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhatRunOnTakes_AndWhatALegLeftWithoutAStepGets()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);

        foreach (var text in new[] { $"runOn: [{string.Join(", ", PlatformNames.OperatingSystems)}]", "skippedSteps", "refused before anything starts" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The runners topic says where an input's value comes from, in which order, what --input
    /// refuses, and that a secret does not go there.
    /// </summary>
    [Fact]
    public async Task RunnersTopic_SaysWhereAnInputsValueComesFrom()
    {
        var result = await CliRunner.RunAsync(["help", "runners"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "resolved from 'run --input <name>=<value>' first, the",
            "runner value directories second and each input's own 'default' last",
            "--input takes one name=value each time it is given",
            "an empty value and a name given twice",
            "a secret stays in",
        })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The tools topic says what --dry-run does, that init installs only when asked, how a tool
    /// narrows the legs that need it, and that a leg in a developer environment is told about a tool
    /// as the PATH it sets up holds it; the layout topic says init writes the tree it runs in and
    /// names a rule by git's own answer.
    /// </summary>
    [Fact]
    public async Task ToolsAndLayoutTopics_SayWhatInitAndInstallMissingToolsDo()
    {
        var tools = await CliRunner.RunAsync(["help", "tools"], TestContext.Current.CancellationToken);
        var layout = await CliRunner.RunAsync(["help", "layout"], TestContext.Current.CancellationToken);

        foreach (var text in new[]
        {
            "--install-tools' runs it too; plain init installs nothing and says how.",
            "--dry-run reaches and asks every host as a run does, and installs nothing",
            "\"toolchains\": [\"msvc\"]",
            "\"legs\": [\"win-arm\", \"gate\"]",
            "\"processors\": [\"arm64\"]",
            "\"emulators\": [\"qemu-arm64\"]",
            "a developer environment, on the PATH that environment sets up for its processor -",
            "own PATH lacks is unknown for a leg in a developer environment, which this command",
            "covering no declared leg, is refused.",
        })
        {
            Assert.Contains(text, tools.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var text in new[]
        {
            "block on later runs and leaving every other rule untouched. It then asks git which",
            "overrules does nothing there - named only where taking it out would take from git",
            "configuration, a placeholder, an action's files, an anchor registry - and no",
            "the slot a placeholder is kept in is overruled for the slot's contents and needed",
            "init writes the tree it runs in, a worktree's own included",
        })
        {
            Assert.Contains(text, layout.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The legs topic says a toolchain names its compiler, that a build directory is held to the
    /// compiler it was configured with by the file it starts, not by its name, and where a language
    /// only a subproject enables is identified.
    /// </summary>
    [Fact]
    public async Task LegsTopic_SaysAToolchainNamesItsCompiler_AndHowABuildDirectoryIsHeldToIt()
    {
        var result = await CliRunner.RunAsync(["help", "legs"], TestContext.Current.CancellationToken);

        Assert.Contains("A toolchain CMake builds with also names its compiler - CC or CXX under env,", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("One only .NET or Dart builds with names none.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("build directory is then held to the compiler it was configured with by the file that", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("and by its name only where that PATH holds", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"compilerId\": { \"C\": \"MSVC\", \"CXX\": \"MSVC\" }", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("A build CMake configured with another compiler fails before anything is built with", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("loaded: CMakeFiles/<version>/CMake<language>Compiler.cmake - where the record names", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the compiler the answer names and is no newer than the answer", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorktreesTopic_SaysWhenDeleteWorktreeRefuses_AndHowToForceIt()
    {
        var result = await CliRunner.RunAsync(["help", "worktrees"], TestContext.Current.CancellationToken);

        foreach (var text in new[] { "delete-worktree", "uncommitted changes", "skip-worktree", "locked", "--force", $"exits {HarnessExit.Refused}", $"exits {HarnessExit.CommandFailed}" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnchorsTopic_DocumentsEveryStatus_Command_AndDefault()
    {
        var result = await CliRunner.RunAsync(["help", "anchors"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        // Each command as its list gives it, with what it takes: named in prose alone, one was missing
        // from the list a reader scans.
        var expected = AnchorStatus.Cells
            .Concat(["write-anchor", "set-anchor", "read-anchor", "read-anchors", "check-anchor-balance [--base REF]"])
            .Concat(["check-anchor-citations --current-commit|--current-tree|--current-pr", "--anchor-dry-run"])
            .Concat([AnchorSettings.DefaultPendingAnchorsPath, AnchorSettings.DefaultDoneAnchorsPath, AnchorRegistryDocument.TableHeader]);

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The citation check says what it cannot see - a break inside a segment, with no hyphen on either
    /// side - both where its command is described and in the topic that lists it.
    /// </summary>
    [Theory]
    [InlineData("help", "anchors")]
    [InlineData("check-anchor-citations", "--help")]
    public async Task TheCitationCheck_StatesTheOneWrapItCannotSee(string first, string second)
    {
        var result = await CliRunner.RunAsync([first, second], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        // Read as prose: a description is wrapped to the terminal, with its continuation indented.
        var prose = string.Join(' ', result.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("with no hyphen on either side", prose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegsTopic_DocumentsTheNamesTheRulesAndTheExitCodes_FromTheCode()
    {
        var result = await CliRunner.RunAsync(["help", "legs"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        // Every name the validator accepts for a leg is listed, so the help never leaves out a name a leg can use.
        foreach (var name in PlatformNames.OperatingSystems.Concat(PlatformNames.Processors))
        {
            Assert.Contains(name, result.StandardOutput, StringComparison.Ordinal);
        }

        var expected = new[]
        {
            "--legs",
            "host-exec --ssh",
            "--wsl",
            ".harness-config/sshItems/<name>/",
            $".NET {ToolPackage.MinimumSdkMajor} SDK",
            "never downgraded",
            "witness",
        };

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains($"{LegsExit.Unavailable,3}  legs:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{HarnessExit.HostUnavailable,3}  host-exec:", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exit-codes")]
    [InlineData("config")]
    [InlineData("legs")]
    [InlineData("worktrees")]
    [InlineData("anchors")]
    [InlineData("layout")]
    [InlineData("secrets")]
    [InlineData("tools")]
    [InlineData("runners")]
    [InlineData("verdicts")]
    public async Task EveryAdvertisedTopic_Renders(string topic)
    {
        var result = await CliRunner.RunAsync(["help", topic], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutput.Length > 200, $"topic '{topic}' produced little output");
        Assert.DoesNotContain("Unknown topic", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_AdvertisesOnlyTopicsThatExist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var overview = await CliRunner.RunAsync(["help"], cancellationToken);

        Assert.Equal(0, overview.ExitCode);

        var topics = TopicPattern().Matches(overview.StandardOutput);
        Assert.NotEmpty(topics);

        foreach (Match match in topics)
        {
            var topic = match.Groups["topic"].Value;
            var rendered = await CliRunner.RunAsync(["help", topic], cancellationToken);

            Assert.DoesNotContain("Unknown topic", rendered.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnknownTopic_SaysSoAndListsTheRealOnes()
    {
        var result = await CliRunner.RunAsync(
            ["help", "not-a-topic"],
            TestContext.Current.CancellationToken);

        // A mistyped topic is a usage error. Reporting it on stdout with exit 0 would
        // break both "zero always means success" and the pipeable-output rule.
        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("Unknown topic", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("exit-codes", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown topic", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownTopic_Succeeds_AndWritesToStandardOutput()
    {
        var result = await CliRunner.RunAsync(
            ["help", "layout"],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains(".harness-config", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_ExplainsTheGlobalOptions()
    {
        var result = await CliRunner.RunAsync(["help"], TestContext.Current.CancellationToken);

        Assert.Contains("--directory", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--verbose", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Reads the command names out of the root help listing.</summary>
    private static async Task<IReadOnlyList<string>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        var result = await CliRunner.RunAsync(["--help"], cancellationToken);

        return [.. CommandLines(result.StandardOutput)
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .Where(name => name.Length > 0)];
    }

    /// <summary>The lines of the root help listing that describe a command.</summary>
    private static IEnumerable<string> CommandLines(string helpOutput)
    {
        var inCommands = false;

        foreach (var line in helpOutput.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("Commands:", StringComparison.Ordinal))
            {
                inCommands = true;
                continue;
            }

            if (!inCommands)
            {
                continue;
            }

            if (line.Trim().Length == 0)
            {
                break;
            }

            yield return line;
        }
    }

    [GeneratedRegex(@"^\s{2}(?<name>\S+)(\s+<\S+>)?\s{2,}(?<description>.+)$")]
    private static partial Regex CommandLinePattern();

    [GeneratedRegex(@"DssHarness help (?<topic>[a-z-]+)")]
    private static partial Regex TopicPattern();
}
