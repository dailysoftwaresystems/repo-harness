using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>How the environment a process starts with is built from the layers that declare it.</summary>
public sealed class PhaseEnvironmentTests
{
    /// <summary>
    /// Each layer is over the ones before it, and a name is one name whatever its case: a layer that
    /// sets PATH replaces a lower layer's Path rather than leaving the process two of them.
    /// </summary>
    [Fact]
    public void ALaterLayer_WinsAName_WhateverItsCase()
    {
        var environment = PhaseEnvironment.Layered(
            new Dictionary<string, string> { ["Path"] = "/host/bin", ["RH_HOST"] = "host" },
            new Dictionary<string, string> { ["PATH"] = "/variant/bin" });

        Assert.Equal(2, environment.Count);
        Assert.Equal("/variant/bin", environment["PATH"]);
        Assert.Equal("host", environment["RH_HOST"]);
    }
}
