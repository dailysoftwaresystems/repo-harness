using System.Text.RegularExpressions;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Build;

/// <summary>What a build directory's dependency records say.</summary>
/// <param name="ObjectsRead">How many objects had a dependency record at all.</param>
/// <param name="WithoutHeaders">Objects that recorded no header dependencies, and are not excused.</param>
/// <param name="Excused">
/// Objects that recorded none legitimately, each with the source that explains it.
/// </param>
/// <param name="Skipped">Why the check did not run, or null when it did.</param>
public sealed record NinjaDependencyReport(
    int ObjectsRead,
    IReadOnlyList<string> WithoutHeaders,
    IReadOnlyDictionary<string, string> Excused,
    string? Skipped)
{
    /// <summary>Whether every object that should have recorded headers did.</summary>
    public bool IsClean => Skipped is not null || WithoutHeaders.Count == 0;
}

/// <summary>
/// Finds objects a build produced without recording which headers they depend on.
/// </summary>
/// <remarks>
/// An object with no recorded header dependencies is never rebuilt when a header it includes
/// changes, so the next build links yesterday's object and reports success. The check runs per leg,
/// on that leg's own build directory, because every leg has one: run only where the harness happens
/// to be, it leaves every other leg unchecked, and the leg most likely to be misconfigured is the
/// one nobody is sitting at.
/// </remarks>
public sealed partial class NinjaDependencyCheck(IProcessRunner processRunner, IFileSystem fileSystem)
{
    /// <summary>The file a ninja build directory describes itself in.</summary>
    public const string ManifestFileName = "build.ninja";

    /// <summary>
    /// What a Ninja generator starts to build, and the name the check looks ninja up by when the build
    /// recorded no program of its own.
    /// </summary>
    public const string Program = "ninja";

    /// <summary>
    /// How long <c>ninja -t deps</c> may take. A probe, not a phase: it reads a log and prints, so a
    /// budget here bounds a hang rather than guessing at a workload.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A header line of <c>ninja -t deps</c>: the object, then how many dependencies it recorded.
    /// Whether the record is valid or stale is deliberately not read — a valid record of zero
    /// dependencies is exactly the broken state this looks for.
    /// </summary>
    /// <remarks>
    /// Read up to the first <c>: #deps</c>, so an object whose path holds a space is still counted;
    /// a header starts its line, and the dependencies listed beneath it are indented, so none of them
    /// is ever read as an object.
    /// </remarks>
    [GeneratedRegex(@"^(?<object>\S.*?):\s+#deps\s+(?<count>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DepsHeader { get; }

    /// <summary>A preprocessor include, used to excuse a translation unit that has none.</summary>
    [GeneratedRegex(@"^\s*#\s*include\b", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Include { get; }

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Checks one build directory.</summary>
    /// <param name="buildDirectory">The directory to read.</param>
    /// <param name="appendToPath">The directories the build appended to its PATH, which ninja is looked up on too.</param>
    /// <param name="program">
    /// The ninja the build ran, as its configuration recorded it, or <see langword="null"/> to look one
    /// up. Read by the program that wrote them, the records are read the way they were written, and a
    /// ninja only the build's own environment could find is found all the same. A relative one is read
    /// from the build directory, where the check starts.
    /// </param>
    /// <param name="environment">
    /// The environment the build's phases ran in, which the check runs in too: a ninja looked up by
    /// name is found on the PATH the build had.
    /// </param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="HarnessException">
    /// The check could not run: the directory is missing, ninja could not be started, or it answered
    /// with nothing. An empty answer is a failure and never a pass — it is indistinguishable from
    /// "every object recorded its headers", and reading it as one is how a broken build directory
    /// stays green.
    /// </exception>
    public async Task<NinjaDependencyReport> CheckAsync(
        string buildDirectory,
        IReadOnlyList<string> appendToPath,
        string? program = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appendToPath);

        if (!_fileSystem.DirectoryExists(buildDirectory))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' does not exist, so its dependency records cannot be read.");
        }

        var manifest = Path.Combine(buildDirectory, ManifestFileName);

        if (!_fileSystem.FileExists(manifest))
        {
            // The one legitimate skip: a build system other than ninja records dependencies its own
            // way, and there is nothing here to read.
            return new NinjaDependencyReport(0, [], EmptyExcuses, $"'{buildDirectory}' is not a ninja build directory");
        }

        ProcessResult result;

        try
        {
            result = await _processRunner
                .RunAsync(
                    new ProcessRequest
                    {
                        FileName = string.IsNullOrWhiteSpace(program) ? Program : ProcessRunner.Anchored(program, buildDirectory),
                        Arguments = ["-C", buildDirectory, "-t", "deps"],

                        // The directories the build was given, for a ninja looked up by name: the one
                        // the survey found for the build, not "not installed".
                        AppendToPath = appendToPath,
                        Environment = environment ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                        WorkingDirectory = buildDirectory,
                        Timeout = Budget,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            // The check could not run, which says nothing about the build it was to read: reported
            // as the check that did not run, never as the build failing, and never as a pass.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' could not be started for '{buildDirectory}': {ex.Message}",
                ex);
        }

        if (result.TimedOut)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' did not answer within {Budget.TotalMinutes:0} minutes for '{buildDirectory}'.");
        }

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' failed for '{buildDirectory}' (exit {result.ExitCode}).");
        }

        var counts = ReadCounts(result.StandardOutput);

        if (counts.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' listed no objects for '{buildDirectory}'. An empty answer is "
                + "indistinguishable from every object having recorded its headers, so it is never read as one.");
        }

        var withoutHeaders = counts.Where(entry => entry.Value == 0).Select(entry => entry.Key).ToList();

        if (withoutHeaders.Count == 0)
        {
            return new NinjaDependencyReport(counts.Count, [], EmptyExcuses, null);
        }

        var (flagged, excused) = Excuse(buildDirectory, withoutHeaders);

        return new NinjaDependencyReport(counts.Count, flagged, excused, null);
    }

