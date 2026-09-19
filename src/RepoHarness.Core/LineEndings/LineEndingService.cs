using System.Text.Json;
using System.Text.Json.Nodes;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.LineEndings;

/// <summary>Which files the policy is applied to.</summary>
public enum LineEndingScope
{
    /// <summary>Every file git tracks.</summary>
    All,

    /// <summary>The working set: what is staged, and what is changed and not staged.</summary>
    Changed,
}

/// <summary>The line ending a path's declared policy asks for.</summary>
public enum DeclaredLineEnding
{
    /// <summary>The policy declares none for this path, so nothing is rewritten.</summary>
    None,

    /// <summary>The path is binary, by declaration or by content, so nothing is rewritten.</summary>
    Binary,

    /// <summary>A single line feed.</summary>
    Lf,

    /// <summary>A carriage return and a line feed.</summary>
    Crlf,
}

/// <summary>One file the policy would change, or did.</summary>
/// <param name="Path">The file, relative to the repository root, with forward slashes.</param>
/// <param name="Ending">What the policy declares for it.</param>
/// <param name="CarriageReturnsBefore">Carriage returns the file held before, for the report.</param>
public sealed record LineEndingChange(string Path, DeclaredLineEnding Ending, int CarriageReturnsBefore);

/// <summary>What fix-line-endings was asked to do.</summary>
/// <param name="Scope">Which files to cover.</param>
/// <param name="CheckOnly">Refuse instead of rewriting, which is what the guard this replaces did.</param>
public sealed record LineEndingRequest(LineEndingScope Scope, bool CheckOnly);

/// <summary>What fix-line-endings measured.</summary>
/// <param name="Scope">Which files were covered.</param>
/// <param name="CheckOnly">Whether the run was only allowed to look.</param>
/// <param name="FilesConsidered">Files the scope named, before anything was skipped.</param>
/// <param name="Binary">Files skipped because git treats them as binary.</param>
/// <param name="Undeclared">Files skipped because the policy declares no ending for them.</param>
/// <param name="Excluded">Files skipped because <c>lineEndings.exclude</c> names them.</param>
/// <param name="Changed">Files rewritten, or that a check found needing it.</param>
public sealed record LineEndingReport(
    LineEndingScope Scope,
    bool CheckOnly,
    int FilesConsidered,
    int Binary,
    int Undeclared,
    int Excluded,
    IReadOnlyList<LineEndingChange> Changed);

/// <summary>Applies the repository's declared line-ending policy to the files on disk.</summary>
public interface ILineEndingService
{
    /// <summary>Applies the policy, or reports what applying it would change.</summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="request">Which files, and whether to rewrite them.</param>
    /// <param name="cancellationToken">Stops the git processes and the rewrite.</param>
    /// <exception cref="HarnessException">git could not say what the policy declares.</exception>
    Task<LineEndingReport> ApplyAsync(
        string startDirectory,
        LineEndingRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILineEndingService"/>
/// <remarks>
/// <para>
/// The policy is read from <c>.gitattributes</c> through <c>git check-attr text eol</c> and is never
/// restated in code or in configuration. One statement of it then governs both what git stores and
/// what this rewrites; a second copy eventually disagrees with the first, and the file deciding a
/// checkout's bytes would not be the one anybody edited.
/// </para>
/// <para>
/// A path the policy says nothing about is left alone rather than given a default. A default would be
/// this tool inventing a policy, and the file it rewrote would then differ from what git checks out.
/// </para>
/// </remarks>
public sealed class LineEndingService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem) : ILineEndingService
{
    /// <summary>
    /// Bytes examined for a NUL before a file is rewritten, the window git's own text detection uses.
    /// A file declared <c>text=auto</c> that holds one is binary to git whatever the declaration says,
    /// and rewriting its bytes would corrupt it.
    /// </summary>
    private const int BinaryProbeLength = 8000;

    /// <summary>
    /// Paths named in one <c>git check-attr</c> invocation. Batched because a command line is bounded
    /// -- 32,767 characters on Windows -- and a repository has more tracked files than that allows.
    /// </summary>
    private const int AttributeBatchSize = 200;

