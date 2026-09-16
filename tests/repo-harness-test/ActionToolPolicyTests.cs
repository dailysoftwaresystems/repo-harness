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
            () => new ActionToolPolicy(new HostPlatform()).Enforce(action, Config, Root));

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

        new ActionToolPolicy(new HostPlatform()).Enforce(action, Config, Root);

        Assert.Empty(new ActionToolPolicy(new HostPlatform()).Problems(action, Config, Root));
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

        var problem = Assert.Single(new ActionToolPolicy(new HostPlatform()).Problems(action, Config, Root));

        Assert.StartsWith("line 5:", problem, StringComparison.Ordinal);
    }

    private static string Root => Path.Combine(Path.GetTempPath(), "harness-policy-root");

    private static ActionFile Parse(string text)
        => new ActionFileParser(
                new PhysicalFileSystem(FilePermissionsFactory.Create()),
                new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false))
            .Parse("actions/build.yaml", text);
}
