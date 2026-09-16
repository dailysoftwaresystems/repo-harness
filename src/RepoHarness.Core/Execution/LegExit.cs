namespace RepoHarness.Core.Execution;

/// <summary>
/// The exit codes the commands that run legs — <c>build</c>, <c>test</c> and <c>run</c> — add to the
/// shared ones. Each stands for a verdict whose remedy differs from every other verdict's, which is
/// the whole reason it is not reported as a plain failure.
/// </summary>
/// <remarks>
/// These live in the range 1-9, reserved for a command's own contract, like
/// <see cref="Git.VerifyGitStatus"/> and <see cref="Legs.LegsExit"/>. A shared code landing in that
/// range collides with a command contract without anyone deciding it should, which
/// <c>ExitCodeContractTests</c> exists to catch.
/// </remarks>
public static class LegExit
{
    /// <summary>
    /// Files the tests read changed while they ran, or whether they held still could not be
    /// established. Remedy: let the tree settle, then run again. Reported apart from a failure
    /// because the report describes a tree that never existed, so it says nothing about the code.
    /// </summary>
    public const int InputsMoved = 3;

    /// <summary>
    /// Another process used the leg's build directory while it ran. Remedy: wait for the other run.
    /// A test run started by hand in a shared build directory while a gate ran turned a green suite
    /// red, with four test processes live at once, and no lock can see a tool nobody locked.
    /// </summary>
    public const int Contended = 4;

    /// <summary>
    /// The command exited zero, but the pattern that proves it ran never matched its output.
    /// Remedy: find out what actually ran. A wrapper that reports success without evidence is
    /// indistinguishable from one that never ran.
    /// </summary>
    public const int Unwitnessed = 5;

    /// <summary>
    /// Another live run owns this run's log path, so this run cannot write the evidence for its own
    /// verdict. Remedy: find out which run still owns this leg's logs. Distinct from a
    /// held lock, which stopped the run before it started: here the work could run and its record
    /// could not be kept.
    /// </summary>
    public const int LogHeld = 6;
}
