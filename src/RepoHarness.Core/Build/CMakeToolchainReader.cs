using System.Text.Json;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Build;

/// <summary>One compiler CMake configured a build with, for one language.</summary>
/// <param name="Language">The language as CMake names it: <c>C</c>, <c>CXX</c>, or another it reports.</param>
/// <param name="Id">CMake's own id for the compiler, such as <c>GNU</c>, <c>Clang</c> or <c>MSVC</c>.</param>
/// <param name="Version">The compiler's version as CMake reports it; empty where it reported none.</param>
public sealed record CompilerFact(string Language, string Id, string Version);

/// <summary>What CMake said about the compilers it configured a build with.</summary>
/// <param name="Compilers">One per language it identified a compiler for.</param>
/// <param name="Unread">
/// Why it said nothing, where it said nothing: no answer, or one this build cannot read.
/// </param>
public sealed record CompilerReading(IReadOnlyList<CompilerFact> Compilers, string? Unread)
{
    /// <summary>A reading that found nothing, and why.</summary>
    /// <param name="why">Why, to end a sentence.</param>
    public static CompilerReading None(string why) => new([], why);
}

/// <summary>The compilers a leg built with, as its line in a ledger names them.</summary>
public static class CompilerFacts
{
    /// <summary>
    /// <paramref name="compilers"/> as one mark - <c>compiler: MSVC 19.51.36231 (C, CXX)</c> - or
    /// <see langword="null"/> where there are none.
    /// </summary>
    /// <param name="compilers">What CMake reported.</param>
    /// <remarks>
    /// One entry per compiler rather than per language, in the order CMake reported the first
    /// language each serves: C and C++ from one MSVC are one compiler, and naming it twice would
    /// suggest two.
    /// </remarks>
    public static string? Describe(IReadOnlyList<CompilerFact> compilers)
    {
        ArgumentNullException.ThrowIfNull(compilers);

        if (compilers.Count == 0)
        {
            return null;
        }

        var named = compilers
            .GroupBy(compiler => (compiler.Id, compiler.Version))
            .Select(group => $"{group.Key.Id}{(group.Key.Version.Length > 0 ? " " + group.Key.Version : string.Empty)} "
                + $"({string.Join(", ", group.Select(compiler => compiler.Language))})");

        return "compiler: " + string.Join(", ", named);
    }
}

/// <summary>
/// Asks CMake which compilers it configured a build directory with, through its file API, and reads
/// the answer back.
/// </summary>
/// <remarks>
/// The compilers CMake resolved, not the ones a toolchain names: resolved by CMake, a name can reach
/// another compiler than the one it names - how a leg named msvc built with MinGW's gcc on every run.
/// The query is a file the build directory keeps, so every configure answers it, with the
/// <c>toolchains-v1</c> object that CMake 3.20 and later write.
/// </remarks>
public sealed class CMakeToolchainReader(IFileSystem fileSystem)
{
    /// <summary>The query, relative to a build directory.</summary>
    public const string QueryRelativePath = ".cmake/api/v1/query/toolchains-v1";

    /// <summary>Where CMake writes its answers, relative to a build directory.</summary>
    public const string ReplyRelativeDirectory = ".cmake/api/v1/reply";

    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Asks the next configure of <paramref name="buildDirectory"/> which compilers it resolves.</summary>
    /// <param name="buildDirectory">The build directory, which need not exist yet.</param>
    public void Ask(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        var query = Relative(buildDirectory, QueryRelativePath);

        if (_fileSystem.FileExists(query))
        {
            return;
        }

        _fileSystem.CreateDirectory(Path.GetDirectoryName(query)!);
        _fileSystem.WriteAllTextAtomic(query, string.Empty);
    }

    /// <summary>
    /// The compilers the last configure of <paramref name="buildDirectory"/> resolved, one per
    /// language it identified one for, or why it said nothing.
    /// </summary>
    /// <param name="buildDirectory">The build directory.</param>
    /// <remarks>
    /// Read from the newest index, whose name sorts last - the file API's own rule, since an index is
    /// named for the moment it was written. A language CMake could not identify a compiler for, such as
    /// the resource compiler it lists on Windows, is not one this build can be held to, and is left
    /// out rather than read as a mismatch.
    /// </remarks>
    public CompilerReading Read(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        var replies = Relative(buildDirectory, ReplyRelativeDirectory);

        if (!_fileSystem.DirectoryExists(replies))
        {
            return CompilerReading.None("CMake wrote no file API answer there, which it does from version 3.20");
        }

        var index = _fileSystem.EnumerateFiles(replies, recursive: false)
            .Where(file => Path.GetFileName(file).StartsWith("index-", StringComparison.Ordinal)
                && Path.GetExtension(file) == ".json")
            .Order(StringComparer.Ordinal)
            .LastOrDefault();

        if (index is null)
        {
            return CompilerReading.None("CMake wrote no file API index there");
        }

        try
        {
            using var indexDocument = JsonDocument.Parse(_fileSystem.ReadAllText(index));

            if (!indexDocument.RootElement.TryGetProperty("reply", out var reply)
                || !reply.TryGetProperty("toolchains-v1", out var answer))
            {
                return CompilerReading.None("CMake's file API index holds no answer about the compilers");
            }

            if (answer.TryGetProperty("error", out var error))
            {
                return CompilerReading.None($"CMake answered that it could not say: {error.GetString()}");
            }

            if (!answer.TryGetProperty("jsonFile", out var jsonFile) || jsonFile.GetString() is not { Length: > 0 } name)
            {
                return CompilerReading.None("CMake's answer about the compilers names no file");
            }

            var file = Path.Combine(replies, name);

            if (!_fileSystem.FileExists(file))
            {
                return CompilerReading.None($"CMake's answer about the compilers names '{name}', which is not there");
            }

            using var toolchains = JsonDocument.Parse(_fileSystem.ReadAllText(file));

            if (!toolchains.RootElement.TryGetProperty("toolchains", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return CompilerReading.None($"'{name}' lists no toolchains");
            }

            var compilers = new List<CompilerFact>();

            foreach (var toolchain in list.EnumerateArray())
            {
                if (Text(toolchain, "language") is { } language
                    && toolchain.TryGetProperty("compiler", out var compiler)
                    && Text(compiler, "id") is { } id)
                {
                    compilers.Add(new CompilerFact(language, id, Text(compiler, "version") ?? string.Empty));
                }
            }

            return new CompilerReading(compilers, compilers.Count == 0 ? "CMake identified no compiler" : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Nothing CMake wrote is taken on trust: an answer this build cannot read says nothing
            // about the compiler, and is reported as that rather than as any compiler at all.
            return CompilerReading.None($"CMake's file API answer could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The compilers <paramref name="buildDirectory"/> was last configured with, where
    /// <paramref name="project"/> is built with CMake; none otherwise.
    /// </summary>
    /// <param name="project">The leg's project.</param>
    /// <param name="buildDirectory">Its build directory.</param>
    /// <remarks>
    /// For a leg that runs against a build it does not make itself, such as <c>test --no-build</c>:
    /// its verdict is about binaries the last configure's compilers produced.
    /// </remarks>
    public IReadOnlyList<CompilerFact> Configured(Configuration.ProjectConfig? project, string buildDirectory)
        => project is not null && BuildAdapters.Find(project.Type) is CMakeAdapter
            ? Read(buildDirectory).Compilers
            : [];

    /// <summary>A string member's value, or <see langword="null"/> where it is absent, not a string, or empty.</summary>
    private static string? Text(JsonElement element, string member)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(member, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string Relative(string buildDirectory, string relative)
        => Path.Combine(buildDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
}
