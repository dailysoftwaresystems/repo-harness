using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>Where a leg runs, given what each host was measured to be, and what the check reports.</summary>
public sealed class LegPlacementTests
{
    private static readonly HarnessConfig Config = new()
    {
        Hosts = new HostsConfig
        {
            Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
            Ssh =
            {
                ["mac"] = new SshHostConfig { RepositoryPath = "/Users/dev/repo" },
                ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
            },
        },
    };

    private static readonly HostReport Windows = Measured(HostId.Local, "windows", "x86_64");

    private static readonly HostReport Ubuntu = Measured(HostId.Wsl("Ubuntu"), "linux", "x86_64");

    private static readonly HostReport Mac = Measured(HostId.Ssh("mac"), "macos", "arm64");

    private static readonly HostReport Pi = Measured(HostId.Ssh("pi"), "linux", "arm64");

    [Fact]
    public void Candidates_AreThisMachine_ThenWsl_ThenSsh_EachInDeclaredOrder()
    {
        Assert.Equal(
            ["local", "wsl Ubuntu", "ssh mac", "ssh pi"],
            LegPlacement.Candidates(Config, Leg("linux", "arm64")).Select(host => host.ToString()));
    }

    [Fact]
    public void Candidates_AreOnlyTheHost_ALegNames()
    {
        var leg = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug", Ssh = "mac" };

        Assert.Equal(["ssh mac"], LegPlacement.Candidates(Config, leg).Select(host => host.ToString()));
    }

    [Fact]
    public void Place_ChoosesTheFirstHost_ThatProvidesWhatTheLegNeeds()
    {
        var placement = Place(Leg("linux", "arm64"), Windows, Ubuntu, Mac, Pi);

        Assert.True(placement.Runnable);
        Assert.Equal(HostId.Ssh("pi"), placement.Host?.Host);
    }

    [Fact]
    public void Place_PrefersThisMachine_WhenItCanRunTheLeg()
    {
        Assert.Equal(HostId.Local, Place(Leg("windows", "x86_64"), Windows, Ubuntu).Host?.Host);
    }

    [Fact]
    public void Place_ExplainsEveryCandidate_WhenNoneFits()
    {
        var placement = Place(Leg("macos", "x86_64"), Windows, Ubuntu, Mac, Pi);

        Assert.False(placement.Runnable);
        Assert.Contains("local: it runs windows, and the leg needs macos", placement.Reason, StringComparison.Ordinal);
        Assert.Contains("ssh mac: it is arm64, and the leg runs natively on x86_64", placement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Place_GivesAnUnavailableHost_ItsOwnReason()
    {
        var offline = new HostReport { Host = HostId.Ssh("pi"), Reason = "ssh could not connect: Connection timed out" };

        var placement = Place(Leg("linux", "arm64"), Windows, Ubuntu, Mac, offline);

        Assert.Contains("ssh pi: ssh could not connect: Connection timed out", placement.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Place_RunsAnEmulatedLeg_OnlyWhereItsEmulatorsCheckPassed()
    {
        var leg = new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Config = "debug" };
        var broken = Ubuntu with { Emulators = Checks(EmulatorCheck.Unavailable("qemu-aarch64 is missing")) };
        var working = Ubuntu with { Emulators = Checks(new EmulatorCheck(true, null, "aarch64")) };

        Assert.Contains(
            "wsl Ubuntu: emulator 'qemu-arm64' cannot run there: qemu-aarch64 is missing",
            Place(leg, Windows, broken).Reason,
            StringComparison.Ordinal);

        Assert.Equal(HostId.Wsl("Ubuntu"), Place(leg, Windows, working).Host?.Host);
    }

    [Fact]
    public void Place_PassesOverAHost_ThatWasNotMeasured()
    {
        Assert.Equal(HostId.Ssh("pi"), Place(Leg("linux", "arm64"), Windows, Pi).Host?.Host);
    }

    [Fact]
    public void TheCheck_FailsForANamedLegThatCannotRun_ButNotForADeclaredOne()
    {
        // A switched-off machine is normal; a leg asked for by name that cannot run is not.
        var runnable = new LegPlacement(Selected("a"), Windows, null);
        var stranded = new LegPlacement(Selected("b"), null, "no host");

        Assert.True(new LegsReport([runnable, stranded], [Windows], Named: false).Passed);
        Assert.False(new LegsReport([runnable, stranded], [Windows], Named: true).Passed);
        Assert.False(new LegsReport([stranded], [Windows], Named: false).Passed);
        Assert.False(new LegsReport([], [], Named: false).Passed);
    }

    [Fact]
    public void Render_ExitsWithTheLegsCode_AndWritesJsonThatParses()
    {
        var report = new LegsReport(
            [new LegPlacement(Selected("a"), Windows, null), new LegPlacement(Selected("b"), null, "no host")],
            [Windows],
            Named: true);

        var outcome = LegsReports.Render(report, json: true);

        Assert.Equal(LegsExit.Unavailable, outcome.ExitCode);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var legs = document.RootElement.GetProperty("legs");
        Assert.Equal(2, legs.GetArrayLength());
        Assert.Equal("local", legs[0].GetProperty("host").GetString());
        Assert.Equal("no host", legs[1].GetProperty("reason").GetString());
    }

    [Fact]
    public void Render_NamesTheHostAndTheEmulator_EachRunnableLegUses_AndWhatWasInstalled()
    {
        var emulated = new SelectedLeg("arm", new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Config = "debug" });
        var host = Ubuntu with { Actions = ["installed repo-harness 1.2.0"] };

        var outcome = LegsReports.Render(new LegsReport([new LegPlacement(emulated, host, null)], [host], Named: false), json: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("arm  runs on wsl Ubuntu (linux x86_64) through qemu-arm64", outcome.Details!);
        Assert.Contains("wsl Ubuntu: installed repo-harness 1.2.0", outcome.Details!);
    }

    private static LegPlacement Place(LegConfig leg, params HostReport[] measured)
        => LegPlacement.Place(Config, new SelectedLeg("leg", leg), measured.ToDictionary(report => report.Host));

    private static HostReport Measured(HostId host, string os, string processor) => new() { Host = host, Os = os, Processor = processor };

    private static Dictionary<string, EmulatorCheck> Checks(EmulatorCheck check)
        => new(StringComparer.OrdinalIgnoreCase) { ["qemu-arm64"] = check };

    private static LegConfig Leg(string os, string processor) => new() { Os = os, Processor = processor, Config = "debug" };

    private static SelectedLeg Selected(string name) => new(name, Leg("windows", "x86_64"));
}
