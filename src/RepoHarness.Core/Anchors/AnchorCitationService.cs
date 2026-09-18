using System.Text.Json;
using System.Text.Json.Nodes;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>What a citation check reads.</summary>
/// <remarks>
/// The three answer different questions and none of them substitutes for another: what is committed,
/// what is on disk, and what this change added. A check that only ever read one would either miss a
/// citation still only in the editor or red on one a colleague committed.
/// </remarks>
public enum AnchorCitationSubject
{
    /// <summary>The files as HEAD holds them.</summary>
    CurrentCommit,

    /// <summary>The files as they are on disk, ignored files excluded.</summary>
    CurrentTree,

    /// <summary>The files this branch changed against the merge base with the default branch.</summary>
    CurrentPullRequest,
}

/// <summary>What check-anchor-citations measured.</summary>
/// <param name="Subject">What was read.</param>
/// <param name="SubjectDescription">The subject in words, including the commit it resolved to.</param>
/// <param name="Roots">The declared roots, as configured.</param>
/// <param name="FilesScanned">Files inside a root that were actually read.</param>
/// <param name="CitationsFound">Every citation found, repeats included.</param>
/// <param name="RegistryRows">Rows both registries hold, which a citation must resolve to.</param>
/// <param name="Unresolved">Citations that resolve to no row, in the order they were found.</param>
public sealed record AnchorCitationReport(
    AnchorCitationSubject Subject,
    string SubjectDescription,
    IReadOnlyList<string> Roots,
    int FilesScanned,
    int CitationsFound,
    int RegistryRows,
    IReadOnlyList<AnchorCitation> Unresolved)
{
    /// <summary>Whether every citation resolved.</summary>
    public bool Passed => Unresolved.Count == 0;
}

