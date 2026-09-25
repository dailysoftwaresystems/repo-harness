using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Build;

/// <summary>What ninja says of a build directory's outputs that no target of the current build produces any more.</summary>
/// <param name="Paths">Each such output that is there, as ninja names it: below the build directory, or absolute.</param>
/// <param name="Answered">
/// Whether ninja said: <see langword="false"/> for a build directory that is not ninja's, or a ninja that could not
/// say, whose silence says nothing about what is dead.
/// </param>
public sealed record NinjaDeadOutputs(IReadOnlyList<string> Paths, bool Answered)
{
    /// <summary>Nothing said: not a ninja build directory, or a ninja that could not answer.</summary>
    public static NinjaDeadOutputs Unknown { get; } = new([], false);
}

/// <summary>
/// Asks ninja which outputs of earlier builds its current manifest no longer produces - those of a target
/// renamed or removed - and removes nothing.
/// </summary>
/// <remarks>
/// A directory kept between builds holds the objects of targets since renamed away, and a consumer's incremental
/// builds warned every time that one of those was deeper than the path budget allowed, though a new worktree's
/// build, starting from clean, never holds it. ninja knows them: <c>ninja -t cleandead</c> removes every output its
/// build log records that the manifest no longer produces, and asked with <c>-n</c> it names them instead. Its
/// answer rather than a guess from the manifest: an object the dependency log still remembers answers
/// <c>ninja -t query</c> as known, and what CMake's configure writes is no output of the manifest at all. Run as the
/// dependency check runs ninja, with the ninja the build ran; and never a failure of the build, unlike that check -
/// a ninja too old to know the tool, or one that cannot answer, leaves the path budget measured as it always was.
/// </remarks>
/// <param name="processRunner">Runs ninja.</param>
/// <param name="fileSystem">Tells a ninja build directory.</param>
public sealed class NinjaDeadOutputCheck(IProcessRunner processRunner, IFileSystem fileSystem)
{
    /// <summary>What the dry run prints before each output it would remove.</summary>
    private const string RemovePrefix = "Remove ";

    /// <summary>How long the dry run may take: it reads two logs and prints, so this bounds a hang.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Asks ninja about one build directory.</summary>
    /// <param name="buildDirectory">The directory to ask about.</param>
    /// <param name="appendToPath">The directories the build appended to its PATH, which ninja is looked up on too.</param>
    /// <param name="program">The ninja the build ran, as its configuration recorded it, or <see langword="null"/> to look one up.</param>
    /// <param name="environment">The environment the build's phases ran in.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    public async Task<NinjaDeadOutputs> CheckAsync(
        string buildDirectory,
        IReadOnlyList<string> appendToPath,
        string? program = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        ArgumentNullException.ThrowIfNull(appendToPath);

        if (!_fileSystem.FileExists(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName)))
        {
            return NinjaDeadOutputs.Unknown;
        }

        ProcessResult result;

        try
        {
            result = await _processRunner
                .RunAsync(
                    new ProcessRequest
                    {
                        FileName = string.IsNullOrWhiteSpace(program) ? NinjaDependencyCheck.Program : ProcessRunner.Anchored(program, buildDirectory),

                        // -n: named, never removed. The harness removes nothing of a build directory it keeps.
                        Arguments = ["-C", buildDirectory, "-n", "-t", "cleandead"],
                        AppendToPath = appendToPath,
                        Environment = environment ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                        WorkingDirectory = buildDirectory,
                        Timeout = Budget,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProgramStartException)
        {
            return NinjaDeadOutputs.Unknown;
        }

        // A ninja before 1.10 knows no such tool, and exits saying so; any other failure is as silent. An answer
        // naming nothing is an answer: nothing is dead.
        if (!result.Succeeded)
        {
            return NinjaDeadOutputs.Unknown;
        }

        return new NinjaDeadOutputs(
            [.. result.StandardOutput
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith(RemovePrefix, StringComparison.Ordinal) && line.Length > RemovePrefix.Length)
                .Select(line => line[RemovePrefix.Length..])],
            Answered: true);
    }
}
