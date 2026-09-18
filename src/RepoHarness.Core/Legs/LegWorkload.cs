using RepoHarness.Core.Configuration;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Runners;

namespace RepoHarness.Core.Legs;

/// <summary>
/// What a command will have each leg do, which decides the programs a host must have before a leg is
/// placed on it.
/// </summary>
/// <param name="Build">
/// Whether the leg is built: its project's build program, ninja where its toolchain asks for the
/// Ninja generator, and the compilers its variant names.
/// </param>
/// <param name="Test">Whether the leg's tests are run: its test runner.</param>
/// <param name="Programs">
/// What else the command starts on every leg that its host must have, such as the steps of the
/// runner <c>run</c> was given; one started under an environment that sets PATH goes to
/// <see cref="UnderOwnPath"/> instead.
/// </param>
/// <remarks>
/// Said by each command rather than assumed for all of them. Asked what a leg needs in general, a
/// survey turns a build away for want of the test runner it never starts, and a copy for want of a
/// compiler, although a copy starts no program at all.
/// </remarks>
public sealed record LegWorkload(bool Build, bool Test, IReadOnlyList<string> Programs)
{
    /// <summary>
    /// What else the command starts on every leg under an environment that sets PATH: asked about,
    /// so the directory each is found in reaches that PATH like any other, and never required of a
    /// host - that PATH is where it is looked for when it starts, and no survey can see it.
    /// </summary>
    public IReadOnlyList<string> UnderOwnPath { get; init; } = [];

    /// <summary>Building and testing: what a leg is for, and what <c>legs</c> answers for.</summary>
    public static LegWorkload BuildAndTest { get; } = new(Build: true, Test: true, []);

    /// <summary>Building alone.</summary>
    public static LegWorkload BuildOnly { get; } = new(Build: true, Test: false, []);

    /// <summary>A copy of the tree, which starts no program on the host.</summary>
    public static LegWorkload Copy { get; } = new(Build: false, Test: false, []);

    /// <summary>
    /// What running <paramref name="runner"/> has a leg do: its steps, and a build first where it
    /// requires one.
    /// </summary>
    /// <param name="runner">The runner.</param>
    /// <param name="action">Its action file, when it runs one rather than phases of its own.</param>
    /// <remarks>
    /// A step whose environment - its own or the runner's - sets PATH finds its program on that PATH,
    /// which no survey can see, so its program is the run's to find rather than a demand on the host,
    /// and is only asked about: see <see cref="UnderOwnPath"/>.
    /// </remarks>
    public static LegWorkload ForRunner(RunnerConfig runner, ActionFile? action)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var runnerPath = ProcessRunner.SetsPath(runner.Env.Keys);

        List<(string Program, bool OwnPath)> starts = action is null
            ? [.. runner.Phases
                .Where(phase => phase.Command.Count > 0)
                .Select(phase => (phase.Command[0], runnerPath || ProcessRunner.SetsPath(phase.Env.Keys)))]
            : [.. action.Steps
                .SelectMany(step => step.Commands.Select(command => (command.Program, runnerPath || ProcessRunner.SetsPath(step.Env.Keys))))];

        return new LegWorkload(Build: runner.RequireBuild, Test: false, [.. starts.Where(start => !start.OwnPath).Select(start => start.Program)])
        {
            UnderOwnPath = [.. starts.Where(start => start.OwnPath).Select(start => start.Program)],
        };
    }
}
