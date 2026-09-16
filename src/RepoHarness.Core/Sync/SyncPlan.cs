using RepoHarness.Core.Results;
using System.Globalization;

namespace RepoHarness.Core.Sync;

/// <summary>What one sync would do to a host's copy.</summary>
/// <param name="Writes">Files whose content differs, or which the copy does not have.</param>
/// <param name="Deletes">Files the copy has and the source does not.</param>
/// <param name="Unchanged">
/// Files already identical. Counted rather than listed, and never rewritten: an unchanged file that
/// is rewritten gets a new modification time, and an incremental build decides what is stale by
/// ordering timestamps. Rewriting everything either rebuilds everything or, worse, leaves a source
/// looking older than the object built from it and the build reports a stale binary as success.
/// </param>
public sealed record SyncPlan(
    IReadOnlyList<SyncEntry> Writes,
    IReadOnlyList<string> Deletes,
    int Unchanged)
{
    /// <summary>
    /// The paths among <see cref="Writes"/> that the copy already had, holding something else.
    /// </summary>
    /// <remarks>
    /// Told apart from a file the copy never had, which is the other half of <see cref="Writes"/> and
    /// costs nobody anything. These are the ones where something is lost: an edit made in the copy and
    /// never committed reads exactly like a file that was simply never there, and a report that did not
    /// separate them would say "would write" over work about to disappear.
    /// </remarks>
    public IReadOnlyList<string> Overwrites { get; init; } = [];

    /// <summary>Whether the copy already matches the source.</summary>
    public bool IsUpToDate => Writes.Count == 0 && Deletes.Count == 0;

    /// <summary>
    /// Compares a source manifest with a copy's and decides what to write and what to delete.
    /// </summary>
    /// <param name="source">The tree being synced from.</param>
    /// <param name="destination">The copy as it stands.</param>
    /// <param name="exclusions">What is withheld from transfer and protected from deletion.</param>
    /// <returns>The plan, before its deletion bound has been checked.</returns>
    /// <remarks>
    /// Files are compared by content and size, never by timestamp order. Nothing here compares two
    /// times taken at different moments or on different machines: one host this tool serves steps
    /// its clock forward by about 25 seconds every few seconds, and a file written after a marker
    /// can carry a stamp from before it.
    /// </remarks>
    public static SyncPlan Between(SyncManifest source, SyncManifest destination, SyncExclusions exclusions)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(exclusions);

        var writes = new List<SyncEntry>();
        var overwrites = new List<string>();
        var unchanged = 0;

        foreach (var path in source.Paths)
        {
            var entry = source.Entries[path];
            var had = destination.Entries.TryGetValue(path, out var existing);

            if (had
                && existing!.Size == entry.Size
                && string.Equals(existing.ContentHash, entry.ContentHash, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            if (had)
            {
                overwrites.Add(path);
            }

            writes.Add(entry);
        }

        var deletes = destination.Paths
            .Where(path => !source.Entries.ContainsKey(path))
            .Where(path => !exclusions.IsProtectedFromDeletion(path))
            .ToList();

        return new SyncPlan(writes, deletes, unchanged) { Overwrites = overwrites };
    }

    /// <summary>
    /// What this plan would cost a copy that already holds work of its own, worst first and bounded,
    /// for a refusal somebody has to read and act on.
    /// </summary>
    /// <param name="most">How many paths to name before the rest are only counted.</param>
    public IReadOnlyList<string> DescribeLoss(int most = 20)
    {
        // Overwrites first. A deleted file is obvious once it is named; a file whose content is
        // replaced looks like an ordinary write in every other report this command prints.
        var lost = new List<string>();
        lost.AddRange(Overwrites.OrderBy(path => path, StringComparer.Ordinal).Select(path => $"overwrite {path}"));
        lost.AddRange(Deletes.OrderBy(path => path, StringComparer.Ordinal).Select(path => $"delete    {path}"));

        if (lost.Count <= most)
        {
            return lost;
        }

        return [.. lost.Take(most), $"...and {(lost.Count - most).ToString(CultureInfo.InvariantCulture)} more"];
    }

    /// <summary>
    /// Refuses a plan that would delete more of the copy than the configuration allows.
    /// </summary>
    /// <param name="destination">The copy as it stands, which is what the share is measured against.</param>
    /// <param name="maxDeleteFraction">The share one sync may delete; zero allows any deletion.</param>
    /// <exception cref="HarnessException">The plan is over the bound, and nothing is changed.</exception>
    /// <remarks>
    /// Deletions have to propagate, or a copy that only ever gains files stops being a copy of the
    /// tree: a deleted source file keeps compiling, a deleted test keeps running, and a renamed file
    /// exists twice. The bound is what keeps the same rule from emptying a host when a repository
    /// path is mistyped, because a source that is not there looks exactly like a source that deleted
    /// everything.
    /// </remarks>
    public void RefuseWhenDeletingTooMuch(SyncManifest destination, double maxDeleteFraction)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (maxDeleteFraction <= 0 || destination.Entries.Count == 0 || Deletes.Count == 0)
        {
            return;
        }

        var share = (double)Deletes.Count / destination.Entries.Count;

        if (share <= maxDeleteFraction)
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.Refused,
            $"This sync would delete {Deletes.Count} of the {destination.Entries.Count} file(s) in "
            + $"'{destination.Root}', which is {share:P0} of it and over the {maxDeleteFraction:P0} "
            + $"sync.maxDeleteFraction allows. Nothing was changed. Confirm the source tree is the one "
            + "you meant, then raise sync.maxDeleteFraction or run with --dry-run to see the list.");
    }

    /// <summary>The lines <c>--dry-run</c> prints, and the same lines a real sync reports afterwards.</summary>
    /// <param name="verb">Either what would happen, or what did.</param>
    public IReadOnlyList<string> Describe(SyncVerb verb)
    {
        var lines = new List<string>();
        var (write, delete) = verb == SyncVerb.Planned ? ("would write", "would delete") : ("wrote", "deleted");

        foreach (var entry in Writes.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            lines.Add($"{write}  {entry.Path}");
        }

        // Deletions are always listed, never summarised: what a sync removed from a host is the one
        // thing nobody can recover by running it again.
        foreach (var path in Deletes)
        {
            lines.Add($"{delete} {path}");
        }

        lines.Add($"unchanged {Unchanged} file(s)");

        return lines;
    }
}

/// <summary>Whether a plan is being described before it runs or after.</summary>
public enum SyncVerb
{
    /// <summary>Nothing has happened yet.</summary>
    Planned,

    /// <summary>It has.</summary>
    Done,
}
