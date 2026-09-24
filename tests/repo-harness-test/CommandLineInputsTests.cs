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

    /// <summary>
    /// A step's own inputs are read by that step alone, so a run that does not run it gives them nowhere
    /// to go: named on a plain run, a manual step's input is refused, and said as that step's. The
    /// action's own inputs, which every step reads, and the inputs of a step the run does run, are taken.
    /// </summary>
    [Fact]
    public void AnInputOfAStepTheRunLeavesOut_IsRefused_NamingTheStep()
    {
        var file = new ActionFile(
            "actions/corpus/corpus.yml",
            "corpus",
            null,
            [new ActionInput("root", "corpus", Required: false, null)],
            [
                new ActionStep { Name = "build", Commands = [new ActionCommand("dotnet --version", 4, ["dotnet", "--version"])] },
                new ActionStep
                {
                    Name = "bench",
                    Manual = true,
                    SuccessPattern = "^done",
                    Inputs = [new ActionInput("size", "25", Required: false, null)],
                    Commands = [new ActionCommand("dotnet --info", 8, ["dotnet", "--info"])],
                },
            ]);

        var plain = StepSelection.Default.Apply("corpus", file);
        var benchmarking = new StepSelection { ManualSteps = ["bench"] }.Apply("corpus", file);

        CommandLineInputs.RequireRead("corpus", plain, new Dictionary<string, string> { ["root"] = "x" });
        CommandLineInputs.RequireRead("corpus", benchmarking, new Dictionary<string, string> { ["root"] = "x", ["size"] = "5" });

        var refusal = Assert.Throws<HarnessException>(() => CommandLineInputs.RequireRead(
            "corpus",
            plain,
            new Dictionary<string, string> { ["size"] = "5", ["depth"] = "3" }));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Equal(
            "--input names 'depth', which 'actions/corpus/corpus.yml' does not declare under inputs; "
            + "--input names 'size', which only step(s) 'bench' reads, and this run of runner 'corpus' does not run it; "
            + "the steps this run runs read root.",
            refusal.Message);
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

    /// <summary>
    /// A host running one of the run's legs is handed the manual steps too, one option each - which it
    /// reads back to the same steps. A leg there running the runner's own steps where the command line
    /// named others would report on work nobody asked it to do.
    /// </summary>
    [Fact]
    public void AHostIsHandedTheManualStepsNamed()
    {
        var named = StepSelection.Names(["bench,profile"]);

        var forwarded = RunnerRunService.RemoteArguments("probe", time: false, CommandLineInputs.Parse(["size=5"]), named);

        Assert.Equal(["probe", "--input", "size=5", "--manual-step", "bench", "--manual-step", "profile"], forwarded);

        var steps = forwarded
            .Select((argument, index) => (argument, index))
            .Where(item => item.index > 0 && forwarded[item.index - 1] == StepSelection.Option)
            .Select(item => item.argument)
            .ToList();

        Assert.Equal(named, StepSelection.Names(steps));
    }
}
