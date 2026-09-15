using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Checks that a change did not leave more open anchors than it found.</summary>
public interface IAnchorBalanceService
{
    /// <summary>Compares the registries in the working tree with the registries at <paramref name="baseReference"/>.</summary>
    Task<AnchorBalanceReport> CheckAsync(string startDirectory, string? baseReference, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnchorBalanceService"/>
/// <remarks>
/// Anchors are compared by id across both registries, so moving a row from one to the other is
/// neither progress nor regression. The count that must not rise is open anchors, less those newly
/// disclosed: a disclosed anchor records debt that already existed. The registries are also checked
/// as they stand, because a closed anchor left in the pending registry, or an open one in the done
/// registry, makes both counts wrong.
/// </remarks>
public sealed class AnchorBalanceService(
    IHarnessContextLoader contextLoader,
    IAnchorRegistryLocator locator,
    IGitClient gitClient,
    IFileSystem fileSystem) : IAnchorBalanceService
{
    /// <summary>The base used when none is given.</summary>
    public const string DefaultBase = "HEAD";

    private const int ExcerptLength = 80;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IAnchorRegistryLocator _locator = locator;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<AnchorBalanceReport> CheckAsync(
        string startDirectory,
        string? baseReference,
        CancellationToken cancellationToken = default)
    {
        var reference = string.IsNullOrWhiteSpace(baseReference) ? DefaultBase : baseReference.Trim();

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        foreach (var registry in registries.All.Where(registry => registry.IsIgnored))
        {
            // Treating an ignored registry as empty at the base would report every open anchor in it
            // as newly opened: a failure on every run, which a check is soon taught to ignore.
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{registry.RelativePath}' is ignored by git, so it has no history to compare against. "
                + "check-anchor-balance can only measure a registry git tracks.");
        }

        var root = context.Layout.RepositoryRoot;

        var commit = await _gitClient.ResolveCommitAsync(root, reference, cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{reference}' does not name a commit, so there is nothing to compare against.");

        var missingAtBase = new List<string>();
        var atBase = new List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>();

        foreach (var registry in registries.All)
        {
            var text = await _gitClient
                .ReadFileAtCommitAsync(root, commit, registry.RelativePath, cancellationToken)
                .ConfigureAwait(false);

            if (text is null)
            {
                missingAtBase.Add(registry.RelativePath);
                continue;
            }

            atBase.Add((AnchorRegistryDocument.Parse(text, rules), registry));
        }

        var findings = new List<AnchorFinding>();
        var now = new List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>();

        foreach (var registry in registries.All)
        {
            if (!_fileSystem.FileExists(registry.FullPath))
            {
                findings.Add(new AnchorFinding(
                    registry.RelativePath,
                    0,
                    AnchorFindingSeverity.Fatal,
                    registry.Kind == AnchorRegistryKind.Done
                        ? "there is no done registry, so closing an anchor has nowhere to move it and this check cannot tell a closed anchor from a lost one"
                        : "there is no pending registry, so no open anchor can be counted"));
                continue;
            }

            var document = AnchorRegistryDocument.Parse(_fileSystem.ReadAllText(registry.FullPath), rules);
            now.Add((document, registry));

            findings.AddRange(document.Findings.Select(finding =>
                new AnchorFinding(registry.RelativePath, finding.LineNumber, finding.Severity, finding.Message)));

            // A misfiled row is refused as it stands rather than compared with the base: an open row in
            // the done registry is never picked up as work, whatever the base held.
            findings.AddRange(document.Rows.Select(registry.Misfiling).OfType<AnchorFinding>());
        }

        var openAtBase = OpenAnchors(atBase);
        var openNow = OpenAnchors(now);

        var closed = openAtBase.Keys
            .Where(id => !openNow.ContainsKey(id))
            .Order(StringComparer.Ordinal)
            .ToList();

        var opened = openNow
            .Where(pair => !openAtBase.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AnchorOpening(
                pair.Key,
                AnchorCells.Excerpt(pair.Value.Trigger, ExcerptLength),
                AnchorStatus.IsDisclosed(pair.Value.Status)))
            .ToList();

        return new AnchorBalanceReport(
            reference,
            commit,
            openAtBase.Count,
            openNow.Count,
            closed,
            opened,
            missingAtBase,
            [.. findings.OrderBy(finding => finding.File, StringComparer.Ordinal).ThenBy(finding => finding.LineNumber)]);
    }

    /// <summary>
    /// Every id with at least one open row, across both registries. An id with an open row and a closed
    /// one counts as open: a closed copy must never hide an open original.
    /// </summary>
    private static Dictionary<string, AnchorRow> OpenAnchors(IEnumerable<(AnchorRegistryDocument Document, AnchorRegistry Registry)> documents)
    {
        var open = new Dictionary<string, AnchorRow>(StringComparer.Ordinal);

        foreach (var row in documents.SelectMany(pair => pair.Document.Rows).Where(row => !row.IsClosed))
        {
            open.TryAdd(row.Id, row);
        }

        return open;
    }
}