/// <summary>Checks that every anchor id a scanned root cites resolves to a registry row.</summary>
public interface IAnchorCitationService
{
    /// <summary>Reads <paramref name="subject"/> and reports every citation that resolves nowhere.</summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="subject">What to read.</param>
    /// <param name="cancellationToken">Stops the git processes and the scan.</param>
    /// <exception cref="HarnessException">
    /// No root is declared, so nothing could be scanned; or git could not produce the subject.
    /// </exception>
    Task<AnchorCitationReport> CheckAsync(
        string startDirectory,
        AnchorCitationSubject subject,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnchorCitationService"/>
/// <remarks>
/// Nothing outside a declared root is read. Which files are production files is a judgement a
/// repository makes rather than one a tool can infer: scanning everything reds on ids that were
/// never anchors, and an undeclared scope passes on a citation that resolves to nothing.
/// </remarks>
public sealed class AnchorCitationService(
    IHarnessContextLoader contextLoader,
    IAnchorRegistryService registryService,
    IGitClient gitClient,
    IFileSystem fileSystem) : IAnchorCitationService
{
    /// <summary>
    /// Bytes of a file examined for a NUL before it is scanned, the window git's own text detection
    /// uses. A binary file read as text produces id-shaped noise that resolves nowhere.
    /// </summary>
    private const int BinaryProbeLength = 8000;

    /// <summary>References tried, in order, when the repository does not say which branch is default.</summary>
    private static readonly string[] DefaultBranchCandidates =
        ["origin/main", "origin/master", "main", "master"];

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IAnchorRegistryService _registryService = registryService;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<AnchorCitationReport> CheckAsync(
        string startDirectory,
        AnchorCitationSubject subject,
        CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var root = context.Layout.RepositoryRoot;

        var roots = context.Config.Anchors.CitationRoots
            .Select(AnchorRegistryLocator.Normalize)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (roots.Count == 0)
        {
            // Not a pass. An instrument that looked at nothing and reported success is the shape
            // this command exists to end, so it refuses and names the setting instead.
            throw new HarnessException(
                HarnessExit.Refused,
                "anchors.citationRoots declares no root, so nothing was scanned and nothing was checked. "
                + "Declare the files and directories where every cited anchor id must resolve to a row, "
                + "for example [\"src\", \"docs\", \"README.md\"].");
        }

        var scanner = new AnchorIdScanner(AnchorIdRules.From(context.Config.Anchors));
        var selection = await SelectAsync(root, subject, cancellationToken).ConfigureAwait(false);

        var citations = new List<AnchorCitation>();
        var scanned = 0;
        var inRoots = selection.Paths.Where(path => IsInsideARoot(path, roots)).ToList();

        // Every file of a commit read by one git process. Asked for one at a time, each cost two
        // processes, which was measured at 20 minutes over 2,385 files where the disk took 6.5 seconds.
        var committed = selection.Commit is { } commit
            ? await _gitClient.ReadFilesAtCommitAsync(root, commit, inRoots, cancellationToken).ConfigureAwait(false)
            : null;

        foreach (var path in inRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = committed is not null ? committed[path] ?? throw Unread(path, selection.Commit!) : ReadFromDisk(root, path);

            if (text is null || IsBinary(text))
            {
                continue;
            }

            scanned++;
            citations.AddRange(scanner.Scan(path, text));
        }

        var rows = await _registryService
            .ListAsync(startDirectory, new AnchorListFilter(), cancellationToken)
            .ConfigureAwait(false);

        // By the rule read-anchor finds a row by, so the two verbs cannot disagree about whether a
        // row exists - and a citation cut at the end of its line is reported whatever rows exist,
        // because the id it was cut from is not the one it spells.
        var ids = rows.Select(entry => entry.Row.Id).ToHashSet(AnchorIdMatch.Comparer);
        var unresolved = citations.Where(citation => citation.Cut || !ids.Contains(citation.Id)).ToList();

        return new AnchorCitationReport(
            subject,
            selection.Description,
            roots,
            scanned,
            citations.Count,
            rows.Count,
            unresolved);
    }

    /// <summary>
    /// The refusal for a file the commit lists that git could not read. Skipped, a file whose object
    /// is gone passed a check that read nothing in it.
    /// </summary>
    private static HarnessException Unread(string path, string commit)
        => new(
            HarnessExit.CommandFailed,
            $"git lists '{path}' at {Short(commit)} but could not read it, so the citations in it were not checked. "
            + "Check the repository with 'git fsck'.");

    /// <summary>
    /// Whether a repository-relative path lies inside a declared root. A root names a file or a
    /// directory, and the separator at the boundary is required: a bare prefix test would also pull
    /// in a sibling directory whose name merely begins the same way.
    /// </summary>
    /// <remarks>
    /// Through the one matcher every configured list of paths is read by, so a root written
    /// <c>**/name</c> covers that name at any depth here as it does everywhere else.
    /// </remarks>
    private static bool IsInsideARoot(string path, IReadOnlyList<string> roots)
        => Repository.PathPatterns.Matches(roots, path);

    private static bool IsBinary(string text)
        => text.AsSpan(0, Math.Min(text.Length, BinaryProbeLength)).IndexOf('\0') >= 0;

    /// <summary>
    /// The files in <c>git ls-tree -z</c>'s entries - <c>&lt;mode&gt; &lt;type&gt; &lt;object&gt;TAB&lt;path&gt;</c> -
    /// leaving out what is not one.
    /// </summary>
    private static IReadOnlyList<string> Files(string output)
        => [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.TrimStart('\n', '\r'))
            .Where(entry => entry.IndexOf('\t', StringComparison.Ordinal) is var tab and > 0
                && entry[..tab].Split(' ') is [_, "blob", _])
            .Select(entry => entry[(entry.IndexOf('\t', StringComparison.Ordinal) + 1)..])];

    /// <summary>Splits git's NUL-separated output, which needs no quoting and so loses no path.</summary>
    private static IReadOnlyList<string> SplitPaths(string output)
        => [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(path => path.Trim('\n', '\r'))];

    private async Task<AnchorCitationSelection> SelectAsync(
        string root,
        AnchorCitationSubject subject,
        CancellationToken cancellationToken)
    {
        switch (subject)
        {
            case AnchorCitationSubject.CurrentTree:
            {
                // Tracked files and untracked ones git does not ignore: what a person editing the
                // tree would call "the files", which is what --current-tree is asked about.
                var listed = await RunAsync(
                    root,
                    ["ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                    cancellationToken).ConfigureAwait(false);

                return new AnchorCitationSelection(SplitPaths(listed), "the working tree", null);
            }

            case AnchorCitationSubject.CurrentPullRequest:
            {
                var branch = await ResolveDefaultBranchAsync(root, cancellationToken).ConfigureAwait(false);

                var mergeBase = await RunAsync(root, ["merge-base", branch, "HEAD"], cancellationToken)
                    .ConfigureAwait(false);
                var commit = mergeBase.Trim();

                var changed = await RunAsync(
                    root,
                    ["diff", "--name-only", "-z", "--diff-filter=d", commit, "--"],
                    cancellationToken).ConfigureAwait(false);

                return new AnchorCitationSelection(
                    SplitPaths(changed),
                    $"what this branch changed against {branch} ({Short(commit)})",
                    null);
            }

            default:
            {
                var commit = await _gitClient.ResolveCommitAsync(root, "HEAD", cancellationToken).ConfigureAwait(false)
                    ?? throw new HarnessException(
                        HarnessExit.Refused,
                        "HEAD names no commit, so there is no commit to check. Commit first, or use --current-tree.");

                // Files alone: a submodule's entry names a commit in another repository, which is
                // no file here, and every file listed is one git must then be able to read.
                var listed = await RunAsync(root, ["ls-tree", "-r", "-z", commit], cancellationToken)
                    .ConfigureAwait(false);

                return new AnchorCitationSelection(Files(listed), $"the files at HEAD ({Short(commit)})", commit);
            }
        }
    }

    /// <summary>
    /// A file's text as the disk holds it. A commit's files are read from the commit instead, so a
    /// smudged checkout cannot change what a committed file is judged to say.
    /// </summary>
    private string? ReadFromDisk(string root, string path)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

        // A path git listed can be gone by the time it is read: a file staged and then deleted, or
        // one another process removed. It is not this command's business to report that.
        return _fileSystem.FileExists(full) ? _fileSystem.ReadAllText(full) : null;
    }

    /// <summary>
    /// The branch a pull request would be opened against: what the remote says is its default, and
    /// failing that the conventional names, each tried until one resolves. Guessing one that does
    /// not exist would make <c>--current-pr</c> compare against nothing and pass.
    /// </summary>
    private async Task<string> ResolveDefaultBranchAsync(string root, CancellationToken cancellationToken)
    {
        var declared = await _gitClient
            .RunAsync(root, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (declared.Succeeded && declared.StandardOutput.Trim() is { Length: > 0 } name)
        {
            return name;
        }

        foreach (var candidate in DefaultBranchCandidates)
        {
            if (await _gitClient.ResolveCommitAsync(root, candidate, cancellationToken).ConfigureAwait(false) is not null)
            {
                return candidate;
            }
        }

        throw new HarnessException(
            HarnessExit.Refused,
            $"No default branch was found to compare with (tried the remote's own HEAD, then {string.Join(", ", DefaultBranchCandidates)}). "
            + "Fetch the remote, or use --current-tree or --current-commit.");
    }

    private async Task<string> RunAsync(string root, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await _gitClient.RunAsync(root, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git {string.Join(' ', arguments)} failed, so nothing was checked: {result.FailureMessage}");
        }

        return result.StandardOutput;
    }

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;

    /// <summary>
    /// The files a subject covers, how to describe it, and the commit to read them from when the
    /// subject is a commit rather than the disk.
    /// </summary>
    private sealed record AnchorCitationSelection(IReadOnlyList<string> Paths, string Description, string? Commit);
}

/// <summary>Turns a citation report into what check-anchor-citations prints.</summary>
/// <remarks>
/// Each unresolved citation is written as <c>path:line: id</c>, the form an editor and a log reader
/// both already know how to jump to, so a finding is one click rather than one search.
/// </remarks>
public static class AnchorCitationReports
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>What check-anchor-citations reports.</summary>
    /// <param name="report">What was measured.</param>
    /// <param name="json">Whether to write the findings as JSON instead of as lines.</param>
    public static CommandOutcome Render(AnchorCitationReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<string> data = json
            ? [Json(report)]
            : [.. report.Unresolved.Select(citation => citation.Cut
                ? $"{citation.Path}:{citation.LineNumber}: {citation.Id}- (cut at the end of the line)"
                : $"{citation.Path}:{citation.LineNumber}: {citation.Id}")];

        var looked =
            $"{report.CitationsFound} citation(s) over {report.FilesScanned} file(s) of {report.SubjectDescription}, "
            + $"against {report.RegistryRows} registry row(s)";

        if (report.Passed)
        {
            return CommandOutcome.Ok($"every cited anchor resolves: {looked}") with { Data = data, Quiet = json };
        }

        var distinct = report.Unresolved
            .Select(citation => citation.Id)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return CommandOutcome.Failed(
            AnchorExit.Findings,
            $"{report.Unresolved.Count} citation(s) of {distinct} anchor id(s) resolve to no row in either registry; "
            + $"add the row, or correct the citation. Scanned {looked}.") with
        { Data = data };
    }

    private static string Json(AnchorCitationReport report)
    {
        var node = new JsonObject
        {
            ["subject"] = report.SubjectDescription,
            ["roots"] = new JsonArray([.. report.Roots.Select(root => (JsonNode?)JsonValue.Create(root))]),
            ["filesScanned"] = report.FilesScanned,
            ["citationsFound"] = report.CitationsFound,
            ["registryRows"] = report.RegistryRows,
            ["passed"] = report.Passed,
            ["unresolved"] = new JsonArray([.. report.Unresolved.Select(citation => (JsonNode)new JsonObject
            {
                ["anchor"] = citation.Id,
                ["file"] = citation.Path,
                ["line"] = citation.LineNumber,
                ["cut"] = citation.Cut,
            })]),
        };

        return node.ToJsonString(JsonOptions);
    }
}
