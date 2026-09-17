using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// The ways the placeholder vocabulary and an action's own names can quietly disagree. Each of
/// these was a way for a step to run with a value nobody intended, or with none at all, and report
/// success either way.
/// </summary>
public sealed class ActionVocabularyGuardTests
{
    /// <summary>
    /// A name the pattern cannot see is a name neither half can refuse: it reaches the program as
    /// its own text, and a tool handed an unreadable path often exits 0 having done nothing.
    /// Runner value directories hold exactly this shape.
    /// </summary>
    [Theory]
    [InlineData("{CORPUS_PATH}")]
    [InlineData("{corpus-path}")]
    [InlineData("{inputs.greeting}")]
    public void ANameCarryingAnUnderscoreDotOrDash_IsSeenAndRefused_NotPassedThrough(string written)
    {
        var refusal = Assert.Throws<HarnessException>(
            () => LegPathNames.RefuseUnknown(written, "'probe' run line"));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("nothing here can fill in", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>And the same name is filled in where something does supply it.</summary>
    [Fact]
    public void ANameCarryingAnUnderscore_IsFilledInWhenSomethingSuppliesIt()
    {
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CORPUS_PATH"] = "/data/corpus-2026",
        };

        var expanded = LegPathNames.Expand(
            "--corpus {CORPUS_PATH}",
            new LegPaths("/tree", "/tree/build/v"),
            "'probe' run line",
            PlaceholderPolicy.Refuse,
            supplied);

        Assert.Equal("--corpus /data/corpus-2026", expanded);
    }

    /// <summary>
    /// An input named for something this tool already fills in would be answered two ways: the run
    /// line would get this tool's value and the environment the action's, and both halves would
    /// report success.
    /// </summary>
    [Theory]
    [InlineData("config")]
    [InlineData("os")]
    [InlineData("product")]
    [InlineData("leg")]
    public void AnInputNamedForSomethingThisToolFillsIn_IsRefusedWhenTheFileIsRead(string name)
    {
        var factory = new HarnessFactory();
        var parser = new ActionFileParser(factory.FileSystem, factory.Output, factory.Platform);

        var refusal = Assert.Throws<HarnessException>(() => parser.Parse("probe.yml", $"""
            name: probe
            inputs:
              {name}:
                default: something
            steps:
              - name: measure
                run: |
                  git --version
            """));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("a name this tool already fills in", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An ordinary input name is not refused, so the rule above is not simply "no inputs".</summary>
    [Fact]
    public void AnInputNamedForSomethingElse_IsAccepted()
    {
        var factory = new HarnessFactory();
        var parser = new ActionFileParser(factory.FileSystem, factory.Output, factory.Platform);

        var file = parser.Parse("probe.yml", """
            name: probe
            inputs:
              corpusRoot:
                default: real-examples/c
            steps:
              - name: measure
                run: |
                  git --version
            """);

        Assert.Equal("corpusRoot", Assert.Single(file.Inputs).Name);
    }

    /// <summary>
    /// A step's name becomes a directory under the action's build directory as well as a log file's
    /// name. Everything a path cannot carry is replaced, but a name that is only dots survives that
    /// and means "the directory above" to every file system there is.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    public void AStepNamedOnlyForTheDirectoryAboveIt_IsRefused(string name)
    {
        var factory = new HarnessFactory();
        var parser = new ActionFileParser(factory.FileSystem, factory.Output, factory.Platform);

        var refusal = Assert.Throws<HarnessException>(() => parser.Parse("probe.yml", $"""
            name: probe
            steps:
              - name: "{name}"
                run: |
                  git --version
            """));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("name a directory can carry", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step named after a path still gets a directory of its own rather than one somewhere else:
    /// the separators and the colon are replaced, so nothing escapes the action's build directory.
    /// </summary>
    [Theory]
    [InlineData("C:/Users/someone/.harness-config")]
    [InlineData("/etc/cron.d")]
    [InlineData("../../../etc")]
    public void AStepNamedAfterAPath_StaysInsideItsOwnDirectory(string name)
    {
        var safe = RunSegments.FileNameFor(name);

        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain(':', safe);
        Assert.False(Path.IsPathRooted(safe), $"'{safe}' should not be rooted");

        // And joining it cannot leave the directory it is joined to.
        var joined = Path.GetFullPath(Path.Combine("/tree/build/run/leg", safe));
        Assert.StartsWith(
            Path.GetFullPath("/tree/build/run/leg"),
            joined,
            StringComparison.Ordinal);
    }
}
