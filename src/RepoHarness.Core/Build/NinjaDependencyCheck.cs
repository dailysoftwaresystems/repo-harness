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

    /// <summary>The program the check starts, which is also what a Ninja generator starts to build.</summary>
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
    [GeneratedRegex(@"^(?<object>\S+):\s+#deps\s+(?<count>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DepsHeader { get; }

    /// <summary>An edge in <c>build.ninja</c>: the object it produces, and the first input it takes.</summary>
    [GeneratedRegex(@"^build\s+(?<object>\S+):\s+(?<rule>\S+)\s+(?<source>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex BuildEdge { get; }

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
    /// ninja only the build's own environment could find is found all the same.
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

        var result = await _processRunner
            .RunAsync(
                new ProcessRequest
                {
                    FileName = string.IsNullOrWhiteSpace(program) ? Program : program,
                    Arguments = ["-C", buildDirectory, "-t", "deps"],

                    // The directories the build was given, for a ninja looked up by name: the one the
                    // survey found for the build, not "not installed".
                    AppendToPath = appendToPath,
                    WorkingDirectory = buildDirectory,
                    Timeout = Budget,
                },
                cancellationToken)
            .ConfigureAwait(false);

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

        var manifestText = _fileSystem.ReadAllText(manifest);
        var withoutHeaders = counts.Where(entry => entry.Value == 0).Select(entry => entry.Key).ToList();

        if (withoutHeaders.Count == 0)
        {
            return new NinjaDependencyReport(counts.Count, [], EmptyExcuses, null);
        }

        var (flagged, excused) = Excuse(buildDirectory, manifestText, withoutHeaders);

        return new NinjaDependencyReport(counts.Count, flagged, excused, null);
    }

    /// <summary>
    /// Separates objects that legitimately recorded no headers from those that did not.
    /// </summary>
    /// <remarks>
    /// Only under <c>deps = msvc</c>, which parses <c>/showIncludes</c> and so reports headers and
    /// never the source itself: a translation unit that includes nothing legitimately records zero.
    /// Under <c>deps = gcc</c> the source is always listed, so zero can never be legitimate, and a
    /// manifest holding both keeps its full strength rather than borrowing the weaker rule. A source
    /// that cannot be read counts as having includes, so an unreadable file is never excused.
    /// </remarks>
    private (IReadOnlyList<string> Flagged, IReadOnlyDictionary<string, string> Excused) Excuse(
        string buildDirectory,
        string manifestText,
        List<string> withoutHeaders)
    {
        var msvcOnly = manifestText.Contains("deps = msvc", StringComparison.Ordinal)
            && !manifestText.Contains("deps = gcc", StringComparison.Ordinal);

        if (!msvcOnly)
        {
            return (withoutHeaders, EmptyExcuses);
        }

        var sources = ReadSources(manifestText);
        var flagged = new List<string>();
        var excused = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var obj in withoutHeaders)
        {
            if (!sources.TryGetValue(Normalize(obj), out var source))
            {
                flagged.Add(obj);
                continue;
            }

            var path = Path.Combine(buildDirectory, source.Replace('/', Path.DirectorySeparatorChar));

            if (HasInclude(path))
            {
                flagged.Add(obj);
            }
            else
            {
                excused[obj] = source;
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
            var match = DepsHeader.Match(line.Trim());

            if (match.Success)
            {
                counts[match.Groups["object"].Value] = int.Parse(
                    match.Groups["count"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return counts;
    }

    private static Dictionary<string, string> ReadSources(string manifestText)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in manifestText.Split('\n'))
        {
            var match = BuildEdge.Match(line);

            if (match.Success)
            {
                sources[Normalize(match.Groups["object"].Value)] = match.Groups["source"].Value;
            }
        }

        return sources;
    }

    /// <summary>
    /// Puts an object path in one spelling. Measured: <c>ninja -t deps</c> prints forward separators
    /// on Windows while <c>build.ninja</c> holds backslashes, so keyed raw every lookup missed and
    /// the excusal silently never fired.
    /// </summary>
    private static string Normalize(string path) => path.Replace('\\', '/');

    private static readonly Dictionary<string, string> EmptyExcuses = new(StringComparer.Ordinal);
}
