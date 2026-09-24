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

        using var watch = fingerprint.Watch(temp.Path, Inputs);

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
        using var broken = fingerprint.Watch(temp.Combine("gone"), Inputs);

        Assert.NotNull(broken.Failure);
        Assert.Equal(InputChange.Unmeasured, InputFingerprint.Compare(before, after, broken).Change);
    }

    /// <summary>
    /// A watch that can report a write made before it began - as one on macOS can - has what it
    /// reports confirmed by the file's own readings: a write the first reading already holds came from
    /// before, and moved nothing. A watch that cannot report one counts every report, as it always did.
    /// </summary>
    [Theory]
    [InlineData(true, InputChange.Unchanged)]
    [InlineData(false, InputChange.Moved)]
    public async Task AReportOfAWriteTheFirstReadingHolds_CountsOnlyWhereTheWatchCannotReportEarlierWrites(
        bool reportsEarlierWrites,
        InputChange expected)
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);

        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, reportsEarlierWrites);

        if (watch.Failure is { } failure)
        {
            Assert.Skip($"This machine cannot watch the tree: {failure}");
        }

        // Written, and seen, before the first reading: what macOS delivers late to a watch begun after
        // the write, in an order every platform can be made to show.
        temp.WriteFile("config/c.lang.json", "{\"rewritten\": true}");
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(expected, InputFingerprint.Compare(before, after, watch).Change);
    }

    /// <summary>
    /// Where what the watch reports is confirmed by readings, an edit made and undone while the work
    /// ran still counts: its content reads the same afterwards, and its time of writing does not.
    /// </summary>
    [Fact]
    public async Task AnEditUndoneWhileTheWorkRan_StillCounts_WhereReportsAreConfirmedByReadings()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        var path = temp.Combine("config/c.lang.json");
        var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        // Dated well back, so the edit's own time differs from it however coarse the clock that
        // stamps files is.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, reportsEarlierWrites: true);

        if (watch.Failure is { } failure)
        {
            Assert.Skip($"This machine cannot watch the tree: {failure}");
        }

        temp.WriteFile("config/c.lang.json", "{\"rewritten\": true}");
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);

        var comparison = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Contains("config/c.lang.json", comparison.Changed);
    }

    /// <summary>
    /// A file absent from both readings has no times to confirm anything, so what the watch saw of it
    /// counts even where reports are confirmed by readings: made and removed while the work ran, it
    /// may have been read.
    /// </summary>
    [Fact]
    public async Task AFileMadeAndRemovedWhileTheWorkRan_StillCounts_WhereReportsAreConfirmedByReadings()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        File.Delete(temp.Combine("corpus/sample.txt"));

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, reportsEarlierWrites: true);

        if (watch.Failure is { } failure)
        {
            Assert.Skip($"This machine cannot watch the tree: {failure}");
        }

        temp.WriteFile("corpus/sample.txt", "briefly");
        await WaitForAsync(watch, "corpus/sample.txt", TestContext.Current.CancellationToken);
        File.Delete(temp.Combine("corpus/sample.txt"));

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        var comparison = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Contains("corpus/sample.txt", comparison.Changed);
    }

    /// <summary>
    /// A file replaced while the work ran by one holding the same bytes and dated as it was - as a copy
    /// that keeps times puts one back - still counts where reports are confirmed by readings: the file
    /// in its place was created since.
    /// </summary>
    [Fact]
    public async Task AFileReplacedByItsOwnBytesAndTime_StillCounts_WhereReportsAreConfirmedByReadings()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "NTFS gives a file renamed into a name the creation time of the file that name last held.");

        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        var path = temp.Combine("config/c.lang.json");
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var written = File.GetLastWriteTimeUtc(path);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, reportsEarlierWrites: true);

        if (watch.Failure is { } failure)
        {
            Assert.Skip($"This machine cannot watch the tree: {failure}");
        }

        // Made a while after the original, so a coarse clock that stamps files still tells the two
        // apart; then dated as the original and moved over it.
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        var copy = temp.Combine("config/c.lang.json.copy");
        await File.WriteAllBytesAsync(copy, bytes, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(copy, written);

        if (File.GetCreationTimeUtc(copy) <= written)
        {
            Assert.Skip("The runtime reads no time of creation here, and gives the file's earliest other time instead.");
        }

        File.Move(copy, path, overwrite: true);
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(
            before.Files.Single(file => file.Path == "config/c.lang.json").Written,
            after.Files.Single(file => file.Path == "config/c.lang.json").Written);
        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after, watch).Change);
    }

    /// <summary>Only a watch on macOS can report a write made before it began.</summary>
    [Theory]
    [InlineData(PlatformId.MacOs, true)]
    [InlineData(PlatformId.Linux, false)]
    [InlineData(PlatformId.Windows, false)]
    public void OnlyAWatchOnMacOs_HasItsReportsConfirmedByReadings(PlatformId platform, bool confirmed)
    {
        using var temp = new TempDirectory();
        Seed(temp);

        var host = Substitute.For<IHostPlatform>();
        host.Current.Returns(platform);
        host.PathComparison.Returns(new HostPlatform().PathComparison);

        using var watch = new InputFingerprint(new HarnessFactory().FileSystem, host).Watch(temp.Path, Inputs);

        Assert.Equal(confirmed, watch.ReportsEarlierWrites);
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
