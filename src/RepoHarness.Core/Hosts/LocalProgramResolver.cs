using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Where each of a set of programs is on this machine, and the directories found off the PATH.
/// </summary>
/// <param name="Found">Where each program is, by the name it was asked for.</param>
/// <param name="OffPathDirectories">
/// The directories a program was found in that the PATH does not name, in the order the search
/// prefers them. Appended to the PATH of every process a leg starts, so that a program which starts
/// another one by name — cmake starting ninja and the compilers — finds it where the search did.
/// </param>
public sealed record ProgramSearch(
    IReadOnlyDictionary<string, ProgramLocation> Found,
    IReadOnlyList<string> OffPathDirectories)
{
    /// <summary>A search that looked for nothing.</summary>
    public static ProgramSearch None { get; } = new(new Dictionary<string, ProgramLocation>(StringComparer.Ordinal), []);
}

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

        var searched = Expanded(directories);
        var found = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal);

        foreach (var program in programs.Where(program => !string.IsNullOrWhiteSpace(program)).Distinct(StringComparer.Ordinal))
        {
            found[program] = Find(program, searched);
        }

        // In the order the search prefers them rather than the order the programs happened to be
        // asked for, so the PATH a leg is given ranks two directories the way the search did.
        var offPath = found.Values
            .Where(location => location.Found == ProgramFound.OffPath && location.Path is not null)
            .Select(location => Path.GetDirectoryName(location.Path!)!)
            .Distinct(PathComparer)
            .OrderBy(directory => searched.FindIndex(candidate => PathComparer.Equals(candidate, directory)))
            .ToList();

        return new ProgramSearch(found, offPath);
    }

    /// <summary>Finds one program.</summary>
    /// <param name="program">The name, or path, to find.</param>
    /// <param name="directories">Where to look when the PATH does not name it.</param>
    public ProgramLocation Find(string program, IReadOnlyList<string> directories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(directories);

        return Find(program, Expanded(directories));
    }

    private ProgramLocation Find(string program, List<string> directories)
    {
        var windows = _platform.Current == PlatformId.Windows;

        // A path is where it is, and is looked for nowhere else: a toolchain naming its compiler by
        // path means that compiler and no other, and PATH has no say in it.
        if (program.Contains('/') || program.Contains('\\'))
        {
            string full;

            try
            {
                // With the one extension Windows adds to a path, as the process runner does, so a path
                // found here is the file that would start.
                full = Path.GetFullPath(windows ? ProcessRunner.WithWindowsExtension(program) : program);
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
        foreach (var directory in directories)
        {
            if (ProcessRunner.ProgramInDirectory(program, directory, windows, _filePermissions.IsExecutable) is { } offPath)
            {
                return new ProgramLocation(program, ProgramFound.OffPath, offPath);
            }
        }

        return new ProgramLocation(program, ProgramFound.Nowhere);
    }

    /// <summary>The directories with <c>~</c> made this machine's home, and any this machine cannot express dropped.</summary>
    private List<string> Expanded(IReadOnlyList<string> directories)
    {
        var home = _platform.HomeDirectory;
        var expanded = new List<string>();

        foreach (var directory in directories)
        {
            var spelled = directory.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Join(home, directory[2..])
                : directory;

            try
            {
                expanded.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(spelled)));
            }
            catch (ArgumentException)
            {
                // A directory this machine cannot spell, such as a Windows drive on Linux, holds
                // nothing here to find.
            }
        }

        return expanded;
    }

    private StringComparer PathComparer
        => _platform.PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
