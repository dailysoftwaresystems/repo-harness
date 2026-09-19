using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// What <c>run --input name=value</c> accepts, what it refuses before anything is read, and what a
/// host running one of the run's legs is handed so it runs with the same values.
/// </summary>
public sealed class CommandLineInputsTests
{
    /// <summary>
    /// Each pair is split at its first '=', so a value may hold one, and spaces are the value's own.
    /// </summary>
    [Fact]
    public void Parse_ReadsEachPair_SplittingAtTheFirstEquals()
    {
        var given = CommandLineInputs.Parse(["root=corpus", "filter=a=b c", "flag=--version"]);

        Assert.Equal(["root", "filter", "flag"], given.Keys);
        Assert.Equal(["corpus", "a=b c", "--version"], given.Values);
    }

    /// <summary>
    /// A pair that names nothing, gives nothing, or gives a name a second value is refused with every
    /// problem named at once: an empty value is what an unset variable produces, and read as a value
    /// it would override the runner's .env with nothing.
    /// </summary>
    [Theory]
    [InlineData(new[] { "root" }, "--input 'root' is not name=value.")]
    [InlineData(new[] { "=corpus" }, "--input '=corpus' names no input.")]
    [InlineData(new[] { "root=" }, "--input 'root=' gives 'root' no value; leave it out to use the runner's .env value or the input's default.")]
    [InlineData(new[] { "root=a", "root=b" }, "--input gives 'root' a value more than once.")]
    [InlineData(new[] { "root=a", "root=b", "root=c", "x" }, "--input gives 'root' a value more than once; --input 'x' is not name=value.")]
    public void Parse_Refuses_APairThatGivesNoValueOrOneTwice(string[] pairs, string expected)
    {
        var refusal = Assert.Throws<HarnessException>(() => CommandLineInputs.Parse(pairs));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Equal(expected, refusal.Message);
    }

    /// <summary>
    /// A value goes to an input the action declares, matched exactly as a run line names it: one
    /// for any other name would change nothing while the command line said it had.
    /// </summary>
    [Fact]
    public void RequireDeclared_Refuses_AnInputTheActionDoesNotDeclare_NamingWhatItDoes()
    {
        var file = new ActionFile(
            "actions/corpus/corpus.yml",
            "corpus",
            null,
            [new ActionInput("root", "corpus", Required: false, null), new ActionInput("filter", null, Required: false, null)],
            [new ActionStep { Name = "measure", Commands = [new ActionCommand("dotnet --version", 4, ["dotnet", "--version"])] }]);

        CommandLineInputs.RequireDeclared("corpus", file, new Dictionary<string, string> { ["root"] = "x", ["filter"] = "y" });
        CommandLineInputs.RequireDeclared("corpus", null, new Dictionary<string, string>());

        var unknown = Assert.Throws<HarnessException>(() => CommandLineInputs.RequireDeclared(
            "corpus",
            file,
            new Dictionary<string, string> { ["Root"] = "x", ["root"] = "y", ["depth"] = "3" }));

        Assert.Equal(HarnessExit.UsageError, unknown.ExitCode);
        Assert.Equal(
            "--input names 'Root', 'depth', which 'actions/corpus/corpus.yml' does not declare under inputs; it declares root, filter.",
            unknown.Message);

        var none = Assert.Throws<HarnessException>(() => CommandLineInputs.RequireDeclared(
            "corpus",
            file with { Inputs = [] },
            new Dictionary<string, string> { ["root"] = "x" }));

        Assert.EndsWith("it declares none.", none.Message, StringComparison.Ordinal);
    }

    /// <summary>A runner of phases has no file, so no inputs: a value given for one is refused, not dropped.</summary>
    [Fact]
    public void RequireDeclared_Refuses_AnyInput_ForARunnerOfPhases()
    {
        var refusal = Assert.Throws<HarnessException>(() => CommandLineInputs.RequireDeclared(
            "bench",
            null,
            new Dictionary<string, string> { ["root"] = "x" }));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("Runner 'bench' runs phases of its own", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host running one of the run's legs is handed the runner, --time where it was asked for, and
    /// every input as it was given, one pair per option - which it reads back to the same values. A
    /// leg on another machine running the default where the command line gave a value would be a
    /// verdict about a run nobody asked for.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHostIsHandedTheSameRun_InputsAndAll(bool time)
    {
        var given = CommandLineInputs.Parse(["root=corpus", "filter=a=b c"]);

        var forwarded = RunnerRunService.RemoteArguments("probe", time, given);

        Assert.Equal(
            ["probe", .. time ? new[] { "--time" } : [], "--input", "root=corpus", "--input", "filter=a=b c"],
            forwarded);

        var pairs = forwarded
            .Select((argument, index) => (argument, index))
            .Where(item => item.index > 0 && forwarded[item.index - 1] == CommandLineInputs.Option)
            .Select(item => item.argument)
            .ToList();

        Assert.Equal(given, CommandLineInputs.Parse(pairs));
    }
}