    /// <summary>
    /// Characters of path a single batch may carry, well inside the bound above, so a tree of long
    /// paths is batched by what it actually costs rather than by a count that assumes short names.
    /// </summary>
    private const int AttributeBatchCharacters = 8000;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<LineEndingReport> ApplyAsync(
        string startDirectory,
        LineEndingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var root = context.Layout.RepositoryRoot;

        var excludes = context.Config.LineEndings.Exclude
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .ToList();

        var considered = await SelectAsync(root, request.Scope, cancellationToken).ConfigureAwait(false);
        var reached = considered.Where(name => !IsExcluded(name.Text, excludes)).ToList();

        // Before anything is read or rewritten: read as UTF-8, such a name became another, which the
        // disk did not have, and the file was passed over without a word.
        if (reached.FirstOrDefault(name => !name.IsUtf8) is { } unnamed)
        {
            throw unnamed.Unreadable("its line endings cannot be checked");
        }

        var covered = reached.Select(name => name.Text).ToList();
        var declared = await ReadPolicyAsync(root, covered, cancellationToken).ConfigureAwait(false);

        var changed = new List<LineEndingChange>();
        var binary = 0;
        var undeclared = 0;

        foreach (var path in covered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ending = declared.GetValueOrDefault(path, DeclaredLineEnding.None);

            if (ending == DeclaredLineEnding.Binary)
            {
                binary++;
                continue;
            }

            if (ending == DeclaredLineEnding.None)
            {
                undeclared++;
                continue;
            }

            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

            if (!_fileSystem.FileExists(full))
            {
                continue;
            }

            var before = File.ReadAllBytes(full);

            if (IsBinary(before))
            {
                binary++;
                continue;
            }

            var after = Rewrite(before, ending);

            if (before.AsSpan().SequenceEqual(after))
            {
                continue;
            }

            changed.Add(new LineEndingChange(path, ending, Count(before, (byte)'\r')));

            if (!request.CheckOnly)
            {
                await _fileSystem.WriteAllBytesAtomicAsync(full, after, cancellationToken).ConfigureAwait(false);
            }
        }

        return new LineEndingReport(
            request.Scope,
            request.CheckOnly,
            considered.Count,
            binary,
            undeclared,
            considered.Count - covered.Count,
            changed);
    }

    /// <summary>
    /// The bytes the policy asks for. Existing CRLF pairs are collapsed first so that a CRLF target
    /// cannot double a carriage return it already has, which is how a naive replace turns one
    /// mixed-ending file into an unreadable one.
    /// </summary>
    private static byte[] Rewrite(byte[] content, DeclaredLineEnding ending)
    {
        var written = new List<byte>(content.Length);

        for (var index = 0; index < content.Length; index++)
        {
            var byteValue = content[index];

            if (byteValue == (byte)'\r' && index + 1 < content.Length && content[index + 1] == (byte)'\n')
            {
                continue;
            }

            if (byteValue == (byte)'\n' && ending == DeclaredLineEnding.Crlf)
            {
                written.Add((byte)'\r');
            }

            written.Add(byteValue);
        }

        return [.. written];
    }

    private static bool IsBinary(byte[] content)
        => content.AsSpan(0, Math.Min(content.Length, BinaryProbeLength)).IndexOf((byte)0) >= 0;

    private static int Count(byte[] content, byte value)
    {
        var found = 0;

        foreach (var item in content)
        {
            if (item == value)
            {
                found++;
            }
        }

        return found;
    }

    private static string Normalize(string path)
        => string.Join('/', path.Split('/', '\\').Where(segment => segment.Length > 0 && segment != "."));

    /// <summary>
    /// Whether <paramref name="path"/> is covered by <paramref name="excludes"/>, by the one rule
    /// every configured list of paths is read by.
    /// </summary>
    /// <remarks>
    /// The same matcher <c>sync.exclude</c> uses, so <c>**/node_modules</c> means here what it means
    /// there. Compared as literal text, as this was, an entry written that way covered nothing and
    /// said nothing — a rule that protects nothing while reading, to anybody looking at the file, as
    /// evidence that it does.
    /// </remarks>
    private static bool IsExcluded(string path, IReadOnlyList<string> excludes)
        => PathPatterns.Matches(excludes, path);

    /// <summary>
    /// The files <paramref name="scope"/> covers, each once: git lists a file in conflict once for
    /// each side of it, and one both staged and changed since in both lists. Compared whole, since two
    /// names that are not UTF-8 can read alike and still be two files.
    /// </summary>
    private async Task<IReadOnlyList<GitName>> SelectAsync(
        string root,
        LineEndingScope scope,
        CancellationToken cancellationToken)
    {
        if (scope == LineEndingScope.All)
        {
            var tracked = await _gitClient.ListNamesAsync(root, ["ls-files", "-z", "--cached"], cancellationToken)
                .ConfigureAwait(false);

            return [.. tracked.Distinct()];
        }

        // Staged and unstaged, and nothing else: an untracked file is in neither, and rewriting one
        // would change a file the repository has never been asked to store.
        var unstaged = await _gitClient.ListNamesAsync(root, ["diff", "--name-only", "-z", "--diff-filter=d"], cancellationToken)
            .ConfigureAwait(false);
        var staged = await _gitClient.ListNamesAsync(root, ["diff", "--name-only", "-z", "--diff-filter=d", "--cached"], cancellationToken)
            .ConfigureAwait(false);

        return [.. unstaged.Concat(staged).Distinct()];
    }

