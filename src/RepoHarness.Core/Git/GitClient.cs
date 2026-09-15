using System.Globalization;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <inheritdoc cref="IGitClient"/>
public sealed class GitClient(IProcessRunner processRunner, IHarnessOutput output) : IGitClient
{
    private const string GitExecutable = "git";

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
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var path = relativePath.Replace('\\', '/');

        // Whether the file exists at the commit is asked separately, of a command whose exit code does
        // not depend on the answer. Reading the error text of a failed `git show` instead would mistake
        // a commit git cannot read for a file that was simply not there yet.
        var listing = await RunAsync(
            directory,
            ["ls-tree", "--name-only", "-z", commit, "--", path],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(listing, $"look for '{path}' at {commit}");

        if (listing.StandardOutput.Length == 0)
        {
            return null;
        }

        var content = await RunAsync(
            directory,
            ["show", $"{commit}:{path}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(content, $"read '{path}' at {commit}");
        return content.StandardOutput;
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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Without this, an operation that decides to ask for credentials blocks
            // forever with no output. A hang is the one failure this tool cannot
            // report, so git is told up front that nobody is watching.
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

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
