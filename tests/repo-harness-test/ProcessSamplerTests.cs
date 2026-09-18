using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// A lock keeps two harness runs apart; it cannot see a tool somebody started by hand. Measured: a
/// test run started in a shared build directory while a gate ran turned a green suite red, with
/// four test processes live at once. These pin what sampling reports, and what it refuses to report
/// as nothing found.
/// </summary>
public sealed class ProcessSamplerTests
{
    /// <summary>
    /// A rooted path this platform spells its own way. A command line is matched against the build
    /// directory as the host writes it, so a path that only looks absolute on one platform would
    /// make this test measure the test rather than the rule.
    /// </summary>
    private static readonly string BuildDirectory =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo", "build", "x86_64-msvc-release"));

    private static readonly string OtherBuildDirectory =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo", "build", "x86_64-gcc-release"));

    [Fact]
    public async Task ASample_SeesThisMachinesProcesses_IncludingThisOne()
    {
        var factory = new HarnessFactory();
        var sampler = new ProcessSampler(factory.ProcessTable, factory.Platform, factory.Output);

        var sample = await sampler.SampleAsync(0, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Null(sample.Unreadable);
        Assert.NotEmpty(sample.Processes);
        Assert.Contains(sample.Processes, process => process.Id == Environment.ProcessId);
    }

    [Fact]
    public async Task ASession_KeepsEverySample_AndReportsWhatItCannotSee()
    {
        var factory = new HarnessFactory();
        var sampler = new ProcessSampler(factory.ProcessTable, factory.Platform, factory.Output);

        var session = await sampler.StartAsync(
            new ContentionRequest
            {
                Leg = "win-msvc-release",
                BuildDirectory = BuildDirectory,
                BuildTools = ["ninja", "ctest"],
                SampleSeconds = 0,
            },
            TestContext.Current.CancellationToken);

        var report = await session.StopAsync(TestContext.Current.CancellationToken);

        // One as the leg started and one as it ended, at the very least.
        Assert.True(report.Samples.Count >= 2, $"only {report.Samples.Count} sample(s) were kept");
        Assert.False(report.Contended);
        Assert.Null(report.Verdict());

        // Stated even on a clean report: a reader who does not know what was not looked at reads
        // "no contender" as "nothing could have contended".
        Assert.Contains(report.Limits, limit => limit.Contains("relative path", StringComparison.Ordinal));
        Assert.Contains(report.Limits, limit => limit.Contains("command line", StringComparison.Ordinal));
    }

    [Fact]
    public void ABuildToolNamingTheBuildDirectory_MakesTheLegContended()
    {
        var report = Classify(
        [
            Sample(0, Process(4242, "ninja", $"ninja -C {BuildDirectory} all", parent: 9999)),
            Sample(1, Process(4242, "ninja", $"ninja -C {BuildDirectory} all", parent: 9999)),
        ]);

        var contender = Assert.Single(report.Contenders);

        Assert.Equal(4242, contender.Process.Id);
        Assert.Equal("ninja", contender.Tool);
        Assert.Equal(ProcessSeen.Throughout, contender.Seen);
        Assert.Equal(LegVerdict.Contended, report.Verdict()!.Verdict);
        Assert.Equal(LegExit.Contended, Verdicts.ExitCodeFor(report.Verdict()!.Verdict));
    }

    [Fact]
    public void TheHarnessOwnChildren_AreNeverContenders()
    {
        // The harness starts ninja itself; that is the build, not a contender. The harness is in
        // the sample too, because sampling is machine-wide.
        var report = Classify(
        [
            Sample(
                0,
                Process(Environment.ProcessId, "DssHarness", "DssHarness build", parent: 9999),
                Process(4242, "ninja", $"ninja -C {BuildDirectory} all", parent: Environment.ProcessId)),
        ]);

        Assert.Empty(report.Contenders);
        Assert.Null(report.Verdict());
    }

    [Fact]
    public void ABuildToolInAnotherBuildDirectory_IsNotAContender()
    {
        // Two variants build side by side, each in its own directory, and that is the design.
        var report = Classify(
        [
            Sample(0, Process(4242, "ninja", $"ninja -C {OtherBuildDirectory} all", parent: 9999)),
        ]);

        Assert.Empty(report.Contenders);
    }

    [Fact]
    public void ASharedResourceTool_IsAWarningRatherThanAVerdict()
    {
        var report = Classify(
            [Sample(0, Process(77, "ccache", "ccache gcc -c main.c", parent: 9999))],
            sharedResourceTools: ["ccache"]);

        Assert.Empty(report.Contenders);
        Assert.Equal("ccache", Assert.Single(report.SharedResourceUsers).Tool);
        Assert.Null(report.Verdict());
    }

    [Fact]
    public void AContenderThatCameAndWentBetweenTheEnds_IsStillReported()
    {
        // Every sample is kept: with only a first and a last reading this process leaves no trace.
        var report = Classify(
        [
            Sample(0),
            Sample(1, Process(4242, "ctest", $"ctest --test-dir {BuildDirectory}", parent: 9999)),
            Sample(2),
        ]);

        var contender = Assert.Single(report.Contenders);

        Assert.Equal(ProcessSeen.DuringTheRun, contender.Seen);
        Assert.Equal("during the run", ContentionReport.Describe(contender.Seen));
    }

    [Fact]
    public void AProcessTableThatCouldNotBeRead_IsUnknownRatherThanNothingFound()
    {
        var report = Classify(
        [
            Sample(0),
            new ProcessSample(1, TimeSpan.FromSeconds(5), [], "the process table could not be read"),
        ]);

        Assert.Single(report.Unreadable);
        Assert.Contains("could not be read", report.Unreadable[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessWithNoCommandLine_IsCountedButNeverMatched()
    {
        var report = Classify([Sample(0, Process(4242, "ninja", commandLine: null, parent: 9999))]);

        Assert.Empty(report.Contenders);
        Assert.Contains(report.Limits, limit => limit.Contains("no command line could be read", StringComparison.Ordinal));
    }

    [Fact]
    public void AParentThatStartedAfterItsChild_IsNotFollowed()
    {
        // Process ids are recycled: on Windows a freed one was measured coming back after about a
        // hundred allocations. Following that link would invent an ancestry.
        var child = new SampledProcess(4242, ParentId: 100, "ninja", DateTimeOffset.UnixEpoch.AddHours(1), "ninja -C build");
        var recycled = new SampledProcess(100, ParentId: null, "harness", DateTimeOffset.UnixEpoch.AddHours(2), "harness");

        var table = new Dictionary<int, SampledProcess> { [child.Id] = child, [recycled.Id] = recycled };

        Assert.False(ProcessSampler.InHarnessTree(child, table, harnessId: 100));

        var honest = recycled with { StartedUtc = DateTimeOffset.UnixEpoch };
        Assert.True(ProcessSampler.InHarnessTree(child, new Dictionary<int, SampledProcess> { [child.Id] = child, [honest.Id] = honest }, harnessId: 100));
    }

    [Fact]
    public void AProcessIsIdentifiedByItsIdAndItsStartTime()
    {
        // The same id in both samples, with different start times: two processes, not one that ran
        // throughout. Reported as two, each seen at one end.
        var report = Classify(
        [
            Sample(0, Process(4242, "ninja", $"ninja -C {BuildDirectory} all", parent: 9999, started: DateTimeOffset.UnixEpoch)),
            Sample(1, Process(4242, "ninja", $"ninja -C {BuildDirectory} all", parent: 9999, started: DateTimeOffset.UnixEpoch.AddHours(3))),
        ]);

        Assert.Equal(2, report.Contenders.Count);
        Assert.Contains(report.Contenders, found => found.Seen == ProcessSeen.AtTheStart);
        Assert.Contains(report.Contenders, found => found.Seen == ProcessSeen.AtTheEnd);
    }

    /// <summary>
    /// Where no start time can be read the id alone has to serve, and it has to serve across the
    /// whole set. Telling the samples apart instead turns one process into one entry per sample: a
    /// contender that never left is then reported twice, as having come at the start and again at
    /// the end. On a host where nothing publishes a start time that is every process in every
    /// report.
    /// </summary>
    [Fact]
    public void AProcessWithNoStartTimeAtAll_IsStillOneProcessAcrossTheSamples()
    {
        var ninja = new SampledProcess(4242, ParentId: 9999, "ninja", StartedUtc: null, $"ninja -C {BuildDirectory} all");

        var report = Classify([Sample(0, ninja), Sample(1, ninja), Sample(2, ninja)]);

        var contender = Assert.Single(report.Contenders);
        Assert.Equal(ProcessSeen.Throughout, contender.Seen);
    }

    /// <summary>
    /// The platform's own source failing for one reading out of several is ordinary: the table then
    /// falls back to what the runtime alone can see, which publishes no start time. Keyed one way in
    /// the samples that read it and another in the sample that did not, one process splits in two —
    /// reported as having come at the start and again at the end, which is the very answer this is
    /// meant to prevent.
    /// </summary>
    [Fact]
    public void AProcessWhoseStartTimeOnlySomeSamplesCouldRead_IsStillOneProcess()
    {
        var known = Process(4242, "ninja", $"ninja -C {BuildDirectory} all", parent: 9999, started: DateTimeOffset.UnixEpoch);
        var unknown = new SampledProcess(4242, ParentId: 9999, "ninja", StartedUtc: null, $"ninja -C {BuildDirectory} all");

        var report = Classify([Sample(0, known), Sample(1, unknown), Sample(2, known)]);

        var contender = Assert.Single(report.Contenders);

        Assert.Equal(ProcessSeen.Throughout, contender.Seen);
        Assert.Contains(report.Limits, limit => limit.Contains("reused during the leg", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cost of naming a process by its id alone: an id freed and handed to something else inside
    /// one leg reads as one process. Accepted, because the alternative reports every process twice
    /// on any host that publishes no start time — but an id seen outside the harness's tree even
    /// once is still reported, rather than being taken for the harness's own child it was first.
    /// </summary>
    [Fact]
    public void AnIdThatWasTheHarnessesAndThenSomebodyElses_IsStillReported()
    {
        var mine = new SampledProcess(4242, ParentId: Environment.ProcessId, "ninja", StartedUtc: null, "ninja -C mine");
        var theirs = new SampledProcess(4242, ParentId: 9999, "ninja", StartedUtc: null, $"ninja -C {BuildDirectory} all");

        var report = Classify([Sample(0, mine), Sample(1, theirs)]);

        var contender = Assert.Single(report.Contenders);

        Assert.Contains(BuildDirectory, contender.Process.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegNoSampleCouldReadTheTableFor_IsUnmeasured_AndNeverPassed()
    {
        // The one shape that turns the whole subsystem off silently: on a machine where the
        // platform's query is blocked, every process comes back without a command line, nothing can
        // ever match a build directory, and every leg reports no contender for ever.
        var report = Classify(
        [
            new ProcessSample(0, TimeSpan.Zero, [], "the process table could not be read from WMI"),
            new ProcessSample(1, TimeSpan.FromSeconds(5), [], "the process table could not be read from WMI"),
        ]);

        Assert.False(report.Looked);
        Assert.Equal(LegVerdict.Unmeasured, report.Verdict()!.Verdict);
        Assert.Contains("could not be read", report.Verdict()!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OneReadingThatFailedAmongSeveral_IsALimitRatherThanAVerdict()
    {
        // The samples that succeeded did look. Forcing a verdict here would make a transient
        // failure of one query turn an honest green leg red.
        var report = Classify(
        [
            Sample(0),
            new ProcessSample(1, TimeSpan.FromSeconds(5), [], "the process table could not be read"),
            Sample(2),
        ]);

        Assert.True(report.Looked);
        Assert.Null(report.Verdict());
        Assert.Single(report.Unreadable);
    }

    [Fact]
    public void AContenderFound_IsStillReportedAsContended_EvenWhereAReadingFailed()
    {
        // A contender found is a positive fact and names something to do about it, so it outranks
        // the reading that did not happen.
        var report = Classify(
        [
            Sample(0, Process(4242, "ninja", $"ninja -C {BuildDirectory} all", parent: 9999)),
            new ProcessSample(1, TimeSpan.FromSeconds(5), [], "the process table could not be read"),
        ]);

        Assert.Equal(LegVerdict.Contended, report.Verdict()!.Verdict);
    }

    /// <summary>
    /// A directory whose name merely starts with this leg's is another directory. Matched as a
    /// substring, a sanitizer leg's work in build/x86_64-msvc-release-asan named this leg's
    /// build/x86_64-msvc-release, and forced this leg's verdict to contended over work it never shared.
    /// </summary>
    [Fact]
    public void ADirectoryWhoseNameOnlyStartsWithThisLegs_IsNotThisLegsBuildDirectory()
    {
        var sibling = BuildDirectory + "-asan";
        var ninja = Process(4242, "ninja", $"ninja -C \"{sibling}\" all", parent: 9999);

        Assert.Empty(Classify([Sample(0, ninja)]).Contenders);

        // And the directory itself, however it ends, still is.
        var own = Process(4343, "ninja", $"ninja -C \"{BuildDirectory}\" all", parent: 9999);
        Assert.Single(Classify([Sample(0, own)]).Contenders);
    }

    /// <summary>
    /// A shared tool working in another leg's build directory is that leg's work. Each leg a host
    /// runs is run by a harness process of its own, so a sibling's compilers are outside this leg's
    /// process tree; without this they were reported as "outside this run" - 597 times, on one
    /// consumer's host, for one sibling leg of the same invocation.
    /// </summary>
    [Fact]
    public void ASharedToolInASiblingsBuildDirectory_IsThatSiblingsWork_AndOneNamingNoneIsNobodysKnown()
    {
        var sibling = Process(5001, "dsscp", $"dsscp -o \"{Path.Combine(OtherBuildDirectory, "a.o")}\"", parent: 9999);
        var stranger = Process(5002, "dsscp", "dsscp --serve", parent: 9999);

        var report = Classify(
            [Sample(0, sibling, stranger)],
            sharedResourceTools: ["dsscp"],
            otherLegs: new Dictionary<string, string> { ["lin-gcc-release"] = OtherBuildDirectory });

        Assert.Equal("lin-gcc-release", report.SharedResourceUsers.Single(user => user.Process.Id == 5001).Owner);
        Assert.Null(report.SharedResourceUsers.Single(user => user.Process.Id == 5002).Owner);
    }

    /// <summary>
    /// One line per tool and per whose it was, with a count and the range of ids - never one line per
    /// process. And what the tool shares is said when the configuration says it, so a reader can
    /// judge whether it matters.
    /// </summary>
    [Fact]
    public void SharedToolSightings_AreOneLinePerSource_NamingTheSiblingAndWhatIsShared()
    {
        var report = Classify(
            [
                Sample(
                    0,
                    Process(7003, "dsscp", $"dsscp \"{Path.Combine(OtherBuildDirectory, "c.o")}\"", parent: 9999),
                    Process(7001, "dsscp", $"dsscp \"{Path.Combine(OtherBuildDirectory, "a.o")}\"", parent: 9999),
                    Process(7002, "dsscp", $"dsscp \"{Path.Combine(OtherBuildDirectory, "b.o")}\"", parent: 9999),
                    Process(8001, "dsscp", "dsscp --serve", parent: 9999)),
            ],
            sharedResourceTools: ["dsscp"],
            otherLegs: new Dictionary<string, string> { ["lin-gcc-release"] = OtherBuildDirectory });

        var lines = ContentionWarnings.SharedLines(
            "win-msvc-release",
            report,
            new ContentionConfig
            {
                SharedResourceTools = ["dsscp"],
                SharedState = { ["dsscp"] = "the per-user compiler cache" },
            });

        Assert.Equal(2, lines.Count);
        Assert.Contains("3 processes, pids 7001-7003", lines[0], StringComparison.Ordinal);
        Assert.Contains("working in leg 'lin-gcc-release''s build directory", lines[0], StringComparison.Ordinal);
        Assert.Contains("it shares the per-user compiler cache", lines[0], StringComparison.Ordinal);
        Assert.Contains("pid 8001", lines[1], StringComparison.Ordinal);
        Assert.Contains("which no declared leg's build directory accounts for", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("outside this run", string.Join("\n", lines), StringComparison.Ordinal);
    }

    /// <summary>Without a description the line says where one goes, rather than implying there is nothing to say.</summary>
    [Fact]
    public void ASharedToolNobodyDescribed_SaysWhereToDescribeIt()
    {
        var report = Classify(
            [Sample(0, Process(9001, "dsscp", "dsscp --serve", parent: 9999))],
            sharedResourceTools: ["dsscp"]);

        var line = Assert.Single(ContentionWarnings.SharedLines(
            "win-msvc-release",
            report,
            new ContentionConfig { SharedResourceTools = ["dsscp"] }));

        Assert.Contains("contention.sharedState", line, StringComparison.Ordinal);
    }

    /// <summary>A description of a tool nothing watches for reads as protection that does not exist.</summary>
    [Fact]
    public void ASharedStateDescription_OfAToolNothingWatches_OrBlank_IsRefused()
    {
        var problems = HarnessConfigValidator.Validate(new HarnessConfig
        {
            Contention = new ContentionConfig
            {
                SharedResourceTools = ["dsscp"],
                SharedState = { ["dsscpp"] = "a typo's cache", ["dsscp"] = " " },
            },
        });

        Assert.Contains(problems, problem => problem.Contains("describes 'dsscpp'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("contention.sharedState.dsscp is blank", StringComparison.Ordinal));
    }

    private static ContentionReport Classify(
        IReadOnlyList<ProcessSample> samples,
        IReadOnlyList<string>? sharedResourceTools = null,
        IReadOnlyDictionary<string, string>? otherLegs = null)
        => ProcessSamplingSession.Classify(
            samples,
            new ContentionRequest
            {
                Leg = "win-msvc-release",
                BuildDirectory = BuildDirectory,
                BuildTools = ["ninja", "ctest", "cmake"],
                SharedResourceTools = sharedResourceTools ?? [],
                OtherLegs = otherLegs ?? new Dictionary<string, string>(),
            },
            StringComparison.OrdinalIgnoreCase,
            harnessId: Environment.ProcessId);

    private static ProcessSample Sample(int index, params SampledProcess[] processes)
        => new(index, TimeSpan.FromSeconds(index * 5), processes, null);

    private static SampledProcess Process(int id, string name, string? commandLine, int parent, DateTimeOffset? started = null)
        => new(id, parent, name, started ?? DateTimeOffset.UnixEpoch, commandLine);
}
