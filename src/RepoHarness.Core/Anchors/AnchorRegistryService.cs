using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Adds, changes, reads and checks anchors.</summary>
public interface IAnchorRegistryService
{
    /// <summary>Adds a new anchor to the registry its status belongs in.</summary>
    Task<AnchorChange> WriteAsync(string startDirectory, AnchorWriteRequest request, bool dryRun, CancellationToken cancellationToken = default);

    /// <summary>Changes an existing anchor, moving it when its new status belongs in the other registry.</summary>
    Task<AnchorChange> SetAsync(string startDirectory, AnchorSetRequest request, bool dryRun, CancellationToken cancellationToken = default);

    /// <summary>Finds anchors by exact id.</summary>
    Task<AnchorLookup> ReadAsync(string startDirectory, IReadOnlyList<string> ids, AnchorScope scope, CancellationToken cancellationToken = default);

    /// <summary>Lists anchors.</summary>
    Task<IReadOnlyList<AnchorEntry>> ListAsync(string startDirectory, AnchorListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Reports every row a reader cannot rely on, and every structural problem in the registries.</summary>
    Task<IReadOnlyList<AnchorFinding>> LintAsync(string startDirectory, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnchorRegistryService"/>
/// <remarks>
/// Where a row lives is decided by its status and nothing else: a closed anchor is in the done
/// registry, every other status in the pending one. A caller never names the destination, so a
/// live anchor cannot be filed in the archive, where nothing reads it as work.
/// </remarks>
public sealed class AnchorRegistryService(
    IHarnessContextLoader contextLoader,
    IAnchorRegistryLocator locator,
    IAnchorRegistryLock registryLock,
    IFileSystem fileSystem) : IAnchorRegistryService
{
    private const int NamespaceHintLimit = 8;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IAnchorRegistryLocator _locator = locator;
    private readonly IAnchorRegistryLock _registryLock = registryLock;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<AnchorChange> WriteAsync(
        string startDirectory,
        AnchorWriteRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);

        // Every value is checked before the registries are opened, so a mistake in the arguments is
        // reported as that and never costs anyone the lock.
        if (!rules.IsMintable(request.Id))
        {
            throw Usage(
                $"'{request.Id}' cannot name a new anchor. A new id is '{rules.Prefix}-' followed by at least "
                + $"{rules.MinimumSegments} hyphen-separated segments of letters, digits or underscores, the "
                + $"first in capitals, for example {rules.Example()}. Spell a compound word as one segment "
                + "(ALWAYSINLINE, not ALWAYS-INLINE).");
        }

        var priority = RequirePriority(request.Priority);
        var status = AnchorStatus.Render(RequireStatus(request.Status));
        RequireTrigger(request.Trigger);

        var row = ComposeRow(
            $" `{request.Id}` ",
            $" {priority} ",
            $" {status} ",
            AnchorCells.Format(request.Trigger, "Trigger"),
            AnchorCells.Format(request.ClosingWork, "Closing work"),
            AnchorCells.Format(request.CrossRefs, "Cross-refs"));

        VerifyRow(row, request.Id, rules);

        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        return _registryLock.RunExclusive(registries, () =>
        {
            var pending = Load(registries.Pending, rules);
            var done = Load(registries.Done, rules);

            var existing = Matches(request.Id, (pending, registries.Pending), (done, registries.Done));
            if (existing.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{request.Id}' already has a row in {Locations(existing)}. write-anchor adds a new anchor; "
                    + "change an existing one with set-anchor.");
            }

            var destination = registries.HomeOf(status);
            var document = destination.Kind == AnchorRegistryKind.Pending ? pending : done;

            document.AppendRow(row);

            if (!dryRun)
            {
                _fileSystem.WriteAllTextAtomic(destination.FullPath, document.ToText());
            }

            return new AnchorChange(request.Id, null, destination, null, status, [], !dryRun);
        });
    }

    public async Task<AnchorChange> SetAsync(
        string startDirectory,
        AnchorSetRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);

        if (!rules.IsWellFormed(request.Id))
        {
            throw Usage(
                $"'{request.Id}' is not an anchor id: an id is '{rules.Prefix}-' followed by hyphen-separated "
                + "letters, digits or underscores.");
        }

        if (!request.HasChanges)
        {
            throw Usage("Nothing to change. Give at least one of --priority, --status, --trigger, --closing or --cross-refs.");
        }

        // Only the cells named are rebuilt; every other cell is written back exactly as it was read.
        var priority = request.Priority is null ? null : RequirePriority(request.Priority);
        var status = request.Status is null ? null : AnchorStatus.Render(RequireStatus(request.Status));

        if (request.Trigger is not null)
        {
            RequireTrigger(request.Trigger);
        }

        var trigger = request.Trigger is null ? null : AnchorCells.Format(request.Trigger, "Trigger");
        var closing = request.ClosingWork is null ? null : AnchorCells.Format(request.ClosingWork, "Closing work");
        var crossRefs = request.CrossRefs is null ? null : AnchorCells.Format(request.CrossRefs, "Cross-refs");

        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        return _registryLock.RunExclusive(registries, () =>
        {
            var pending = Load(registries.Pending, rules);
            var done = Load(registries.Done, rules);

            var all = Matches(request.Id, (pending, registries.Pending), (done, registries.Done));
            var inScope = all.Where(match => InScope(match.Entry.Registry, request.Scope)).ToList();

            if (inScope.Count == 0)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"No row for '{request.Id}' in {ScopeName(request.Scope)}. set-anchor changes an existing "
                    + "anchor; a new one is added with write-anchor.");
            }

            if (all.Count > 1)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{request.Id}' has {all.Count} rows ({Locations(all)}). One id has one row, and which of "
                    + "these is the real one is for a person to decide, not a tool.");
            }

            var (existing, source, sourceDocument) = (inScope[0].Entry.Row, inScope[0].Entry.Registry, inScope[0].Document);
            var cells = AnchorCells.RawCells(existing.RawLine);

            if (existing.CellCount != AnchorRow.ExpectedCellCount || cells.Count != AnchorRow.ExpectedCellCount)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{request.Id}' at {source.RelativePath}:{existing.LineNumber} has {existing.CellCount} cells, not "
                    + $"{AnchorRow.ExpectedCellCount}, so which cell is which cannot be known. Repair the row by hand, "
                    + "then run set-anchor again.");
            }

            var row = ComposeRow(
                cells[0],
                priority is null ? cells[1] : $" {priority} ",
                status is null ? cells[2] : $" {status} ",
                trigger ?? cells[3],
                closing ?? cells[4],
                crossRefs ?? cells[5]);

            VerifyRow(row, existing.Id, rules);

            var fields = new List<AnchorFieldChange>();
            AddChange(fields, "priority", existing.Priority, priority);
            AddChange(fields, "status", existing.Status, status);
            AddChange(fields, "trigger", existing.Trigger, request.Trigger);
            AddChange(fields, "closing", existing.ClosingWork, request.ClosingWork);
            AddChange(fields, "cross-refs", existing.CrossRefs, request.CrossRefs);

            var statusAfter = AnchorCells.Split(row)[3].Trim();
            var destination = registries.HomeOf(statusAfter);
            var destinationDocument = destination.Kind == AnchorRegistryKind.Pending ? pending : done;

            var writes = new List<(string Path, AnchorRegistryDocument Document)>();

            if (destination.Kind == source.Kind)
            {
                sourceDocument.ReplaceRow(existing, row);
                writes.Add((destination.FullPath, sourceDocument));
            }
            else
            {
                destinationDocument.AppendRow(row);
                sourceDocument.RemoveRow(existing);

                // The destination is written first. Two files cannot be replaced in one step, and an
                // interruption between the writes must leave the row in both registries, where the
                // next change refuses the duplicate loudly, rather than in neither, where it would be
                // gone and would read, to every count, exactly like an anchor that was closed.
                writes.Add((destination.FullPath, destinationDocument));
                writes.Add((source.FullPath, sourceDocument));
            }

            if (!dryRun)
            {
                foreach (var (path, document) in writes)
                {
                    _fileSystem.WriteAllTextAtomic(path, document.ToText());
                }
            }

            return new AnchorChange(existing.Id, source, destination, existing.Status, statusAfter, fields, !dryRun);
        });
    }

    public async Task<AnchorLookup> ReadAsync(
        string startDirectory,
        IReadOnlyList<string> ids,
        AnchorScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace))
        {
            throw Usage("Give at least one anchor id, and no empty ones.");
        }

        var documents = await LoadForReadingAsync(startDirectory, cancellationToken).ConfigureAwait(false);

        var entries = Entries(documents).ToList();
        var inScope = entries.Where(entry => InScope(entry.Registry, scope)).ToList();

        var results = ids
            .Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                // Exact and case-sensitive: an id is a name, and a near miss is a different anchor.
                var matches = inScope.Where(entry => string.Equals(entry.Row.Id, id, StringComparison.Ordinal)).ToList();
                var hint = matches.Count == 0 ? SameNamespace(id, entries) : [];
                return new AnchorLookupResult(id, matches, hint);
            })
            .ToList();

        return new AnchorLookup(results);
    }

    public async Task<IReadOnlyList<AnchorEntry>> ListAsync(
        string startDirectory,
        AnchorListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.OnlyOpen && filter.OnlyClosed)
        {
            throw Usage("--open and --closed cannot be combined: together they select nothing.");
        }

        var bands = filter.Bands.Select(RequirePriority).ToHashSet(StringComparer.Ordinal);

        var documents = await LoadForReadingAsync(startDirectory, cancellationToken).ConfigureAwait(false);

        return [.. Entries(documents)
            .Where(entry => InScope(entry.Registry, filter.Scope))
            .Where(entry => bands.Count == 0 || bands.Contains(entry.Row.Priority))
            .Where(entry => !filter.OnlyOpen || !entry.Row.IsClosed)
            .Where(entry => !filter.OnlyClosed || entry.Row.IsClosed)];
    }

    public async Task<IReadOnlyList<AnchorFinding>> LintAsync(string startDirectory, CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        var findings = new List<AnchorFinding>();
        var documents = new List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>();

        foreach (var registry in registries.All)
        {
            if (!_fileSystem.FileExists(registry.FullPath))
            {
                findings.Add(new AnchorFinding(
                    registry.RelativePath,
                    0,
                    AnchorFindingSeverity.Fatal,
                    $"there is no {registry.Name} registry here; run '{ToolPackage.Command} init' to create it"));
                continue;
            }

            var document = AnchorRegistryDocument.Parse(_fileSystem.ReadAllText(registry.FullPath), rules);
            documents.Add((document, registry));

            findings.AddRange(document.Findings.Select(finding =>
                new AnchorFinding(registry.RelativePath, finding.LineNumber, finding.Severity, finding.Message)));

            foreach (var row in document.Rows)
            {
                findings.AddRange(LintRow(row, registry, rules));
            }
        }

        // One id, one row, across both registries: a duplicate hands a reader two histories under one name.
        foreach (var group in Entries(documents).GroupBy(entry => entry.Row.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var locations = string.Join(", ", group.Select(entry => $"{entry.Registry.RelativePath}:{entry.Row.LineNumber}"));

            findings.AddRange(group.Select(entry => new AnchorFinding(
                entry.Registry.RelativePath,
                entry.Row.LineNumber,
                AnchorFindingSeverity.Fatal,
                $"'{group.Key}' has {group.Count()} rows ({locations}); one id has one row")));
        }

        return [.. findings
            .OrderBy(finding => finding.File == registries.Pending.RelativePath ? 0 : 1)
            .ThenBy(finding => finding.LineNumber)];
    }

    private static IEnumerable<AnchorFinding> LintRow(AnchorRow row, AnchorRegistry registry, AnchorIdRules rules)
    {
        AnchorFinding Finding(string message) =>
            new(registry.RelativePath, row.LineNumber, AnchorFindingSeverity.Fatal, message);

        if (row.CellCount != AnchorRow.ExpectedCellCount)
        {
            yield return Finding(
                $"{row.CellCount} cells, not {AnchorRow.ExpectedCellCount}: the cells after the break are shifted or dropped");
        }

        if (!rules.IsBareBacktickedId(row.AnchorCell))
        {
            yield return Finding($"the Anchor cell is not one id in backticks: {AnchorCells.Excerpt(row.AnchorCell, 70)}");
        }

        if (!AnchorPriority.IsBand(row.Priority))
        {
            yield return Finding($"priority '{row.Priority}' is not one of {string.Join(' ', AnchorPriority.Bands)}");
        }

        if (!AnchorStatus.IsCanonical(row.Status))
        {
            yield return Finding($"status '{row.Status}' is not one of {string.Join(" / ", AnchorStatus.Cells)}");
        }

        if (row.Trigger.Length == 0)
        {
            yield return Finding("the Trigger cell is empty, so the row explains nothing");
        }

        if (registry.Misfiling(row) is { } misfiled)
        {
            yield return misfiled;
        }
    }

    private async Task<List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>> LoadForReadingAsync(
        string startDirectory,
        CancellationToken cancellationToken)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        // Reading takes no lock: every write replaces a whole file in one rename, so a reader sees
        // either the old file or the new one, never a mixture.
        return [.. registries.All.Select(registry => (Load(registry, rules), registry))];
    }

    /// <summary>
    /// Reads a registry for a command that reads or changes anchors. A file with a structural problem is
    /// refused rather than read around: its rows could be miscounted, or given a second row, without anyone
    /// being told. Lint and the balance parse the files themselves, because reporting those problems is
    /// their job.
    /// </summary>
    private AnchorRegistryDocument Load(AnchorRegistry registry, AnchorIdRules rules)
    {
        if (!_fileSystem.FileExists(registry.FullPath))
        {
            throw new HarnessException(
                HarnessExit.NotInitialized,
                $"There is no {registry.Name} anchor registry at '{registry.RelativePath}'. Run '{ToolPackage.Command} init' "
                + $"to create it, or correct {registry.Setting} in config.json.");
        }

        var document = AnchorRegistryDocument.Parse(_fileSystem.ReadAllText(registry.FullPath), rules);
        document.EnsureSound(registry.RelativePath);
        return document;
    }

    private static IEnumerable<AnchorEntry> Entries(IEnumerable<(AnchorRegistryDocument Document, AnchorRegistry Registry)> documents)
        => documents.SelectMany(pair => pair.Document.Rows.Select(row => new AnchorEntry(row, pair.Registry)));

    private static List<(AnchorEntry Entry, AnchorRegistryDocument Document)> Matches(
        string id,
        params (AnchorRegistryDocument Document, AnchorRegistry Registry)[] documents)
        => [.. documents.SelectMany(pair => pair.Document.Rows
            .Where(row => string.Equals(row.Id, id, StringComparison.Ordinal))
            .Select(row => (new AnchorEntry(row, pair.Registry), pair.Document)))];

    private static string Locations(IEnumerable<(AnchorEntry Entry, AnchorRegistryDocument Document)> matches)
        => string.Join(", ", matches.Select(match => $"{match.Entry.Registry.RelativePath}:{match.Entry.Row.LineNumber}"));

    private static List<string> SameNamespace(string id, IEnumerable<AnchorEntry> entries)
    {
        var segments = id.Split('-');
        if (segments.Length < 2)
        {
            return [];
        }

        var prefix = segments[0] + "-" + segments[1];

        return [.. entries
            .Select(entry => entry.Row.Id)
            .Where(candidate => candidate == prefix || candidate.StartsWith(prefix + "-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(NamespaceHintLimit)];
    }

    private static bool InScope(AnchorRegistry registry, AnchorScope scope) => scope switch
    {
        AnchorScope.Pending => registry.Kind == AnchorRegistryKind.Pending,
        AnchorScope.Done => registry.Kind == AnchorRegistryKind.Done,
        _ => true,
    };

    private static string ScopeName(AnchorScope scope) => scope switch
    {
        AnchorScope.Pending => "the pending registry",
        AnchorScope.Done => "the done registry",
        _ => "either registry",
    };

    private static string ComposeRow(params string[] cells) => "|" + string.Join('|', cells) + "|";

    /// <summary>
    /// Reads the composed row back through the same parser every command uses, so a defect in
    /// composing a row is caught here rather than by the next person to read the registry.
    /// </summary>
    private static void VerifyRow(string row, string id, AnchorIdRules rules)
    {
        var pieces = AnchorCells.Split(row);

        if (pieces.Count != AnchorRow.ExpectedCellCount + 2 || !string.Equals(rules.Identify(pieces[1]), id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The row composed for '{id}' does not read back as that anchor: {row}");
        }
    }

    private static void AddChange(List<AnchorFieldChange> fields, string field, string before, string? after)
    {
        if (after is null)
        {
            return;
        }

        var shown = AnchorCells.Collapse(after);
        if (!string.Equals(before, shown, StringComparison.Ordinal))
        {
            fields.Add(new AnchorFieldChange(field, before, shown));
        }
    }

    private static string RequirePriority(string? value)
        => AnchorPriority.TryNormalize(value, out var band)
            ? band
            : throw Usage($"'{value}' is not a priority. Use one of {string.Join(", ", AnchorPriority.Bands)}; P0 is the most urgent.");

    private static AnchorState RequireStatus(string? value)
        => AnchorStatus.TryParse(value, out var state)
            ? state
            : throw Usage($"'{value}' is not a status. Use one of {string.Join(", ", AnchorStatus.Words)}.");

    private static void RequireTrigger(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            throw Usage(
                "The Trigger is empty. A row says what is wrong and what would make it worth doing; a status "
                + "alone explains nothing to the next person who reads it.");
        }
    }

    private static HarnessException Usage(string message) => new(HarnessExit.UsageError, message);
}
