using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Where each of a set of programs is on this machine, and the directories they were found in.
/// </summary>
/// <param name="Found">Where each program is, by the name it was asked for.</param>
/// <param name="Directories">
/// Every directory a program asked for by name was found in, on the PATH or off it, in the order the
/// search looked in them. Appended to the PATH of every process a leg starts, so a program that
/// starts another by name - cmake starting ninja and the compilers - finds it where the search did,
/// and so does the run itself where a phase's environment sets a PATH of its own.
/// </param>
public sealed record ProgramSearch(
    IReadOnlyDictionary<string, ProgramLocation> Found,
    IReadOnlyList<string> Directories);

/// <summary>
/// Finds programs on this machine the way a leg on it will start them: on the PATH first, then in
/// each searched directory.
/// </summary>
/// <remarks>
/// Run by the process that will run the leg — this machine for a leg placed here, and the DssHarness
/// on a host for a leg placed there, which answers a survey through this and then runs the leg with
/// what it found. A survey and a run that each looked in their own places is how a leg was reported
/// runnable and then could not find its build tool; asked of one function on one machine, they
/// cannot disagree.
/// </remarks>
/// <param name="platform">This machine.</param>
/// <param name="filePermissions">Says whether a candidate file would start.</param>
/// <param name="path">
/// Reads the PATH a child of this process is given; this process's own when left out. A test hands
/// one in rather than changing the process's own, which every other test running beside it shares.
/// </param>
public sealed class LocalProgramResolver(IHostPlatform platform, IFilePermissions filePermissions, Func<string?>? path = null)
{
    private readonly IHostPlatform _platform = platform;
    private readonly IFilePermissions _filePermissions = filePermissions;
    private readonly Func<string?> _path = path ?? (() => Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Finds each of <paramref name="programs"/>.</summary>
    /// <param name="programs">The names, or paths, to find.</param>
    /// <param name="directories">Where to look when the PATH does not name one; <c>~</c> is this machine's home.</param>
    public ProgramSearch Resolve(IEnumerable<string> programs, IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(directories);

        var search = Plan(directories);
        var found = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal);

        foreach (var program in programs.Where(program => !string.IsNullOrWhiteSpace(program)).Distinct(StringComparer.Ordinal))
        {
            found[program] = Find(program, search);
        }

        // In the order the search looked in them rather than the order the programs happened to be
        // asked for, so the PATH a leg is given ranks two directories the way the search did. A program
        // named by its path is started by that path, and adds nothing to anybody's PATH.
        var order = PathDirectories().Concat(search.Directories).ToList();

        var directoriesFound = found.Values
            .Where(location => location.Present && location.Path is not null && !ProcessRunner.IsPath(location.Program))
            .Select(location => Path.GetDirectoryName(location.Path!)!)
            .Distinct(PathComparer)
            .OrderBy(directory => order.FindIndex(candidate => PathComparer.Equals(candidate, directory)) is var at and >= 0 ? at : int.MaxValue)
            .ToList();

        return new ProgramSearch(found, directoriesFound);
    }

    /// <summary>Finds one program.</summary>
    /// <param name="program">The name, or path, to find.</param>
    /// <param name="directories">Where to look when the PATH does not name it.</param>
    public ProgramLocation Find(string program, IReadOnlyList<string> directories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(directories);

        return Find(program, Plan(directories));
    }

    private ProgramLocation Find(string program, SearchPlan search)
    {
        var windows = _platform.Current == PlatformId.Windows;

        // A path is where it is, and is looked for nowhere else: a toolchain naming its compiler by
        // path means that compiler and no other, and PATH has no say in it.
        if (ProcessRunner.IsPath(program))
        {
            string full;

            try
            {
                full = ProcessRunner.ProgramAtPath(program, windows);
            }
            catch (ArgumentException)
            {
                return new ProgramLocation(program, ProgramFound.Nowhere);
            }

            return _filePermissions.IsExecutable(full)
                ? new ProgramLocation(program, ProgramFound.OnPath, full)
                : new ProgramLocation(program, ProgramFound.Nowhere);
        }

        if (ProcessRunner.ProgramOnPath(program, _path(), windows, _filePermissions.IsExecutable) is { } onPath)
        {
            return new ProgramLocation(program, ProgramFound.OnPath, onPath);
        }

        // One directory at a time rather than joined into a PATH of its own: a directory holding the
        // separator character would otherwise be split into two that do not exist.
        foreach (var directory in search.Directories)
        {
            if (ProcessRunner.ProgramInDirectory(program, directory, windows, _filePermissions.IsExecutable) is { } offPath)
            {
                return new ProgramLocation(program, ProgramFound.OffPath, offPath);
            }
        }

        // Not found, and not looked for everywhere it was meant to be: that is not "missing", and a
        // refusal saying it was would send somebody to install a program that may well be there.
        return search.Unsearched.Count == 0
            ? new ProgramLocation(program, ProgramFound.Nowhere)
            : new ProgramLocation(
                program,
                ProgramFound.Unreadable,
                Reason: $"the home directory is not known here, so {string.Join(", ", search.Unsearched.Select(entry => $"'{entry}'"))} "
                    + "could not be searched");
    }

    /// <summary>
    /// The directories to look in, made full, and the ones that should have been and cannot be.
    /// </summary>
    /// <remarks>
    /// The entries arrive as the ones this platform names (see <see cref="ToolSearchDirectories.For"/>),
    /// and this machine is asked about each all the same, by its own rule for a whole path: an entry
    /// it cannot name as one would be read against wherever the search happened to start, where a
    /// program somebody left there would be found in place of the real one. A <c>~/</c> entry with no
    /// home to expand it against is different: it names a directory this machine has, which could
    /// not be looked in.
    /// </remarks>
    private SearchPlan Plan(IReadOnlyList<string> directories)
    {
        var home = _platform.HomeDirectory;
        var searched = new List<string>();
        var unsearched = new List<string>();

        foreach (var directory in directories)
        {
            if (!PlatformPaths.IsHomeRelative(directory))
            {
                if (Path.IsPathFullyQualified(directory))
                {
                    searched.AddRange(Full(directory));
                }
            }
            else if (!string.IsNullOrEmpty(home) && Path.IsPathRooted(home))
            {
                searched.AddRange(Full(Path.Join(home, directory[2..])));
            }
            else
            {
                unsearched.Add(directory);
            }
        }

        return new SearchPlan(searched, unsearched);
    }

    /// <summary>The directories the PATH names, spelled the way a found program's directory is.</summary>
    private IEnumerable<string> PathDirectories()
        => (_path() ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => Full(directory.Trim('"')));

    /// <summary>
    /// <paramref name="directory"/> made full, or nothing when it holds a character no path can: such
    /// an entry holds nothing to find, here or anywhere.
    /// </summary>
    private static IEnumerable<string> Full(string directory)
    {
        string full;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (ArgumentException)
        {
            yield break;
        }

        yield return full;
    }

    private StringComparer PathComparer
        => _platform.PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Where a search looks, and the directories it was told to look in and cannot.</summary>
    /// <param name="Directories">The directories to look in, full, in the order given.</param>
    /// <param name="Unsearched">The entries that name a directory here that could not be expanded.</param>
    private sealed record SearchPlan(List<string> Directories, IReadOnlyList<string> Unsearched);
}
