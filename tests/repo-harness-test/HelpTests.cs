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

        foreach (var setting in new[] { "buildCores", "testCores", "maxParallelLegs", "sanitizer", "hosts", "emulators" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains(
            $"({HarnessDefaults.DefaultCores.ToString(CultureInfo.InvariantCulture)} each)",
            result.StandardOutput,
            StringComparison.Ordinal);
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

        var expected = AnchorStatus.Cells
            .Concat(["write-anchor", "set-anchor", "read-anchor", "read-anchors", "check-anchor-balance", "--anchor-dry-run"])
            .Concat([AnchorSettings.DefaultPendingAnchorsPath, AnchorSettings.DefaultDoneAnchorsPath, AnchorRegistryDocument.TableHeader]);

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
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
