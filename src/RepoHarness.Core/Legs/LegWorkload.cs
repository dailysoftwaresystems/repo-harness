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
/// What else the command starts on every leg, such as the steps of the runner <c>run</c> was given.
/// </param>
/// <remarks>
/// Said by each command rather than assumed for all of them. Asked what a leg needs in general, a
/// survey turns a build away for want of the test runner it never starts, and a copy for want of a
/// compiler, although a copy starts no program at all.
/// </remarks>
public sealed record LegWorkload(bool Build, bool Test, IReadOnlyList<string> Programs)
{
    /// <summary>Building and testing: what a leg is for, and what <c>legs</c> answers for.</summary>
    public static LegWorkload BuildAndTest { get; } = new(Build: true, Test: true, []);

    /// <summary>Building alone.</summary>
    public static LegWorkload BuildOnly { get; } = new(Build: true, Test: false, []);

    /// <summary>A copy of the tree, which starts no program on the host.</summary>
    public static LegWorkload Copy { get; } = new(Build: false, Test: false, []);
}
