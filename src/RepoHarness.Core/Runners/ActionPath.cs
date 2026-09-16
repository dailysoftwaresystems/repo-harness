using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>
/// Where a runner's action file lives, and what a runner's <c>action</c> key may name.
/// </summary>
/// <remarks>
/// <para>
/// One directory per action: <c>actions/&lt;name&gt;/&lt;name&gt;.yml</c>. A <c>run</c> line is a
/// program and its arguments with no shell, so anything that is not a one-liner — a program, a
/// fixture, a data table — has to live in a file. A flat directory gives that file nowhere to live
/// that is obviously owned by the action it belongs to, and two actions' supporting files would sit
/// side by side with nothing saying which belonged to which.
/// </para>
/// <para>
/// The rules are split in two on purpose. <see cref="Problem"/> reads a spelling and nothing else,
/// so the configuration reader can apply it with no file system at hand and <c>legs</c> and
/// <c>run</c> refuse the same file for the same reason. <see cref="Resolve"/> adds what only the
/// file system knows: that the path stays inside the actions directory once every link along it is
/// followed, and that the file is there.
/// </para>
/// </remarks>
public static class ActionPath
{
    /// <summary>The extension an action file carries, and the one a refusal suggests.</summary>
    public const string Extension = ".yml";

    /// <summary>The other spelling of <see cref="Extension"/>, equally valid.</summary>
    public const string AlternateExtension = ".yaml";

    /// <summary>Both spellings, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [Extension, AlternateExtension];

    /// <summary>
    /// What is wrong with <paramref name="action"/> as a runner's <c>action</c> value, or
    /// <see langword="null"/> when nothing is.
    /// </summary>
    /// <remarks>
    /// Spelling only: this decides nothing that needs a file system, so the configuration reader
    /// applies it when the file is read rather than when a runner is finally invoked. Every refusal
    /// names the path the runner should have had, because a rule stated without its remedy leaves
    /// the reader guessing at a layout the message already knows.
    /// </remarks>
    /// <param name="action">The value of a runner's <c>action</c> key.</param>
    public static string? Problem(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return $"is empty; it names an action file, as '{Expected("<name>")}'";
        }

        // Rooted first, and by both tests: Path.IsPathRooted answers for this platform only, so a
        // Windows drive letter written on Linux would otherwise read as an ordinary directory name
        // and a configuration would mean two different things on two machines.
        if (Path.IsPathRooted(action) || action[0] is '/' or '\\' || (action.Length >= 2 && action[1] == ':'))
        {
            return $"'{action}' is an absolute path; it names a file inside the actions "
                + $"directory, as '{Expected("<name>")}'";
        }

        var segments = action.Split('/', '\\');

        if (segments.Any(segment => segment is ".." or "." or ""))
        {
            return $"'{action}' does not name a file inside the actions directory; it is "
                + $"'{Expected("<name>")}', without '.' or '..'";
        }

        if (segments.Length != 2)
        {
            // One segment is the flat spelling this layout replaced; three or more is a directory
            // deeper than the one directory an action owns. Both get the same remedy, built from
            // whatever the author was trying to name.
            var name = NameFrom(segments);

            return $"'{action}' is not an action file; each action owns one directory, so its file "
                + $"is '{Expected(name)}'";
        }

        var directory = segments[0];
        var file = segments[1];

        if (!Extensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{action}' does not end in '{string.Join("' or '", Extensions)}'; it is "
                + $"'{Expected(directory)}'";
        }

        var stem = Path.GetFileNameWithoutExtension(file);

