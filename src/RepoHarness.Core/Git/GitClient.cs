using System.Globalization;
using System.Text;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <inheritdoc cref="IGitClient"/>
public sealed class GitClient(IProcessRunner processRunner, IHarnessOutput output) : IGitClient
{
    private const string GitExecutable = "git";

    /// <summary>
    /// Variables a caller's environment may carry that would redirect git away from the directory
    /// it was pointed at. Cleared before every git command this client runs.
    /// </summary>
    /// <remarks>
    /// Each one silently outranks <c>-C &lt;directory&gt;</c>. A git hook runs with all three set, so
    /// a harness command invoked from a hook — or from a shell someone left in another checkout —
    /// reads and writes a repository nobody named.
    /// </remarks>
    private static readonly string[] InheritedGitEnvironment =
        ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"];

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHarnessOutput _output = output;

    public bool IsInstalled() => _processRunner.FindExecutable(GitExecutable) is not null;

    public async Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--is-inside-work-tree"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // A repository git refuses to open is not "not a repository". Answering that
            // sends the user to `git init` inside a repository that already exists, when
            // git's own message (dubious ownership, most often) names the exact remedy.
            EnsureNotAnError(result, directory);
            return false;
        }

        return string.Equals(result.StandardOutput.Trim(), "true", StringComparison.Ordinal);
    }

    public async Task<string?> GetRepositoryRootAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            EnsureNotAnError(result, directory);
            return null;
        }

        var root = result.StandardOutput.Trim();
        if (root.Length == 0)
        {
            // Older git answers a bare repository with success and no output.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git found a repository at '{directory}' but reported no working tree for it.");
        }

        return NormalizeDirectory(root);
    }

    public async Task<GitWorktree?> GetMainWorktreeAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        // Taken from git's worktree list rather than derived from --git-common-dir. Asked
        // from the main checkout, that answer is the relative ".git", so the root would be
        // spelled the way the caller typed the directory, while GetRepositoryRootAsync
        // answers with symbolic links resolved. On macOS, where /var links to /private/var,
        // one checkout would carry two spellings and compare unequal to itself. git lists
        // worktrees in the same resolved form as --show-toplevel.
        var result = await RunQueryAsync(
            directory,
            ["worktree", "list", "--porcelain"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            EnsureNotAnError(result, directory);
            return null;
        }

        // git always lists the main worktree first.
        return ParseWorktrees(result.StandardOutput).FirstOrDefault();
    }

    public async Task<bool> IsDirtyAsync(string directory, CancellationToken cancellationToken = default)
    {
        var entries = await GetStatusAsync(directory, cancellationToken).ConfigureAwait(false);
        return entries.Count > 0;
    }

    public async Task<IReadOnlyList<string>> GetStatusAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        // -z keeps paths NUL separated so a path containing a space or a quote is
        // never mangled by the textual quoting git would otherwise apply. Untracked
        // files and submodules are asked for explicitly because configuration can hide
        // both: under status.showUntrackedFiles=no a new file is not listed at all, and
        // the tree reads as clean to a caller about to discard it. --no-optional-locks
        // keeps the question from writing anything: status otherwise refreshes the index
        // and writes it back whenever it can take the lock.
        var result = await RunAsync(
            directory,
            ["--no-optional-locks", "status", "--porcelain", "-z", "--untracked-files=normal", "--ignore-submodules=none"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // An empty list must mean "nothing changed", never "the question failed":
        // a caller reads it as a clean tree and proceeds over uncommitted work.
        Ensure(result, "read the repository status");

        return ParseStatus(result.StandardOutput);
    }

    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            directory,
            ["worktree", "list", "--porcelain"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Reporting an empty list would read as "this repository has no worktrees",
        // which is a different answer from "I could not find out".
        Ensure(result, "list the worktrees");

        return ParseWorktrees(result.StandardOutput);
    }

    public async Task<GitLocation?> GetLocationAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--show-prefix", "--absolute-git-dir"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            EnsureNotAnError(result, directory);
            return null;
        }

        // Read by position: at the root of a work tree the prefix is an empty line, and
        // dropping empty lines would take the git directory for the prefix.
        var lines = result.StandardOutput.Split('\n');

        if (lines.Length < 2 || lines[1].TrimEnd('\r').Length == 0)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git did not report where '{directory}' sits in its repository.");
        }

        return new GitLocation(lines[0].TrimEnd('\r'), NormalizeDirectory(lines[1].TrimEnd('\r')));
    }

    public async Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        // -v is what shows the flags that hide an edit from status, and -z keeps every path intact.
        var result = await RunAsync(
            directory,
            ["ls-files", "--stage", "-v", "-z"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(result, "read the index");

        var entries = new List<GitIndexEntry>();

        foreach (var field in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // The tag, mode, object and stage, separated by spaces, then a tab and the path.
            var tab = field.IndexOf('\t');
            string[] parts = tab < 0 ? [] : field[..tab].Split(' ');

            if (parts.Length != 4
                || parts[0].Length != 1
                || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var stage))
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git listed an index entry in a form this build cannot read: '{field}'");
            }

            entries.Add(new GitIndexEntry(parts[0][0], parts[1], parts[2], stage, field[(tab + 1)..]));
        }

        return entries;
    }

    public async Task<string> GetIndexFileAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            directory,
            ["rev-parse", "--git-path", "index"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(result, "find the index");

        // git answers relative to the directory it ran in, where it can.
        return Path.GetFullPath(Path.Combine(directory, result.StandardOutput.Trim()));
    }

    public async Task<IReadOnlyList<string>> FindEditedFilesAsync(
        string directory,
        string indexCopy,
        IReadOnlyList<string> assumedUnchanged,
        IReadOnlyList<string> skipWorktree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assumedUnchanged);
        ArgumentNullException.ThrowIfNull(skipWorktree);

        // Each mark is cleared by a call of its own: given both options, update-index clears only
        // the first. The paths travel on standard input, so no list of them outgrows a command line.
        foreach (var (option, paths) in new[] { ("--no-assume-unchanged", assumedUnchanged), ("--no-skip-worktree", skipWorktree) })
        {
            if (paths.Count == 0)
            {
                continue;
            }

            // --no-split-index writes the copy whole: a split index would otherwise be written as
            // a new shared index file beside the real one, in the worktree's git directory.
            var cleared = await RunWithIndexAsync(
                directory,
                indexCopy,
                ["update-index", "--no-split-index", option, "-z", "--stdin"],
                string.Join('\0', paths) + '\0',
                cancellationToken).ConfigureAwait(false);

            Ensure(cleared, "clear the marks in a copy of the index");
        }

        // Status compares the files the way it compares any other. Hashing one alone would skip
        // git's rule that leaves line endings already stored in the index as they are, and report
        // an untouched file as edited under core.autocrlf.
        var status = await RunWithIndexAsync(
            directory,
            indexCopy,
            ["--no-optional-locks", "status", "--porcelain", "-z", "--untracked-files=no", "--ignore-submodules=all"],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        Ensure(status, "compare the files marked assume-unchanged or skip-worktree");

        var marked = new HashSet<string>([.. assumedUnchanged, .. skipWorktree], StringComparer.Ordinal);

        return [.. ParseStatus(status.StandardOutput).Select(entry => entry[3..]).Where(marked.Contains)];
    }

    public async Task<string?> ResolveGitDirectoryAsync(
        string directory,
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--resolve-git-dir", path],
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return NormalizeDirectory(result.StandardOutput.Trim());
        }

        // "not a gitdir" is git's answer that the path is no repository; any other failure is git
        // unable to look, which must not read as a directory holding nothing.
        if (!result.TimedOut && result.StandardError.Contains("not a gitdir", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"git could not tell whether '{path}' is a repository: {result.FailureMessage}");
    }

    public async Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            directory,
            ["rev-list", "--count", .. revisions],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ReadCount(result);
    }

    public async Task<int> CountRepositoryCommitsAsync(
        string gitDirectory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            gitDirectory,
            [.. RepositoryOnly(gitDirectory), "rev-list", "--count", .. revisions],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ReadCount(result);
    }

    public async Task<bool> HasStashAsync(string gitDirectory, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            gitDirectory,
            [.. RepositoryOnly(gitDirectory), "rev-parse", "--verify", "--quiet", "refs/stash^{commit}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // With --verify --quiet, git answers "no stash" with exit code 1 and nothing else; any other
        // failure is git being unable to look.
        return result.ExitCode switch
        {
            0 when !result.TimedOut => true,
            1 when !result.TimedOut => false,
            _ => throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not look for a stash in '{gitDirectory}': {result.FailureMessage}"),
        };
    }

    public async Task<bool> IsIgnoredAsync(
        string directory,
        string path,
        CancellationToken cancellationToken = default)
    {
        // check-ignore's exit code is three valued: 0 matched, 1 did not match, and
        // anything else is a failure. Folding the third into "not ignored" would
        // report a secret as safe to copy because the question itself errored.
        var result = await RunAsync(
            directory,
            ["check-ignore", "--quiet", "--no-index", path],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.ExitCode switch
        {
            0 when !result.TimedOut => true,
            1 when !result.TimedOut => false,
            _ => throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not determine whether '{path}' is ignored: {result.FailureMessage}"),
        };
    }

    public async Task<IReadOnlyList<IgnoreDecision>> ExplainIgnoredAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return [];
        }

        var result = await CheckIgnoreAsync(directory, paths, cancellationToken).ConfigureAwait(false);

        if (Answered(result))
        {
            return ReadDecisions(result.StandardOutput, paths);
        }

        // One path git will not answer about - one beyond a symbolic link, which git never looks past -
        // ends the whole question. Asked one at a time, every other path is still answered, and that one
        // says why it was not; where none is, git cannot answer at all.
        if (paths.Count > 1 && !result.TimedOut)
        {
            var decisions = new List<IgnoreDecision>(paths.Count);

            foreach (var path in paths)
            {
                var alone = await CheckIgnoreAsync(directory, [path], cancellationToken).ConfigureAwait(false);

                decisions.Add(Answered(alone)
                    ? ReadDecisions(alone.StandardOutput, [path])[0]
                    : new IgnoreDecision(path, null, 0, null) { Unanswered = alone.FailureMessage });
            }

            if (decisions.Any(decision => decision.Unanswered is null))
            {
                return decisions;
            }
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"Could not ask git which rules decide {paths.Count} path(s) in '{directory}': {result.FailureMessage}");
    }

    /// <summary>Asks git which rule decides each of <paramref name="paths"/>, as <c>check-ignore</c> answers.</summary>
    /// <remarks>
    /// NUL-separated both ways, so a path or a rule may hold anything a line can; and every path
    /// answered, matched or not, so the answer lines up with the question.
    /// </remarks>
    private Task<GitCommandResult> CheckIgnoreAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        => RunForBytesAsync(
            directory,
            ["check-ignore", "--verbose", "--non-matching", "--no-index", "-z", "--stdin"],
            cancellationToken,
            string.Join('\0', paths) + '\0');

    /// <summary>
    /// Whether <c>check-ignore</c> answered: 0 when some path matched a rule and 1 when none did.
    /// Anything else is git unable to answer, and folded into "nothing matched" it would report every
    /// path asked about as unruled.
    /// </summary>
    private static bool Answered(GitCommandResult result) => !result.TimedOut && result.ExitCode is 0 or 1;

    /// <summary>What <c>check-ignore -v -n -z</c> printed, one decision per path of <paramref name="paths"/>, in order.</summary>
    /// <exception cref="HarnessException">git answered in another form, or about other paths.</exception>
    internal static IReadOnlyList<IgnoreDecision> ReadDecisions(string output, IReadOnlyList<string> paths)
    {
        // Four fields to a path - source, line, pattern, path - and nothing after the last NUL.
        var fields = output.Split('\0');

        if (fields.Length != (paths.Count * 4) + 1)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git answered which rules decide {paths.Count} path(s) in a form this build cannot read.");
        }

        var decisions = new List<IgnoreDecision>(paths.Count);

        for (var index = 0; index < paths.Count; index++)
        {
            var source = GitName.FromBytes(fields[index * 4]).Quoted;
            var pattern = GitName.FromBytes(fields[(index * 4) + 2]).Quoted;
            var path = GitName.FromBytes(fields[(index * 4) + 3]).Text;

            if (!string.Equals(path, paths[index], StringComparison.Ordinal))
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git answered about '{path}' where it was asked about '{paths[index]}'.");
            }

            if (source.Length == 0)
            {
                decisions.Add(new IgnoreDecision(path, null, 0, null));
                continue;
            }

            if (!int.TryParse(fields[(index * 4) + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var line))
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git answered which rule decides '{path}' in a form this build cannot read: "
                    + $"'{fields[(index * 4) + 1]}' is not a line number.");
            }

            decisions.Add(new IgnoreDecision(path, source, line, pattern));
        }

        return decisions;
    }

    public async Task<string?> ResolveCommitAsync(
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        // --end-of-options keeps a reference that starts with a dash from being read as an option,
        // and ^{commit} refuses a tag or tree that is not a commit.
        var result = await RunAsync(
            directory,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", reference + "^{commit}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return result.StandardOutput.Trim();
        }

        // With --verify --quiet, git answers "no such commit" with exit code 1 and nothing else;
        // any other failure is git being unable to look.
        if (result.ExitCode == 1 && !result.TimedOut)
        {
            return null;
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"Could not resolve '{reference}': {result.FailureMessage}");
    }

    public async Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        // Spelled by this machine, so its own separator is turned into git's; nothing else is, as a
        // backslash is an ordinary character in a name on Linux.
        var path = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var read = await ReadFilesAtCommitAsync(directory, commit, [path], cancellationToken).ConfigureAwait(false);

        return read[path];
    }

    public async Task<IReadOnlyDictionary<string, string?>> ReadFilesAtCommitAsync(
        string directory,
        string commit,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(relativePaths);

        // As git spells them, and keyed as given: rewritten, a name holding a backslash - an ordinary
        // character on Linux - named another file, and its answer was filed under a key the caller
        // never asked for.
        var paths = relativePaths.Distinct(StringComparer.Ordinal).ToList();
        var read = paths.ToDictionary(path => path, _ => (string?)null, StringComparer.Ordinal);

        // Each named to git one to a line, as '<commit>:<path>'; a path holding a line break - which
        // git allows and no line can carry - by the object the commit's listing gives it instead.
        var files = paths.Any(HoldsLineBreak)
            ? await FilesAtCommitAsync(directory, commit, cancellationToken).ConfigureAwait(false)
            : null;

        var asked = paths.Where(path => !HoldsLineBreak(path) || files!.ContainsKey(path)).ToList();

        if (asked.Count > 0)
        {
            var objects = asked.Select(path => HoldsLineBreak(path) ? files![path] : $"{commit}:{path}").ToList();
            var answers = await ReadObjectsAsync(directory, commit, objects, cancellationToken).ConfigureAwait(false);

            foreach (var (path, content) in asked.Zip(answers))
            {
                read[path] = content;
            }
        }

        // git answers "missing" for a file whose object it cannot read exactly as it does for a path
        // that names no file, and only the commit's listing tells the two apart. Passed off as absent,
        // a file nobody could read passed a check that read nothing in it.
        var unanswered = asked.Where(path => read[path] is null).ToList();

        if (unanswered.Count > 0)
        {
            files ??= await FilesAtCommitAsync(directory, commit, cancellationToken).ConfigureAwait(false);

            if (unanswered.FirstOrDefault(files.ContainsKey) is { } unread)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git lists '{unread}' at {commit} but could not read it. Check the repository with 'git fsck'.");
            }
        }

        return read;
    }

    public async Task<IReadOnlyList<GitName>> ListFilesAtCommitAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken = default)
        => [.. (await ListTreeAsync(directory, commit, cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.IsFile)
            .Select(entry => entry.Name)];

    public async Task<IReadOnlyList<GitName>> ListNamesAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await RunForBytesAsync(directory, arguments, cancellationToken).ConfigureAwait(false);

        Ensure(result, $"run git {string.Join(' ', arguments)}");

        return [.. Records(result.StandardOutput).Select(GitName.FromBytes)];
    }

    /// <summary>
    /// Every entry of <paramref name="commit"/>'s tree, from the repository's root, as
    /// <c>git ls-tree -r</c> lists it.
    /// </summary>
    private async Task<IReadOnlyList<TreeEntry>> ListTreeAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        var result = await RunForBytesAsync(directory, ["ls-tree", "-r", "-z", "--full-tree", commit], cancellationToken)
            .ConfigureAwait(false);

        Ensure(result, $"list the files at {commit}");

        // '<mode> <type> <object>TAB<name>': the name is everything after the first tab, so one
        // holding a tab keeps it.
        return [.. Records(result.StandardOutput).Select(entry =>
            entry.IndexOf('\t', StringComparison.Ordinal) is var tab and > 0
                && entry[..tab].Split(' ') is [_, var type, var objectId]
                ? new TreeEntry(type, objectId, GitName.FromBytes(entry[(tab + 1)..]))
                : throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git listed a tree entry in a form this build cannot read: '{GitName.FromBytes(entry).Quoted}'"))];
    }

    /// <summary>
    /// Each file at <paramref name="commit"/> whose name is UTF-8 - so one a caller can ask for - with
    /// the object git holds its bytes in.
    /// </summary>
    private async Task<Dictionary<string, string>> FilesAtCommitAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken)
        => (await ListTreeAsync(directory, commit, cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.IsFile && entry.Name.IsUtf8)
            .ToDictionary(entry => entry.Name.Text, entry => entry.ObjectId, StringComparer.Ordinal);

    private static bool HoldsLineBreak(string path) => path.IndexOfAny(['\n', '\r']) >= 0;

    /// <summary>What git separated with NUL - its <c>-z</c> form - one record to an element.</summary>
    private static string[] Records(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Runs git with its output read as Latin-1, which carries each byte as one character: for an
    /// answer that is not all text, or whose names are not all UTF-8.
    /// </summary>
    private Task<GitCommandResult> RunForBytesAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null)
        => RunCoreAsync(
            directory,
            arguments,
            echoOutput: false,
            untranslated: false,
            indexFile: null,
            standardInput,
            cancellationToken,
            Encoding.Latin1);

    /// <summary>
    /// The objects <paramref name="objects"/> names, in order, read by one <c>git cat-file --batch</c>:
    /// each file's text, or <see langword="null"/> for a name that is not a file git could read.
    /// </summary>
    /// <remarks>
    /// The commit is asked about first, in the same process: git answers "missing" for a commit it
    /// cannot read exactly as it does for a file that was not there, and an unreadable commit passed
    /// off as absent files would pass a check that read nothing. Read as bytes, so the answer is cut
    /// by the byte counts git gives, and each file is then decoded as its own text.
    /// </remarks>
    private async Task<IReadOnlyList<string?>> ReadObjectsAsync(
        string directory,
        string commit,
        IReadOnlyList<string> objects,
        CancellationToken cancellationToken)
    {
        var input = new StringBuilder().Append(commit).Append("^{commit}\n");

        foreach (var name in objects)
        {
            input.Append(name).Append('\n');
        }

        var result = await RunForBytesAsync(directory, ["cat-file", "--batch"], cancellationToken, input.ToString())
            .ConfigureAwait(false);

        Ensure(result, $"read {objects.Count} file(s) at {commit}");

        var answers = new BatchAnswers(result.StandardOutput);

        if (answers.Next() is not { Type: "commit" })
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read {objects.Count} file(s) at {commit}: git cannot read that commit.");
        }

        return [.. objects.Select(_ => answers.Next() is { Type: "blob" } blob ? Decoded(blob.Content) : null)];
    }

    /// <summary>A file's bytes, carried one to a character, as the text they are.</summary>
    /// <remarks>
    /// Read as a file on disk is read: a byte order mark says which encoding the rest is in - UTF-16,
    /// as a Windows PowerShell 5.1 redirect writes it - and is not part of the text, and UTF-8 is
    /// assumed where there is none. Read as UTF-8 regardless, a UTF-16 file became every other
    /// character a NUL, was taken for binary and skipped, and the commit passed a check that the
    /// same file on disk failed.
    /// </remarks>
    private static string Decoded(string bytes)
    {
        using var reader = new StreamReader(
            new MemoryStream(Encoding.Latin1.GetBytes(bytes)),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads git's batch answers in order: for each object asked about, its header, then as many
    /// bytes as the header names, then a line break; or one line saying it names nothing.
    /// </summary>
    private sealed class BatchAnswers(string output)
    {
        private int _position;

        /// <summary>The next object's type and bytes, or <see langword="null"/> when it names nothing.</summary>
        /// <exception cref="HarnessException">git answered for fewer objects, or with fewer bytes, than it said.</exception>
        public (string Type, string Content)? Next()
        {
            var end = output.IndexOf('\n', _position);

            if (end < 0)
            {
                throw Short();
            }

            var header = output[_position..end].Split(' ');
            _position = end + 1;

            // '<object id> <type> <size>'; anything else - '<name> missing', '<name> ambiguous' -
            // names nothing, and has no content after it.
            if (header is not [var id, var type, var bytes]
                || !id.All(char.IsAsciiHexDigit)
                || !int.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                return null;
            }

            if (_position + size >= output.Length)
            {
                throw Short();
            }

            var content = output.Substring(_position, size);
            _position += size + 1;

            return (type, content);
        }

        private static HarnessException Short()
            => new(HarnessExit.CommandFailed, "git cat-file answered with less than it said it would.");
    }

    /// <summary>One entry of a commit's tree, as <c>git ls-tree</c> lists it.</summary>
    /// <param name="Type">What the entry is: <c>blob</c>, <c>tree</c>, or <c>commit</c> for a submodule's.</param>
    /// <param name="ObjectId">The object git holds the entry in.</param>
    /// <param name="Name">The entry's path, from the repository's root.</param>
    private sealed record TreeEntry(string Type, string ObjectId, GitName Name)
    {
        /// <summary>
        /// Whether the entry is a file - a symbolic link is one, its text the path it points at -
        /// rather than a directory, or a submodule's entry, which names a commit in another repository.
        /// </summary>
        public bool IsFile => Type == "blob";
    }

    public Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(directory, arguments, echoOutput, untranslated: false, cancellationToken);

    /// <summary>
    /// Runs a read-only query whose failure message is inspected, with git's messages
    /// untranslated.
    /// </summary>
    /// <remarks>
    /// "not a git repository" and "not a gitdir" are recognised by their text, and a translated
    /// git answers in another language, so these queries run under <c>LC_ALL=C</c>. The override is kept
    /// to them alone: every other git command, including those that run the user's hooks,
    /// keeps the user's locale, because forcing C onto a hook changes how it handles
    /// anything outside ASCII.
    /// </remarks>
    private Task<GitCommandResult> RunQueryAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => RunCoreAsync(directory, arguments, echoOutput: false, untranslated: true, cancellationToken);

    private Task<GitCommandResult> RunCoreAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput,
        bool untranslated,
        CancellationToken cancellationToken)
        => RunCoreAsync(directory, arguments, echoOutput, untranslated, indexFile: null, standardInput: null, cancellationToken);

    /// <summary>
    /// Runs git against <paramref name="indexFile"/> instead of the work tree's own index: a copy
    /// made to be changed, so the real index is never touched.
    /// </summary>
    private Task<GitCommandResult> RunWithIndexAsync(
        string directory,
        string indexFile,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
        => RunCoreAsync(directory, arguments, echoOutput: false, untranslated: false, indexFile, standardInput, cancellationToken);

    private async Task<GitCommandResult> RunCoreAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput,
        bool untranslated,
        string? indexFile,
        string? standardInput,
        CancellationToken cancellationToken,
        Encoding? outputEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Without this, an operation that decides to ask for credentials blocks
            // forever with no output. A hang is the one failure this tool cannot
            // report, so git is told up front that nobody is watching.
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

        // Cleared before every question, not only before every change. These three override the
        // repository, the working tree and the index that `-C <directory>` would otherwise select,
        // so a hook, or a command started from another checkout, steers every answer git gives:
        // the harness would then read one tree's status and act on another's. A caller that
        // genuinely wants a different index passes it below, after the inherited one is gone.
        foreach (var inherited in InheritedGitEnvironment)
        {
            environment[inherited] = null;
        }

        if (untranslated)
        {
            environment["LC_ALL"] = "C";
        }

        if (indexFile is not null)
        {
            environment["GIT_INDEX_FILE"] = indexFile;
        }

        var request = new ProcessRequest
        {
            FileName = GitExecutable,
            Arguments = arguments,
            WorkingDirectory = directory,
            OnOutputLine = echoOutput ? _output.Raw : null,
            OnErrorLine = echoOutput ? _output.RawError : null,
            Environment = environment,
            StandardInput = standardInput,
            StandardOutputEncoding = outputEncoding,
        };

        var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);

        return new GitCommandResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.TimedOut);
    }

    /// <summary>Parses <c>git worktree list --porcelain</c>, whose first entry is the main worktree.</summary>
    private static List<GitWorktree> ParseWorktrees(string output)
    {
        var worktrees = new List<GitWorktree>();
        string? path = null;
        string? commit = null;
        string? branch = null;
        var bare = false;
        string? lockReason = null;

        void Flush()
        {
            if (path is null)
            {
                return;
            }

            worktrees.Add(new GitWorktree(
                NormalizeDirectory(path),
                commit,
                branch,
                IsMain: worktrees.Count == 0,
                IsBare: bare)
            {
                LockReason = lockReason,
            });

            path = null;
            commit = null;
            branch = null;
            bare = false;
            lockReason = null;
        }

        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.Length == 0)
            {
                Flush();
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                // An unborn HEAD, as on an orphan branch, is listed as the null object id. It names no
                // commit, and handed to git as one it fails every command it reaches.
                var head = line["HEAD ".Length..];
                commit = head.All(character => character == '0') ? null : head;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                // git reports a full ref; callers want the branch name they would type.
                branch = StripRefPrefix(line["branch ".Length..]);
            }
            else if (string.Equals(line, "bare", StringComparison.Ordinal))
            {
                bare = true;
            }
            else if (string.Equals(line, "locked", StringComparison.Ordinal))
            {
                lockReason = string.Empty;
            }
            else if (line.StartsWith("locked ", StringComparison.Ordinal))
            {
                lockReason = line["locked ".Length..];
            }
        }

        Flush();
        return worktrees;
    }

    /// <summary>
    /// Reads <c>git status --porcelain -z</c>: one entry per changed path, each two status
    /// characters and a space, then the path.
    /// </summary>
    private static List<string> ParseStatus(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<string>();

        for (var index = 0; index < fields.Length; index++)
        {
            var entry = fields[index];
            entries.Add(entry);

            // A rename or copy is one change encoded as two NUL separated fields: the
            // status with the new path, then the original path. Treating the second
            // field as another entry would report one rename as two changes, and the
            // second would have no status prefix at all. Either status column can mark
            // one: a staged rename is marked in the first, and a rename git finds in the
            // work tree, such as that of an intent-to-add file, in the second.
            if (entry is ['R' or 'C', ..] or [_, 'R' or 'C', ..])
            {
                index++;
            }
        }

        return entries;
    }

    /// <summary>
    /// Options that aim git at a git directory alone. The work tree is pointed at the git directory
    /// as well, because a submodule's repository names its checkout in core.worktree, and git stops
    /// when that checkout is gone, even for a question that never reads it.
    /// </summary>
    private static string[] RepositoryOnly(string gitDirectory)
        => [$"--git-dir={gitDirectory}", $"--work-tree={gitDirectory}"];

    /// <summary>Reads the count <c>git rev-list --count</c> printed, or throws when git failed.</summary>
    private static int ReadCount(GitCommandResult result)
    {
        Ensure(result, "count commits");

        var text = result.StandardOutput.Trim();

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git counted commits in a form this build cannot read: '{text}'");
    }

    /// <summary>
    /// Throws when git failed, carrying git's own message. Used where an empty
    /// result would be indistinguishable from a legitimate empty answer.
    /// </summary>
    private static void Ensure(GitCommandResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not {what}: {result.FailureMessage}");
        }
    }

    /// <summary>
    /// Distinguishes git's "not a repository" answer from a genuine failure to run.
    /// git reports the former on stderr in a recognisable form; everything else
    /// (dubious ownership, a corrupt config, an unreadable object store, a timeout) is a
    /// failure whose own message tells the user what to do.
    /// </summary>
    private static void EnsureNotAnError(GitCommandResult result, string directory)
    {
        if (!result.TimedOut
            && result.StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"git could not inspect '{directory}': {result.FailureMessage}");
    }

    /// <summary>Reduces a full ref to the branch name a person would type.</summary>
    private static string StripRefPrefix(string reference)
        => reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;

    /// <summary>
    /// Renders a directory path in the platform's own separator form with no trailing
    /// separator, so paths from git (which always answers with forward slashes) compare
    /// equal to paths the harness built from them.
    /// </summary>
    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
