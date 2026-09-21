using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Where a process started with an environment looks for a program named by its name: the PATH that
/// environment sets, or this process's own where it sets none, then the directories appended to it.
/// </summary>
/// <param name="Path">
/// The PATH the process is given: the one its environment sets, and this process's where it sets none.
/// </param>
/// <param name="ProgramDirectories">The directories appended to it, where a survey found programs off it.</param>
/// <remarks>
/// One answer for everything that asks which file a name starts - the guard on a configured build
/// directory, a leg checking what its developer environment put on its PATH, and the look that
/// decides whether a tool is missing - so none of them can look somewhere a leg would not.
/// </remarks>
public sealed record PathSearch(string? Path, IReadOnlyList<string> ProgramDirectories)
{
    /// <summary>Where a process started with <paramref name="environment"/> looks.</summary>
    /// <param name="environment">The environment it starts with, over this process's own.</param>
    /// <param name="programDirectories">What its PATH is given after its own.</param>
    public static PathSearch For(IReadOnlyDictionary<string, string?> environment, IReadOnlyList<string> programDirectories)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(programDirectories);

        var declared = environment.FirstOrDefault(pair => string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase));

        return new PathSearch(
            declared.Key is null ? Environment.GetEnvironmentVariable("PATH") : declared.Value,
            programDirectories);
    }

    /// <summary>Where <paramref name="program"/> is, looked for as such a process starts it.</summary>
    /// <param name="platform">This machine.</param>
    /// <param name="filePermissions">Says whether a candidate file would start.</param>
    /// <param name="program">The name, or path, to find.</param>
    public ProgramLocation Find(IHostPlatform platform, IFilePermissions filePermissions, string program)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(filePermissions);

        return new LocalProgramResolver(platform, filePermissions, () => Path).Find(program, ProgramDirectories);
    }
}
