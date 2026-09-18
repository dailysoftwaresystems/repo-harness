using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>Which programs an action file is allowed to start, decided before anything runs.</summary>
public sealed class ActionToolPolicyTests
{
    private static readonly HarnessConfig Config = new()
    {
        Tools =
        [
            new ToolConfig { Name = "cmake" },
            new ToolConfig { Name = "dotnet" },
        ],
    };

    [Fact]
    public void Classify_AllowsADeclaredTool_WhateverItsCaseOrExtension()
    {
        var policy = new ActionToolPolicy(new HostPlatform());

        Assert.Equal(ProgramAllowance.DeclaredTool, policy.Classify("cmake", Config.Tools, Root));
        Assert.Equal(ProgramAllowance.DeclaredTool, policy.Classify("CMake", Config.Tools, Root));
        Assert.Equal(ProgramAllowance.DeclaredTool, policy.Classify("cmake.exe", Config.Tools, Root));
    }

    [Fact]
    public void Classify_AllowsAProgramTheRepositoryShips()
    {
        var policy = new ActionToolPolicy(new HostPlatform());

        // Not required to exist: the program a later step runs is usually one an earlier step built.
        Assert.Equal(ProgramAllowance.RepositoryPath, policy.Classify("build/bin/corpus", Config.Tools, Root));
        Assert.Equal(ProgramAllowance.RepositoryPath, policy.Classify("./tools/generate", Config.Tools, Root));
        Assert.Equal(
            ProgramAllowance.RepositoryPath,
            policy.Classify(Path.Combine(Root, "tools", "generate"), Config.Tools, Root));
    }

    [Fact]
    public void Classify_RefusesAPathThatLeavesTheRepository()
    {
        var policy = new ActionToolPolicy(new HostPlatform());

        Assert.Equal(ProgramAllowance.Undeclared, policy.Classify("../elsewhere/tool", Config.Tools, Root));
        Assert.Equal(ProgramAllowance.Undeclared, policy.Classify("build/../../tool", Config.Tools, Root));
    }

    [Fact]
    public void Classify_RefusesAnUndeclaredBareProgram()
    {
        var policy = new ActionToolPolicy(new HostPlatform());

        Assert.Equal(ProgramAllowance.Undeclared, policy.Classify("ninja", Config.Tools, Root));
    }

    [Fact]
    public void Enforce_RefusesBeforeAnythingRuns_NamingEveryUndeclaredProgram()
    {
        var action = Parse("""
            steps:
              - name: build
                run: |
                  cmake --build build
                  ninja -C build
              - name: publish
                run: curl https://example.invalid
            """);

        var exception = Assert.Throws<HarnessException>(
            () => new ActionToolPolicy(new HostPlatform()).Enforce(action, Config, Root, AsWritten(action)));

        // A policy refusal, not a malformed file: the file is well formed and says something it
        // may not do.
        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("names 2 program(s) that may not run", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'ninja' is not declared under 'tools'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'curl' is not declared under 'tools'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("declared: 'cmake', 'dotnet'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enforce_AcceptsAFileThatOnlyStartsDeclaredToolsAndRepositoryPrograms()
    {
        var action = Parse("""
            steps:
              - name: build
                run: |
                  cmake --build build
                  build/bin/corpus --self-check
            """);

        new ActionToolPolicy(new HostPlatform()).Enforce(action, Config, Root, AsWritten(action));

        Assert.Empty(new ActionToolPolicy(new HostPlatform()).Problems(action, Config, Root, AsWritten(action)));
    }

    [Fact]
    public void Problems_PointAtTheLineThatNamedTheProgram()
    {
        var action = Parse("""
            steps:
              - name: build
                run: |
                  cmake --build build
                  ninja -C build
            """);

        var problem = Assert.Single(new ActionToolPolicy(new HostPlatform()).Problems(action, Config, Root, AsWritten(action)));

        Assert.StartsWith("line 5:", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relative program is read from the directory its step runs in, as the run reads it: from
    /// 'engine', '../bin/tool' is the repository's own, and from the tree root it leaves the repository.
    /// </summary>
    [Fact]
    public void Problems_ReadARelativeProgram_FromTheDirectoryItsStepRunsIn()
    {
        var policy = new ActionToolPolicy(new HostPlatform());

        var fromEngine = Parse("""
            steps:
              - name: probe
                workingDirectory: engine
                run: |
                  ../bin/tool
            """);

        var fromRoot = Parse("""
            steps:
              - name: probe
                run: |
                  ../bin/tool
            """);

        Assert.Empty(policy.Problems(fromEngine, Config, Root, AsWritten(fromEngine)));
        Assert.Single(policy.Problems(fromRoot, Config, Root, AsWritten(fromRoot)));
        Assert.Equal(ProgramAllowance.Undeclared, policy.Classify("../../tool", Config.Tools, Root, "engine"));
    }

    /// <summary>
    /// A program relative to a drive - 'C:tool' on Windows - is read as the run starts it: from the
    /// directory its step runs in, on that drive, never from wherever this process happens to be on it.
    /// </summary>
    [Fact]
    public void Classify_ReadsADriveRelativeProgram_AsTheRunStartsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows reads a path relative to a drive.");

        var policy = new ActionToolPolicy(new HostPlatform());

        Assert.Equal(ProgramAllowance.RepositoryPath, policy.Classify($"{Root[..2]}tool", Config.Tools, Root, "engine"));
    }

    private static string Root => Path.Combine(Path.GetTempPath(), "harness-policy-root");

    /// <summary>
    /// Each line as written, from its step's directory under <see cref="Root"/>: how the run reads a
    /// line that names no placeholder.
    /// </summary>
    private static Func<ActionStep, ActionCommand, (string Program, string WorkingDirectory)> AsWritten(ActionFile action)
        => (step, command) => (command.Program, Path.Combine(Root, step.WorkingPath(action.DirectoryName) ?? string.Empty));

    private static ActionFile Parse(string text)
        => new ActionFileParser(
                new PhysicalFileSystem(FilePermissionsFactory.Create()),
                new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false),
                new HostPlatform())
            .Parse("actions/build.yaml", text);
}
