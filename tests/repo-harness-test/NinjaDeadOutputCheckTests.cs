using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// ninja is asked which outputs of earlier builds no target of the current build produces any more, as a dry run
/// that removes nothing; and whatever keeps it from answering leaves the build measured as it always was, never a
/// failure of it.
/// </summary>
public sealed class NinjaDeadOutputCheckTests
{
    /// <summary>Each output the dry run would remove is named, as ninja spells it, a path holding a space among them.</summary>
    [Fact]
    public async Task EachOutputTheDryRunWouldRemove_IsNamed()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("build.ninja", string.Empty);
        var ninja = new AnsweringRunner(0, "Cleaning...\nRemove CMakeFiles/gone.dir/old name.c.o\r\nRemove gone\n2 files.\n");

        var dead = await Check(ninja).CheckAsync(temp.Path, ["/opt/ninja"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(dead.Answered);
        Assert.Equal(["CMakeFiles/gone.dir/old name.c.o", "gone"], dead.Paths);

        var asked = Assert.Single(ninja.Started);
        Assert.Equal(["-C", temp.Path, "-n", "-t", "cleandead"], asked.Arguments);
        Assert.Equal(temp.Path, asked.WorkingDirectory);
        Assert.Equal(["/opt/ninja"], asked.AppendToPath);
    }

    /// <summary>An answer naming nothing is an answer: nothing is dead.</summary>
    [Fact]
    public async Task AnAnswerNamingNothing_IsNothingDead()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("build.ninja", string.Empty);

        var dead = await Check(new AnsweringRunner(0, "Cleaning...\n0 files.\n")).CheckAsync(temp.Path, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(dead.Answered);
        Assert.Empty(dead.Paths);
    }

    /// <summary>
    /// A directory that is not ninja's asks nothing; a ninja too old to know the tool, one that fails, and one that
    /// will not start say nothing - and none of them is a failure.
    /// </summary>
    [Fact]
    public async Task WhatKeepsNinjaFromAnswering_SaysNothing_AndFailsNothing()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var unasked = new AnsweringRunner(0, "Remove x\n");

        Assert.Same(NinjaDeadOutputs.Unknown, await Check(unasked).CheckAsync(temp.Path, [], cancellationToken: token));
        Assert.Empty(unasked.Started);

        temp.WriteFile("build.ninja", string.Empty);

        Assert.Same(NinjaDeadOutputs.Unknown, await Check(new AnsweringRunner(1, "ninja: error: unknown tool 'cleandead'\n")).CheckAsync(temp.Path, [], cancellationToken: token));
        Assert.Same(NinjaDeadOutputs.Unknown, await Check(new AnsweringRunner(0, string.Empty, raises: true)).CheckAsync(temp.Path, [], cancellationToken: token));
    }

    /// <summary>A relative ninja the build's configuration recorded is read from the build directory, as the build read it.</summary>
    [Fact]
    public async Task ARelativeRecordedNinja_IsReadFromTheBuildDirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("build.ninja", string.Empty);
        var ninja = new AnsweringRunner(0, "0 files.\n");

        await Check(ninja).CheckAsync(temp.Path, [], Path.Combine("tools", "ninja"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(temp.Path, "tools", "ninja"), Assert.Single(ninja.Started).FileName);
    }

    private static NinjaDeadOutputCheck Check(IProcessRunner ninja) => new(ninja, new PhysicalFileSystem(FilePermissionsFactory.Create()));

    /// <summary>Answers every program with the one answer, and records what was started.</summary>
    private sealed class AnsweringRunner(int exitCode, string output, bool raises = false) : IProcessRunner
    {
        public List<ProcessRequest> Started { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Started.Add(request);

            return raises
                ? throw new ProgramStartException(request.FileName, $"'{request.FileName}' could not be started: not there")
                : Task.FromResult(new ProcessResult(exitCode, output, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }
}
