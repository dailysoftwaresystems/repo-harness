using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

public sealed class AnchorRegistryServiceTests
{
    private const string One = "D-AREA-TOPIC-ONE";
    private const string Two = "D-AREA-TOPIC-TWO";
    private const string Three = "D-AREA-TOPIC-THREE";

    [Fact]
    public async Task WriteAsync_AppendsAnOpenAnchorToThePendingRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var first = await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        Assert.True(first.IsNew);
        Assert.True(first.Written);
        Assert.Equal(AnchorRegistryKind.Pending, first.To.Kind);

        var text = File.ReadAllText(PendingPath(temp));
        Assert.EndsWith(
            $"| `{One}` | P1 | 🟠 OPEN | trigger for {One} | work | refs |\n| `{Two}` | P1 | 🟠 OPEN | trigger for {Two} | work | refs |\n",
            text,
            StringComparison.Ordinal);
        Assert.Empty(Rows(harness, DonePath(temp)));
    }

    [Fact]
    public async Task WriteAsync_FilesAClosedAnchorStraightIntoTheDoneRegistry()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var change = await harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(One, "closed"), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(AnchorRegistryKind.Done, change.To.Kind);
        Assert.Equal([One], Rows(harness, DonePath(temp)).Select(row => row.Id));
        Assert.Empty(Rows(harness, PendingPath(temp)));
    }

    [Fact]
    public async Task WriteAsync_RefusesAnIdThatAlreadyHasARow_InEitherRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two, "closed"), dryRun: false, cancellationToken);

        var again = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken));
        var reopened = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Equal(HarnessExit.Refused, reopened.ExitCode);
        Assert.Contains("set-anchor", again.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D-TWO-SEGMENTS", "P1", "open", "t")]
    [InlineData(One, "P9", "open", "t")]
    [InlineData(One, "P1", "done", "t")]
    [InlineData(One, "P1", "open", "  ")]
    [InlineData(One, "P1", "open", @"already \| escaped")]
    public async Task WriteAsync_RefusesAnInvalidValue_AndChangesNothing(string id, string priority, string status, string trigger)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var before = File.ReadAllText(PendingPath(temp));

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.WriteAsync(
            temp.Path,
            new AnchorWriteRequest(id, priority, trigger) { Status = status },
            dryRun: false,
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
    }

    [Fact]
    public async Task WriteAsync_WithADryRun_ReportsTheChange_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var before = File.ReadAllText(PendingPath(temp));

        var change = await harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(One), dryRun: true, TestContext.Current.CancellationToken);

        Assert.False(change.Written);
        Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
    }

    [Fact]
    public async Task SetAsync_RebuildsOnlyTheCellsGiven_AndKeepsTheRestByteForByte()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` |  P1  | 🟠 OPEN | spaced  out \\| text |  work | refs |\n");

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "p0" }, dryRun: false, TestContext.Current.CancellationToken);

        Assert.EndsWith(
            $"| `{One}` | P0 | 🟠 OPEN | spaced  out \\| text |  work | refs |\n",
            File.ReadAllText(PendingPath(temp)),
            StringComparison.Ordinal);

        var field = Assert.Single(change.Fields);
        Assert.Equal(("priority", "P1", "P0"), (field.Field, field.Before, field.After));
        Assert.False(change.Moved);
    }

    [Fact]
    public async Task SetAsync_Closing_MovesTheRowToTheEndOfTheDoneRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Three, "closed"), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed", ClosingWork = "fixed" }, dryRun: false, cancellationToken);

        Assert.True(change.Moved);
        Assert.Equal((AnchorRegistryKind.Pending, AnchorRegistryKind.Done), (change.From!.Kind, change.To.Kind));
        Assert.Equal([Two], Rows(harness, PendingPath(temp)).Select(row => row.Id));

        var done = Rows(harness, DonePath(temp));
        Assert.Equal([Three, One], done.Select(row => row.Id));
        Assert.Equal(("✅ CLOSED", "fixed", $"trigger for {One}"), (done[1].Status, done[1].ClosingWork, done[1].Trigger));
    }

    [Fact]
    public async Task SetAsync_Reopening_MovesTheRowBackToPending()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One, "closed"), dryRun: false, cancellationToken);

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "open" }, dryRun: false, cancellationToken);

        Assert.True(change.Moved);
        Assert.Equal([One], Rows(harness, PendingPath(temp)).Select(row => row.Id));
        Assert.Empty(Rows(harness, DonePath(temp)));
    }

    [Theory]
    [InlineData("gated", "⏳ GATED")]
    [InlineData("disclosed", "🔵 DISCLOSED")]
    public async Task SetAsync_EveryLiveStatus_StaysInPending(string status, string cell)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = status }, dryRun: false, cancellationToken);

        Assert.False(change.Moved);
        Assert.Equal(cell, Assert.Single(Rows(harness, PendingPath(temp))).Status);
    }

    [Fact]
    public async Task SetAsync_RefusesAnIdWithNoRow_WhereItWasToldToLook()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var missing = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(Two) { Priority = "P2" }, dryRun: false, cancellationToken));
        var wrongRegistry = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Scope = AnchorScope.Done, Priority = "P2" }, dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.Refused, missing.ExitCode);
        Assert.Contains("write-anchor", missing.Message, StringComparison.Ordinal);
        Assert.Contains("the done registry", wrongRegistry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_RefusesAnIdWithTwoRows()
    {
        // Which of two rows is the real one is for a person to decide.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | 🟠 OPEN | one | - | - |\n");
        File.AppendAllText(DonePath(temp), $"| `{One}` | P1 | ✅ CLOSED | other | - | - |\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "P2" }, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("2 rows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_RefusesARowWhoseCellsCannotBeTold_Apart()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | 🟠 OPEN |\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "P2" }, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Contains("3 cells", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_WithNothingToChange_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One), dryRun: false, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
    }

    [Fact]
    public async Task AnInterruptedMove_LeavesTheRowInTheDestination_NeverInNeither()
    {
        // A move is two writes. A row lost between them would read, to every count, exactly like
        // an anchor that was closed; a row left in both is refused loudly on the next change.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var crashing = new AnchorRegistryService(
            harness.ContextLoader,
            harness.AnchorRegistryLocator,
            harness.AnchorRegistryLock,
            new FailingWriteFileSystem(harness.FileSystem, failOnWrite: 2));

        await Assert.ThrowsAsync<IOException>(() => crashing.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken));

        Assert.Equal([One], Rows(harness, DonePath(temp)).Select(row => row.Id));
        Assert.Equal([One], Rows(harness, PendingPath(temp)).Select(row => row.Id));

        var next = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "P0" }, dryRun: false, cancellationToken));
        Assert.Contains("2 rows", next.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChange_RefusesWhenAnotherHoldsTheLockTooLong_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var registries = await harness.AnchorRegistryLocator.LocateAsync(context, cancellationToken);
        var before = File.ReadAllText(PendingPath(temp));

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() => harness.AnchorRegistryLock.RunExclusive(registries, () =>
        {
            held.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return 0;
        }));

        holder.Start();

        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(60), cancellationToken), "The holder never took the lock.");

            var impatient = new AnchorRegistryService(
                harness.ContextLoader,
                harness.AnchorRegistryLocator,
                new NamedMutexAnchorRegistryLock(harness.Platform, TimeSpan.FromMilliseconds(200)),
                harness.FileSystem);

            var exception = await Assert.ThrowsAsync<HarnessException>(() =>
                impatient.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken));

            Assert.Equal(HarnessExit.Refused, exception.ExitCode);
            Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public async Task ConcurrentChanges_LoseNoAnchor()
    {
        // Without the lock, each writer reads the registry before the others write, and the last
        // write silently discards every other anchor.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var ids = Enumerable.Range(1, 8).Select(index => $"D-AREA-CONCURRENT-WRITER{index}").ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(
            () => harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(id), dryRun: false, cancellationToken),
            cancellationToken)));

        Assert.Equal(ids.Order(StringComparer.Ordinal), Rows(harness, PendingPath(temp)).Select(row => row.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_AnswersEveryId_InTheOrderAsked()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two, "closed"), dryRun: false, cancellationToken);

        var lookup = await harness.AnchorRegistryService.ReadAsync(temp.Path, [Two, One], AnchorScope.All, cancellationToken);

        Assert.Equal([Two, One], lookup.Results.Select(result => result.Id));
        Assert.Empty(lookup.Missing);
        Assert.Equal(AnchorRegistryKind.Done, Assert.Single(lookup.Results[0].Matches).Registry.Kind);
        Assert.Equal(AnchorRegistryKind.Pending, Assert.Single(lookup.Results[1].Matches).Registry.Kind);
    }

    [Fact]
    public async Task ReadAsync_MatchesExactly_AndListsWhatIsMissing_WithIdsBeginningTheSameWay()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var lookup = await harness.AnchorRegistryService.ReadAsync(
            temp.Path, [One.ToLowerInvariant(), "D-AREA-OTHER-THING", "D-NOWHERE-AT-ALL"], AnchorScope.All, cancellationToken);

        Assert.Equal(3, lookup.Missing.Count);
        Assert.Equal([One], lookup.Missing[1].SameNamespace);
        Assert.Empty(lookup.Missing[2].SameNamespace);
    }

    [Fact]
    public async Task ReadAsync_ShowsEveryRowOfADuplicatedId()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | 🟠 OPEN | one | - | - |\n| `{One}` | P2 | 🟠 OPEN | two | - | - |\n");

        var lookup = await harness.AnchorRegistryService.ReadAsync(temp.Path, [One], AnchorScope.All, TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(lookup.Results).Matches.Count);
    }

    [Fact]
    public async Task ListAsync_FiltersByRegistry_Priority_AndStatus()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, new AnchorWriteRequest(Two, "P3", "t"), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Three, "closed"), dryRun: false, cancellationToken);

        async Task<IEnumerable<string>> ListAsync(AnchorListFilter filter)
            => (await harness.AnchorRegistryService.ListAsync(temp.Path, filter, cancellationToken)).Select(entry => entry.Row.Id);

        Assert.Equal([One, Two, Three], await ListAsync(new AnchorListFilter()));
        Assert.Equal([Three], await ListAsync(new AnchorListFilter { Scope = AnchorScope.Done }));
        Assert.Equal([Two], await ListAsync(new AnchorListFilter { Bands = ["p3"] }));
        Assert.Equal([One, Two], await ListAsync(new AnchorListFilter { OnlyOpen = true }));
        Assert.Equal([Three], await ListAsync(new AnchorListFilter { OnlyClosed = true }));

        var both = await Assert.ThrowsAsync<HarnessException>(() => ListAsync(new AnchorListFilter { OnlyOpen = true, OnlyClosed = true }));
        Assert.Equal(HarnessExit.UsageError, both.ExitCode);
    }

    [Fact]
    public async Task LintAsync_IsClean_ForRegistriesTheCommandsWrote()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two, "closed"), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "gated" }, dryRun: false, cancellationToken);

        Assert.Empty(await harness.AnchorRegistryService.LintAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task LintAsync_ReportsEveryKindOfProblem()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), string.Join("\n",
            "| `D-LINT-CLOSED-HERE` | P1 | ✅ CLOSED | closed in pending | - | - |",
            "| `D-LINT-BAD-PRIORITY` | P9 | 🟠 OPEN | t | - | - |",
            "| `D-LINT-BAD-STATUS` | P1 | ORANGE | t | - | - |",
            "| `D-LINT-EMPTY-TRIGGER` | P1 | 🟠 OPEN |  | - | - |",
            "| `D-LINT-SHORT-ROW` | P1 |",
            "| D-LINT-NO-BACKTICKS | P1 | 🟠 OPEN | t | - | - |",
            "| `D-LINT-TWICE-OVER` | P1 | 🟠 OPEN | t | - | - |") + "\n");
        File.AppendAllText(DonePath(temp), string.Join("\n",
            "| `D-LINT-OPEN-HERE` | P1 | 🟠 OPEN | open in done | - | - |",
            "| `D-LINT-TWICE-OVER` | P1 | ✅ CLOSED | t | - | - |") + "\n");

        var findings = await harness.AnchorRegistryService.LintAsync(temp.Path, TestContext.Current.CancellationToken);

        string[] expected =
        [
            "closed anchor 'D-LINT-CLOSED-HERE' is in the pending registry",
            "priority 'P9'",
            "status 'ORANGE'",
            "Trigger cell is empty",
            "2 cells, not 6",
            "not one id in backticks",
            "'D-LINT-TWICE-OVER' has 2 rows",
            "live anchor 'D-LINT-OPEN-HERE' is in the done registry",
        ];

        foreach (var text in expected)
        {
            Assert.Contains(findings, finding => finding.Message.Contains(text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ATrackedRegistry_IsChangedInTheWorktreeTheCommandRunsIn()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(repository);
        await harness.CommitAllAsync(repository.Path, "harness", cancellationToken);

        var worktree = elsewhere.Combine("wt");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", worktree], cancellationToken);

        await harness.AnchorRegistryService.WriteAsync(worktree, Anchor(One), dryRun: false, cancellationToken);

        Assert.Contains(One, File.ReadAllText(Path.Combine(worktree, ".plans", "_deferred-anchor-registry.md")), StringComparison.Ordinal);
        Assert.DoesNotContain(One, File.ReadAllText(PendingPath(repository)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIgnoredRegistry_IsChangedInTheMainCheckout_FromAWorktree()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);

        repository.WriteFile(".gitignore", "local/\n");
        harness.WriteConfig(repository.Path, new HarnessConfig
        {
            Anchors = new AnchorSettings { PendingAnchorsPath = "local/pending.md", DoneAnchorsPath = "local/done.md" },
        });

        await harness.InitService.InitializeAsync(repository.Path, cancellationToken);
        await harness.CommitAllAsync(repository.Path, "harness", cancellationToken);

        var worktree = elsewhere.Combine("wt");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", worktree], cancellationToken);

        var change = await harness.AnchorRegistryService.WriteAsync(worktree, Anchor(One), dryRun: false, cancellationToken);

        Assert.True(change.To.IsIgnored);
        Assert.Contains(One, File.ReadAllText(repository.Combine("local", "pending.md")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(worktree, "local", "pending.md")));
    }

    [Fact]
    public async Task AMissingRegistry_IsReportedAsNotInitialised()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.Delete(DonePath(temp));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorRegistryService.ListAsync(temp.Path, new AnchorListFilter(), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.NotInitialized, exception.ExitCode);
        Assert.Contains("dssharness init", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no anchor table")]
    [InlineData("a second anchor table")]
    [InlineData("a row outside the table")]
    [InlineData("a row in another table")]
    public async Task AMalformedRegistry_IsNeitherReadNorWritten(string problem)
    {
        // Read around the problem, a registry could miscount an anchor or give it a second row, and
        // nobody would be told. Reporting it is --lint's job.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var registry = File.ReadAllText(PendingPath(temp));
        var malformed = problem switch
        {
            "no anchor table" => "# Someone removed the table\n",
            "a second anchor table" => registry
                + $"\nMore anchors:\n\n{AnchorRegistryDocument.TableHeader}\n{AnchorRegistryDocument.SeparatorRow}\n| `{Two}` | P1 | 🟠 OPEN | t | - | - |\n",
            "a row outside the table" => registry + $"\nA paragraph.\n\n| `{Two}` | P1 | 🟠 OPEN | stray | - | - |\n",
            _ => registry + $"\n| Id | Note |\n|---|---|\n| `{Two}` | kept elsewhere |\n",
        };

        File.WriteAllText(PendingPath(temp), malformed);

        Func<Task>[] commands =
        [
            () => harness.AnchorRegistryService.ReadAsync(temp.Path, [One], AnchorScope.All, cancellationToken),
            () => harness.AnchorRegistryService.ListAsync(temp.Path, new AnchorListFilter(), cancellationToken),
            () => harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken),
            () => harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Priority = "P0" }, dryRun: false, cancellationToken),
        ];

        foreach (var command in commands)
        {
            var exception = await Assert.ThrowsAsync<HarnessException>(command);

            Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
            Assert.Contains("read-anchors --lint", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(malformed, File.ReadAllText(PendingPath(temp)));
    }

    [Fact]
    public async Task SetAsync_ChangesAnExistingIdTheMintingRuleWouldRefuse()
    {
        // Ids already in a registry are never re-checked against the minting rule: a row nobody could
        // maintain is worse than an old spelling, and renaming an id orphans every citation of it.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), "| `D-OLD-NAME` | P1 | 🟠 OPEN | an id from before the rule | - | - |\n");

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest("D-OLD-NAME") { Status = "closed" }, dryRun: false, TestContext.Current.CancellationToken);

        Assert.True(change.Moved);
        Assert.Empty(Rows(harness, PendingPath(temp)));

        var row = Assert.Single(Rows(harness, DonePath(temp)));
        Assert.Equal(("D-OLD-NAME", "✅ CLOSED"), (row.Id, row.Status));
    }

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken);
        return harness;
    }

    private static AnchorWriteRequest Anchor(string id, string status = "open")
        => new(id, "P1", $"trigger for {id}") { Status = status, ClosingWork = "work", CrossRefs = "refs" };

    private static string PendingPath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry.md");

    private static string DonePath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry-done.md");

    private static IReadOnlyList<AnchorRow> Rows(HarnessFactory harness, string path)
        => AnchorRegistryDocument.Parse(File.ReadAllText(path), new AnchorIdRules("D", 3)).Rows;

    /// <summary>A file system that fails one atomic write, as a crash between the two writes of a move would.</summary>
    private sealed class FailingWriteFileSystem(IFileSystem inner, int failOnWrite) : IFileSystem
    {
        private int _writes;

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void DeleteFile(string path) => inner.DeleteFile(path);

        public string ResolveLinks(string path) => inner.ResolveLinks(path);


        public Stream OpenRead(string path) => inner.OpenRead(path);

        public DateTime LastWriteTimeUtc(string path) => inner.LastWriteTimeUtc(path);


        public Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)

            => inner.WriteAllBytesAtomicAsync(path, contents, cancellationToken);

        public string CopyToTemporaryFile(string path) => inner.CopyToTemporaryFile(path);
        public void CopyFile(string source, string destination, bool overwrite = false)
            => inner.CopyFile(source, destination, overwrite);

        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

        public IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);

        public IEnumerable<string> EnumerateDirectoryLinks(string path) => inner.EnumerateDirectoryLinks(path);

        public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);

        public string ReadAllText(string path) => inner.ReadAllText(path);

        public void ProtectSecretFile(string path) => inner.ProtectSecretFile(path);

        public void WriteAllTextAtomic(string path, string contents)
        {
            if (Interlocked.Increment(ref _writes) == failOnWrite)
            {
                throw new IOException("Simulated failure on the second write of a move.");
            }

            inner.WriteAllTextAtomic(path, contents);
        }
    }
}
