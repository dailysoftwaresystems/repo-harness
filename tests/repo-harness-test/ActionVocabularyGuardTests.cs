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
    /// A setting that owns its own names is filled in by the same grammar as every other: a doubled
    /// brace is a literal one, a shell's own ${...} is left alone, and a name it does not own - a
    /// leg's among them - is refused rather than reaching the program as its own text.
    /// </summary>
    [Fact]
    public void ASettingsOwnNames_AreFilledByTheOneGrammar_AndNothingElseIs()
    {
        var names = new Dictionary<string, string> { ["pid"] = "42" };

        Assert.Equal("-w 42 {pid} ${HOME}", LegPathNames.Fill("-w {pid} {{pid}} ${HOME}", names, "keepAwake"));

        var refusal = Assert.Throws<HarnessException>(() => LegPathNames.Fill("{buildDir}", names, "keepAwake"));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("keepAwake names '{buildDir}', which nothing fills in: it can hold only {pid}", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every action already owns a 'build' and an 'artifacts' directory, at whatever depth it is
    /// grouped. A directory of either name under 'actions' would be one of those and an action at
    /// once, and the two rules written for the names would both then be wrong about it: the walk
    /// that finds what a run kept would skip a real action, and the ignore rules would keep that
    /// action's own file out of git so nothing a reviewer reads could ever be committed.
    /// </summary>
    [Theory]
    [InlineData("build/build.yml")]
    [InlineData("artifacts/artifacts.yml")]
    [InlineData("build/nested/nested.yml")]
    [InlineData("group/artifacts/probe/probe.yml")]
    [InlineData("build.yml")]
    public void AnActionCalledAfterOneOfTheTwoDirectoriesEveryActionOwns_IsRefused(string action)
    {
        var problem = ActionPath.Problem(action);

        Assert.NotNull(problem);
        Assert.Contains("every action already owns", problem, StringComparison.Ordinal);

        // And the remedy it offers is a name, not one of these names. Written as 'build.yml' this
        // would otherwise fall through to the flat-spelling refusal, whose remedy is
        // 'build/build.yml' - a path this same rule then refuses.
        Assert.Contains(ActionPath.Expected("<name>"), problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused without case, because on Windows 'Build' and 'build' are one directory: accepted
    /// there, an action would occupy the working space and neither would be what it looked like.
    /// </summary>
    [Theory]
    [InlineData("Build/Build.yml")]
    [InlineData("ARTIFACTS/ARTIFACTS.yml")]
    public void TheSameNameInAnotherCase_IsRefusedToo(string action)
        => Assert.Contains("every action already owns", ActionPath.Problem(action), StringComparison.Ordinal);

    /// <summary>
    /// Only those two names, exactly. The rule is about a collision, not about the word, so an
    /// action whose name merely starts with one of them is an ordinary action.
    /// </summary>
    [Theory]
    [InlineData("build-all/build-all.yml")]
    [InlineData("rebuild/rebuild.yml")]
    [InlineData("artifacts-check/artifacts-check.yml")]
    [InlineData("group/builds/builds.yml")]
    public void ANameThatMerelyLooksLikeOne_IsAnOrdinaryAction(string action)
        => Assert.Null(ActionPath.Problem(action));

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