    /// <summary>
    /// What <c>.gitattributes</c> declares for each path, asked of git rather than parsed here: git's
    /// own precedence between patterns, directories and the repository's info attributes is the
    /// policy, and a second implementation of it would be a second policy.
    /// </summary>
    private async Task<Dictionary<string, DeclaredLineEnding>> ReadPolicyAsync(
        string root,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var declared = new Dictionary<string, DeclaredLineEnding>(StringComparer.Ordinal);

        foreach (var batch in Batch(paths))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var output = await RunAsync(
                root,
                ["check-attr", "-z", "text", "eol", "--", .. batch],
                cancellationToken).ConfigureAwait(false);

            // NUL-separated triples of path, attribute and value. A path carrying a space or a quote
            // survives this where git's default quoting would have to be undone first.
            var fields = output.Split('\0');

            for (var index = 0; index + 2 < fields.Length; index += 3)
            {
                var path = fields[index];
                var attribute = fields[index + 1];
                var value = fields[index + 2];

                var current = declared.GetValueOrDefault(path, DeclaredLineEnding.None);

                declared[path] = (attribute, value) switch
                {
                    // `-text` is git's own way of saying "these bytes are the file", so the policy
                    // does not reach it.
                    ("text", "unset") => DeclaredLineEnding.Binary,
                    ("eol", "lf") when current != DeclaredLineEnding.Binary => DeclaredLineEnding.Lf,
                    ("eol", "crlf") when current != DeclaredLineEnding.Binary => DeclaredLineEnding.Crlf,
                    _ => current,
                };
            }
        }

        return declared;
    }

    /// <summary>Groups paths into invocations neither too many nor too long for one command line.</summary>
    private static IEnumerable<List<string>> Batch(IReadOnlyList<string> paths)
    {
        var batch = new List<string>();
        var characters = 0;

        foreach (var path in paths)
        {
            if (batch.Count > 0 && (batch.Count >= AttributeBatchSize || characters + path.Length > AttributeBatchCharacters))
            {
                yield return batch;
                batch = [];
                characters = 0;
            }

            batch.Add(path);
            characters += path.Length + 1;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private async Task<string> RunAsync(string root, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await _gitClient.RunAsync(root, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git {arguments[0]} failed, so the declared policy could not be read and nothing was rewritten: "
                + result.FailureMessage);
        }

        return result.StandardOutput;
    }
}

/// <summary>Turns a line-ending report into what fix-line-endings prints.</summary>
public static class LineEndingReports
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>What fix-line-endings reports.</summary>
    /// <param name="report">What was measured.</param>
    /// <param name="json">Whether to write the result as JSON instead of as lines.</param>
    public static CommandOutcome Render(LineEndingReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<string> data = json
            ? [Json(report)]
            : [.. report.Changed.Select(change => $"{change.Path}   -> {Name(change.Ending)}")];

        var looked =
            $"{report.FilesConsidered} file(s) considered, {report.Binary} binary, "
            + $"{report.Undeclared} with no declared ending, {report.Excluded} excluded";

        if (report.Changed.Count == 0)
        {
            return CommandOutcome.Ok($"every file already matches the declared policy: {looked}") with
            { Data = data, Quiet = json };
        }

        if (report.CheckOnly)
        {
            // Refused rather than reported as a finding: --check is asked whether the tree may be
            // used as it is, and the answer is no.
            return CommandOutcome.Refused(
                $"{report.Changed.Count} file(s) do not match the declared line-ending policy. "
                + $"Run this without --check to rewrite them. {looked}.") with
            { Data = data };
        }

        return CommandOutcome.Ok($"{report.Changed.Count} file(s) rewritten to the declared policy: {looked}") with
        { Data = data };
    }

    private static string Name(DeclaredLineEnding ending) => ending switch
    {
        DeclaredLineEnding.Lf => "LF",
        DeclaredLineEnding.Crlf => "CRLF",
        DeclaredLineEnding.Binary => "binary",
        _ => "undeclared",
    };

    private static string Json(LineEndingReport report)
    {
        var node = new JsonObject
        {
            ["scope"] = report.Scope == LineEndingScope.All ? "all" : "changed",
            ["check"] = report.CheckOnly,
            ["considered"] = report.FilesConsidered,
            ["binary"] = report.Binary,
            ["undeclared"] = report.Undeclared,
            ["excluded"] = report.Excluded,
            ["changed"] = new JsonArray([.. report.Changed.Select(change => (JsonNode)new JsonObject
            {
                ["file"] = change.Path,
                ["ending"] = Name(change.Ending),
                ["carriageReturnsBefore"] = change.CarriageReturnsBefore,
            })]),
        };

        return node.ToJsonString(JsonOptions);
    }
}
