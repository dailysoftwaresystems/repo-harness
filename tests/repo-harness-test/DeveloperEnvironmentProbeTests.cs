using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// Whether a host can set up a developer environment, as the survey asks it: Visual Studio's own
/// installer names the instance, and a host without one says what was looked for rather than
/// letting a leg fail halfway through for want of <c>cl</c>.
/// </summary>
public sealed class DeveloperEnvironmentProbeTests
{
    private static readonly DeveloperEnvironmentConfig VisualStudio = new() { Kind = DeveloperEnvironmentKinds.VisualStudio };

    /// <summary>
    /// vswhere is asked for the newest instance holding the component, in JSON and UTF-8, and the
    /// instance it names is the one that would be set up, with its version.
    /// </summary>
    [Fact]
    public async Task AnInstanceWithTheComponent_IsTheOneThatWouldBeSetUp()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var environment = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, RequiresComponent = "Microsoft.VisualStudio.Component.VC.Tools.ARM64" };

        var check = await Probe(visualStudio).CheckAsync(environment, TestContext.Current.CancellationToken);

        Assert.True(check.CanSetUp, check.Reason);
        Assert.Equal(visualStudio.InstallationPath, check.InstallationPath);
        Assert.Equal(ScriptedVisualStudio.InstallationVersion, check.InstallationVersion);

        var asked = Assert.Single(visualStudio.Probes);
        Assert.Equal(DeveloperEnvironmentProbe.VsWherePath, asked.FileName);
        Assert.Equal(
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.ARM64", "-format", "json", "-utf8"],
            asked.Arguments);
    }

    /// <summary>The C++ build tools for x86 and x64 are what an instance must have when nothing else is named.</summary>
    [Fact]
    public void TheComponentRequired_IsTheCppBuildTools_WhenNoneIsNamed()
        => Assert.Equal("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", VisualStudio.RequiresComponent);

    /// <summary>A host running another system is never asked: Visual Studio sets up on Windows alone.</summary>
    [Fact]
    public async Task AHostOfAnotherSystem_CannotSetItUp_AndVsWhereIsNeverStarted()
    {
        var processes = Substitute.For<IProcessRunner>();

        var check = await new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Linux), processes)
            .CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("Visual Studio is set up on windows, and this host runs linux", check.Reason);
        await processes.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
    }

    /// <summary>A kind this build does not know is said to be one, never looked for as Visual Studio.</summary>
    [Fact]
    public async Task AKindThisBuildDoesNotKnow_CannotBeSetUp()
    {
        using var visualStudio = new ScriptedVisualStudio();

        var check = await Probe(visualStudio).CheckAsync(new DeveloperEnvironmentConfig { Kind = "xcode" }, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("this build does not know how to set up a 'xcode' environment", check.Reason);
        Assert.Empty(visualStudio.Probes);
    }

    /// <summary>A machine without Visual Studio's installer says where vswhere was looked for.</summary>
    [Fact]
    public async Task NoInstaller_SaysWhereVsWhereWasLookedFor()
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ExecutableNotFoundException(DeveloperEnvironmentProbe.VsWherePath));

        var check = await new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Windows), processes)
            .CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal($"Visual Studio's installer is not there: '{DeveloperEnvironmentProbe.VsWherePath}' was not found", check.Reason);
    }

    /// <summary>A vswhere the system will not start is said to be one, with why.</summary>
    [Fact]
    public async Task AVsWhereThatWillNotStart_SaysWhy()
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProgramStartException(DeveloperEnvironmentProbe.VsWherePath, "Access is denied."));

        var check = await new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Windows), processes)
            .CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Unreadable, check.Found);
        Assert.Equal($"'{DeveloperEnvironmentProbe.VsWherePath}' could not be started: Access is denied.", check.Reason);
    }

    /// <summary>A vswhere that fails says so, with what it said, rather than reading its output as an answer.</summary>
    [Fact]
    public async Task AVsWhereThatFails_IsNotReadAsAnAnswer()
    {
        using var visualStudio = new ScriptedVisualStudio { VsWhereExitCode = 87 };

        var check = await Probe(visualStudio).CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Unreadable, check.Found);
        Assert.Equal($"'{DeveloperEnvironmentProbe.VsWherePath}' could not list the Visual Studio instances (exit 87): vswhere: no instances", check.Reason);
    }

    /// <summary>No instance holding the component names the component, which is what to install.</summary>
    [Fact]
    public async Task NoInstanceWithTheComponent_NamesTheComponent()
    {
        using var visualStudio = new ScriptedVisualStudio { Instances = "[]" };

        var check = await Probe(visualStudio).CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("no Visual Studio instance there has the component 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'", check.Reason);
    }

    /// <summary>
    /// What vswhere prints is read for its first instance's path and version; anything else is no
    /// instance, and what cannot be read at all says so.
    /// </summary>
    [Theory]
    [InlineData("""[{"installationPath":"C:\\VS","installationVersion":"18.0.1"}]""", true, "C:\\VS", "18.0.1")]
    [InlineData("""[{"installationPath":"C:\\VS"}]""", true, "C:\\VS", null)]
    [InlineData("""[{"installationPath":"C:\\VS","installationVersion":18}]""", true, "C:\\VS", null)]
    [InlineData("""[{"installationPath":7}]""", false, null, null)]
    [InlineData("""[{"installationPath":""}]""", false, null, null)]
    [InlineData("""[{"installationVersion":"18.0.1"}]""", false, null, null)]
    [InlineData("""[42]""", false, null, null)]
    [InlineData("""{"installationPath":"C:\\VS"}""", false, null, null)]
    public void WhatVsWherePrints_IsReadForItsFirstInstance(string json, bool available, string? path, string? version)
    {
        var check = DeveloperEnvironmentProbe.Read(json, "C");

        Assert.Equal(available ? DeveloperEnvironmentFound.Installed : DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal(path, check.InstallationPath);
        Assert.Equal(version, check.InstallationVersion);
        Assert.Equal(available ? null : "no Visual Studio instance there has the component 'C'", check.Reason);
    }

    /// <summary>An answer that is not JSON is said to be unreadable, never read as no instance.</summary>
    [Fact]
    public void AnAnswerThatIsNotJson_IsSaidToBeUnreadable()
    {
        var check = DeveloperEnvironmentProbe.Read("Visual Studio Locator version 3.1.7", "C");

        Assert.Equal(DeveloperEnvironmentFound.Unreadable, check.Found);
        Assert.StartsWith("vswhere answered in a form this build cannot read: ", check.Reason, StringComparison.Ordinal);
    }

    private static DeveloperEnvironmentProbe Probe(ScriptedVisualStudio visualStudio)
        => new(HostDoubles.Platform(PlatformId.Windows), visualStudio);
}