    /// <summary>
    /// Separates objects that legitimately recorded no headers from those that did not.
    /// </summary>
    /// <remarks>
    /// Only an object built under <c>deps = msvc</c> - its own build line's, or else its rule's -
    /// which parses <c>/showIncludes</c> and so records headers and never the source itself: a
    /// translation unit that includes nothing legitimately records zero. Under <c>deps = gcc</c> the
    /// source is always listed, so zero can never be legitimate, and an object built that way keeps
    /// the check's full strength whatever else the manifest builds. The manifest is read the way
    /// ninja reads it, across the files it includes - CMake keeps its rules in one of those - with
    /// ninja's escapes undone, so the source CMake names absolutely is a file that can be read. An
    /// object no build line produces, a source that is not there and one that cannot be read all
    /// count as having includes, so none of them is ever excused.
    /// </remarks>
    private (IReadOnlyList<string> Flagged, IReadOnlyDictionary<string, string> Excused) Excuse(
        string buildDirectory,
        List<string> withoutHeaders)
    {
        var manifest = NinjaManifest.Read(_fileSystem, buildDirectory);
        var flagged = new List<string>();
        var excused = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var obj in withoutHeaders)
        {
            if (manifest.EdgeFor(obj) is { Deps: "msvc", Source: { } source } && !HasInclude(Path.Combine(buildDirectory, source)))
            {
                excused[obj] = source;
            }
            else
            {
                flagged.Add(obj);
            }
        }

        return (flagged, excused);
    }

    private bool HasInclude(string path)
    {
        try
        {
            return !_fileSystem.FileExists(path) || Include.IsMatch(_fileSystem.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fails closed: an unreadable source is not an excused one.
            return true;
        }
    }

    private static Dictionary<string, int> ReadCounts(string output)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n'))
        {
            var match = DepsHeader.Match(line.TrimEnd('\r'));

            if (match.Success)
            {
                counts[match.Groups["object"].Value] = int.Parse(
                    match.Groups["count"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return counts;
    }

    private static readonly Dictionary<string, string> EmptyExcuses = new(StringComparer.Ordinal);
}
