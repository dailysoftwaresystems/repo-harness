using System.Diagnostics;
using NSubstitute;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// Edit a file mid-run and some tests see the old one and some the new, and the report describes a
/// tree that never existed. This was measured: eight failures, all passing seconds later on the
/// unchanged tree. These pin what the fingerprints and the watch notice, and what they refuse to
/// call clean.
/// </summary>
public sealed class InputFingerprintTests
{
    private static readonly string[] Inputs = ["config/c.lang.json", "corpus/sample.txt"];

    [Fact]
    public async Task InputsThatHeldStill_CompareUnchanged()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        var comparison = InputFingerprint.Compare(before, after);

        Assert.Equal(InputChange.Unchanged, comparison.Change);
        Assert.Null(comparison.Verdict());
    }

    [Fact]
    public async Task AnInputThatChanged_IsNamed()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        temp.WriteFile("config/c.lang.json", "{\"changed\": true}");
        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        var comparison = InputFingerprint.Compare(before, after);

        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Equal(["config/c.lang.json"], comparison.Changed);
        Assert.Equal(LegVerdict.InputsMoved, comparison.Verdict()!.Verdict);
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(comparison.Verdict()!.Verdict));
    }

    [Fact]
    public async Task AFileOfTheSameSizeWithOtherContent_IsStillAChange()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        temp.WriteFile("corpus/sample.txt", "aaaa");

        var before = await fingerprint.TakeAsync(temp.Path, ["corpus/sample.txt"], TestContext.Current.CancellationToken);
        temp.WriteFile("corpus/sample.txt", "bbbb");
        var after = await fingerprint.TakeAsync(temp.Path, ["corpus/sample.txt"], TestContext.Current.CancellationToken);

        // Content as well as size: a corpus fixture rewritten in place keeps its length.
        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after).Change);
    }

    [Fact]
    public async Task AnInputThatWasNeverThere_IsNotAChange()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();

        var before = await fingerprint.TakeAsync(temp.Path, ["missing.json"], TestContext.Current.CancellationToken);
        var after = await fingerprint.TakeAsync(temp.Path, ["missing.json"], TestContext.Current.CancellationToken);

        // Absence is a measurement, and it compares equal to absence. Unreadability is what leaves
        // the question open.
        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);
        Assert.Equal(InputFingerprint.AbsentContent, before.Files.Single().Content);
    }

    [Fact]
    public async Task AnInputThatAppearedWhileTheTestsRan_IsAChange()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();

        var before = await fingerprint.TakeAsync(temp.Path, ["generated.json"], TestContext.Current.CancellationToken);
        temp.WriteFile("generated.json", "{}");
        var after = await fingerprint.TakeAsync(temp.Path, ["generated.json"], TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after).Change);
    }

    [Fact]
    public void ASnapshotThatCouldNotBeTaken_IsNeverReportedAsClean()
    {
        var files = new[] { new FileFingerprint("config/c.lang.json", 4, "abcd") };
        var complete = new InputSnapshot(files, []);
        var incomplete = new InputSnapshot(files, [new UnreadableInput("corpus/sample.txt", "the file is held by another process")]);

        var comparison = InputFingerprint.Compare(complete, incomplete);

        Assert.Equal(InputChange.Unmeasured, comparison.Change);
        Assert.Equal(LegVerdict.Unmeasured, comparison.Verdict()!.Verdict);
        Assert.Contains("corpus/sample.txt", comparison.Detail, StringComparison.Ordinal);
        Assert.False(incomplete.Complete);
    }

    [Fact]
    public async Task AnInputThatCannotBeRead_MakesTheSnapshotIncomplete()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a read while another program holds the file open.");

        using var temp = new TempDirectory();
        var fingerprint = Create();
        var path = temp.WriteFile("corpus/sample.txt", "held");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var snapshot = await fingerprint.TakeAsync(temp.Path, ["corpus/sample.txt"], TestContext.Current.CancellationToken);

            Assert.False(snapshot.Complete);
            Assert.Equal("corpus/sample.txt", snapshot.Unreadable.Single().Path);
            Assert.Empty(snapshot.Files);
        }
    }

    [Fact]
    public async Task AnEditMadeAndUndoneWhileTheTestsRan_IsStillCaught()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        var original = await File.ReadAllTextAsync(temp.Combine("config/c.lang.json"), TestContext.Current.CancellationToken);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        using var watch = await fingerprint.WatchAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        if (watch.Failure is { } failure)
        {
            Assert.Skip($"This machine cannot watch the tree: {failure}");
        }

        temp.WriteFile("config/c.lang.json", "{\"rewritten\": true}");
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);

        // Put back exactly as it was, which is what makes the two snapshots agree and the whole
        // rewrite invisible without the watch.
        await File.WriteAllTextAsync(temp.Combine("config/c.lang.json"), original, TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);

        var withWatch = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, withWatch.Change);
        Assert.Contains("config/c.lang.json", withWatch.Changed);
    }

    [Fact]
    public async Task AWatchThatLostItsEvents_IsUnmeasured()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        // A directory that does not exist cannot be watched, which stands in here for the machine
        // that runs out of watches mid-run: either way the question was never answered.
        using var broken = await fingerprint.WatchAsync(temp.Combine("gone"), Inputs, TestContext.Current.CancellationToken);

        Assert.NotNull(broken.Failure);
        Assert.Equal(InputChange.Unmeasured, InputFingerprint.Compare(before, after, broken).Change);
    }

    /// <summary>
    /// What a watch saw change before it settled is set aside, and what it sees afterwards is not. The
    /// settling is what keeps a change macOS delivers late - made before the work began - from reading
    /// as an input that moved under the work.
    /// </summary>
    [Fact]
    public async Task WhatAWatchSawBeforeItSettled_IsSetAside_AndWhatItSeesAfterIsNot()
    {
        using var temp = new TempDirectory();
        Seed(temp);

        // Made directly, so it has not settled on any platform: the change below stands in for one
        // macOS delivers to a watch that began after it was made.
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal);

        if (watch.Failure is { } failure)
        {
            Assert.Skip($"This machine cannot watch the tree: {failure}");
        }

        temp.WriteFile("config/c.lang.json", "{\"rewritten\": true}");
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);

        // Long enough for anything else the one write raises to arrive before it is set aside.
        await watch.SettleAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        Assert.Empty(watch.Changed);

        temp.WriteFile("corpus/sample.txt", "moved");
        await WaitForAsync(watch, "corpus/sample.txt", TestContext.Current.CancellationToken);

        Assert.Contains("corpus/sample.txt", watch.Changed);
    }

    /// <summary>
    /// Only macOS delivers changes made before a watch began, so only a watch there waits for them
    /// before it is handed back.
    /// </summary>
    [Theory]
    [InlineData(PlatformId.MacOs, true)]
    [InlineData(PlatformId.Linux, false)]
    [InlineData(PlatformId.Windows, false)]
    public async Task OnlyAWatchOnMacOs_WaitsForWhatCameBeforeIt(PlatformId platform, bool waits)
    {
        using var temp = new TempDirectory();
        Seed(temp);

        var host = Substitute.For<IHostPlatform>();
        host.Current.Returns(platform);
        host.PathComparison.Returns(new HostPlatform().PathComparison);

        var fingerprint = new InputFingerprint(new HarnessFactory().FileSystem, host);
        var elapsed = Stopwatch.StartNew();

        using var watch = await fingerprint.WatchAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(waits, fingerprint.LateDelivery > TimeSpan.Zero);

        if (waits)
        {
            // Less a coarse timer's tick, which can end a delay a little before a stopwatch would.
            Assert.True(
                elapsed.Elapsed >= fingerprint.LateDelivery - TimeSpan.FromMilliseconds(50),
                $"The watch was handed back after {elapsed.Elapsed.TotalMilliseconds:F0} ms, before macOS "
                + $"had {fingerprint.LateDelivery.TotalMilliseconds:F0} ms to deliver what came before it.");
        }
    }

    private static InputFingerprint Create()
    {
        var factory = new HarnessFactory();
        return new InputFingerprint(factory.FileSystem, factory.Platform);
    }

    private static void Seed(TempDirectory temp)
    {
        temp.WriteFile("config/c.lang.json", "{\"original\": true}");
        temp.WriteFile("corpus/sample.txt", "sample");
    }

    /// <summary>
    /// Waits for the watch to notice <paramref name="path"/>. File system notifications are
    /// delivered by the operating system when it gets to them, so the wait is generous and its
    /// failure is a real one: an edit that was never noticed is the failure this exists to catch.
    /// </summary>
    private static async Task WaitForAsync(InputWatch watch, string path, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (watch.Changed.Contains(path))
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"The watch never noticed '{path}' changing.");
    }
}