        return string.Equals(stem, directory, StringComparison.Ordinal)
            ? null
            : $"'{action}' names directory '{directory}' but file '{file}'; an action's file carries "
                + $"its directory's name, so it is '{Expected(directory)}' or '{Expected(stem)}'";
    }

    /// <summary>
    /// How an action named <paramref name="name"/> is spelled, as a refusal shows it.
    /// </summary>
    /// <param name="name">The action's directory name.</param>
    public static string Expected(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return $"{name}/{name}{Extension}";
    }

    /// <summary>
    /// The full path of the action file <paramref name="action"/> names, once its spelling is
    /// accepted, every link along it is followed, and it is confirmed to be inside
    /// <paramref name="actionsDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Containment is checked after the links are followed, not before. Resolving a name against the
    /// actions directory and running whatever is there would let a runner read, and then run, a file
    /// from anywhere on the machine; only files a reviewer sees in this directory may declare what a
    /// runner does, and a link is exactly how a file elsewhere comes to appear here. A subdirectory
    /// of the actions directory is as reviewable as the directory itself, which is why the shape
    /// this layout requires is allowed through while everything leaving the directory is not.
    /// </remarks>
    /// <param name="actionsDirectory">The directory action directories live in.</param>
    /// <param name="action">The value of a runner's <c>action</c> key.</param>
    /// <param name="fileSystem">Follows the links along the path.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <exception cref="HarnessException">
    /// The spelling is wrong, the path leaves the actions directory, or the file is absent.
    /// </exception>
    public static string Resolve(
        string actionsDirectory,
        string action,
        IFileSystem fileSystem,
        StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionsDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (Problem(action) is { } problem)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"A runner's action {problem}.");
        }

        string root;
        string path;

        try
        {
            // The actions directory is resolved the same way as the file, so a repository reached
            // through a link of its own — a checkout under a symlinked home directory, a macOS
            // /tmp — compares against the same real path rather than failing every action it has.
            root = Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(Path.GetFullPath(actionsDirectory)));
            path = fileSystem.ResolveLinks(Path.GetFullPath(Path.Combine(actionsDirectory, action)));
        }
        catch (Exception exception)
            when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A path this platform cannot express, links that cannot be read or form a cycle, or a
            // directory along the way this user may not look inside. Either way nothing can say what
            // file the runner meant, and none of them is a defect in the tool: left to escape, the
            // last would be reported as one, and would abandon the problems already collected for
            // every runner checked before it.
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"A runner's action '{action}' could not be resolved inside '{actionsDirectory}': "
                + $"{exception.Message}");
        }

        if (!PathContainment.IsStrictlyInside(root, path, comparison))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"A runner's action '{action}' leads to '{path}', which is outside "
                + $"'{root}'. Only a file inside the actions directory may declare what a runner "
                + "does, so that what a reviewer reads there is what runs.");
        }

        if (!fileSystem.FileExists(path))
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"'{path}' does not exist.");
        }

        return path;
    }

    /// <summary>
    /// Refuses unless every action a runner names resolves to a file inside the actions directory.
    /// </summary>
    /// <remarks>
    /// Run before a leg is placed, so that <c>legs</c> and <c>run</c> answer the same way about the
    /// same repository. The spelling rules are already applied when <c>config.json</c> is read; what
    /// is added here is what only the file system knows — that the file is there, and that no link
    /// along the way leads out of the directory a reviewer reads. Every runner is checked and every
    /// problem reported together, as the configuration reader does, because a repository whose
    /// actions were moved has usually moved all of them.
    /// </remarks>
    /// <param name="runners">The runners to check, by name.</param>
    /// <param name="actionsDirectory">The directory action directories live in.</param>
    /// <param name="fileSystem">Follows the links along each path.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <exception cref="HarnessException">An action does not resolve. Nothing has run.</exception>
    public static void RequireResolvable(
        IEnumerable<KeyValuePair<string, string>> runners,
        string actionsDirectory,
        IFileSystem fileSystem,
        StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(runners);

        var problems = new List<string>();

        foreach (var (name, action) in runners)
        {
            try
            {
                Resolve(actionsDirectory, action, fileSystem, comparison);
            }
            catch (HarnessException exception)
            {
                problems.Add($"predefined runner '{name}': {exception.Message}");
            }
        }

        if (problems.Count == 0)
        {
            return;
        }

        var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

        throw new HarnessException(
            HarnessExit.ConfigInvalid,
            $"{problems.Count} runner action(s) could not be read:{Environment.NewLine}{detail}");
    }

    /// <summary>
    /// The action name to suggest for a value that is not two segments, taken from what the author
    /// wrote so the remedy names their action rather than a placeholder.
    /// </summary>
    /// <param name="segments">The value's path segments.</param>
    private static string NameFrom(IReadOnlyList<string> segments)
    {
        // The last segment is the file they meant, whether they wrote one segment or five. Its stem
        // is the directory an action of that name owns.
        var stem = Path.GetFileNameWithoutExtension(segments[^1]);

        return string.IsNullOrWhiteSpace(stem) ? "<name>" : stem;
    }
}
