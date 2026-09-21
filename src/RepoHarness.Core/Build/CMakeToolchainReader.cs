using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
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

/// <summary>
/// A configure asked which compilers it resolves, with the answers its build directory already held
/// then: what that configure answers is whatever is there afterwards and was not before.
/// </summary>
/// <param name="AnsweredBefore">The file API answers the build directory held before the configure, by file name.</param>
public sealed record CompilerQuestion(IReadOnlySet<string> AnsweredBefore);

/// <summary>The compilers a leg built with, as its line in a ledger names them.</summary>
public static class CompilerFacts
{
    /// <summary>
    /// What a build CMake configured as <paramref name="reading"/> says has to answer for against the
    /// compilerId its toolchain declares, or <see langword="null"/> where it answers for nothing.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="toolchainName">The leg's toolchain.</param>
    /// <param name="reading">What CMake reported about the configure.</param>
    /// <remarks>
    /// A compiler CMake configured the build with that is not the one declared fails the leg: the
    /// build would compile with it, and every verdict after that describes a compiler nobody chose.
    /// A language CMake named no compiler for is not a pass: the declaration asked for a fact
    /// nothing established - an older CMake writes no answer, and a misspelled language is never
    /// answered - so the leg is unwitnessed, naming why. Asked of a build that configures, and of a
    /// test run against a directory it did not configure, which holds the binaries to the same rule.
    /// </remarks>
    public static ReachedVerdict? HeldTo(HarnessConfig config, string toolchainName, CompilerReading reading)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(toolchainName);
        ArgumentNullException.ThrowIfNull(reading);

        if (!config.Toolchains.TryGetValue(toolchainName, out var toolchain) || toolchain.CompilerId.Count == 0)
        {
            return null;
        }

        var contradicted = new List<string>();
        var unanswered = new List<string>();

        foreach (var (language, id) in toolchain.CompilerId)
        {
            var found = reading.Compilers.FirstOrDefault(compiler => string.Equals(compiler.Language, language, StringComparison.OrdinalIgnoreCase));

            if (found is null)
            {
                unanswered.Add(language);
            }
            else if (!string.Equals(found.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                contradicted.Add($"{language} with {found.Id}{(found.Version.Length > 0 ? " " + found.Version : string.Empty)}, not {id}");
            }
        }

        if (contradicted.Count > 0)
        {
            return ReachedVerdict.Of(
                LegVerdict.Failed,
                $"CMake configured this build with another compiler than toolchain '{toolchainName}' declares: "
                + $"{string.Join("; ", contradicted)}. Name the compiler the toolchain means under its env or "
                + "cacheVars, where the host starts that one");
        }

        return unanswered.Count > 0
            ? ReachedVerdict.Of(
                LegVerdict.Unwitnessed,
                $"toolchain '{toolchainName}' declares the compiler for {string.Join(", ", unanswered)}, and CMake named "
                + $"none for {(unanswered.Count == 1 ? "it" : "them")}"
                + (reading.Unread is { } why ? $": {why}" : string.Empty)
                + "; nothing established which compiler this build used")
            : null;
    }

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

    /// <summary>
    /// Asks the next configure of <paramref name="buildDirectory"/> which compilers it resolves, noting
    /// the answers the directory holds now, so that configure's own is told apart from them.
    /// </summary>
    /// <param name="buildDirectory">The build directory, which need not exist yet.</param>
    /// <remarks>
    /// A configure that fails writes no answer about the compilers - CMake 4 writes an error index in
    /// its place - and leaves the last successful configure's where it was: read as the newest, that
    /// answer named a leg whose configure failed after the compiler changed by the compiler before.
    /// Told apart by what was there before rather than by the times in the names, which a clock that
    /// stepped back would reorder.
    /// </remarks>
    public CompilerQuestion Ask(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        var query = Relative(buildDirectory, QueryRelativePath);

        if (!_fileSystem.FileExists(query))
        {
            _fileSystem.CreateDirectory(Path.GetDirectoryName(query)!);
            _fileSystem.WriteAllTextAtomic(query, string.Empty);
        }

        return new CompilerQuestion(Answers(buildDirectory).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The compilers a configure of <paramref name="buildDirectory"/> resolved, one per language it
    /// identified one for, or why it said nothing: the configure <paramref name="asked"/> asked, or,
    /// without it, the last one that answered.
    /// </summary>
    /// <param name="buildDirectory">The build directory.</param>
    /// <param name="asked">
    /// What <see cref="Ask"/> noted before the configure being read; only an answer written since is
    /// that configure's. Left out, the newest answer is read, for a leg running against a directory
    /// it did not configure itself.
    /// </param>
    /// <remarks>
    /// Read from the newest index, whose name sorts last - the file API's own rule, since an index is
    /// named for the moment it was written. A language CMake could not identify a compiler for, such as
    /// the resource compiler it lists on Windows, is not one this build can be held to, and is left
    /// out rather than read as a mismatch.
    /// </remarks>
    public CompilerReading Read(string buildDirectory, CompilerQuestion? asked = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        var replies = Relative(buildDirectory, ReplyRelativeDirectory);

        try
        {
            var written = Answers(buildDirectory)
                .Where(name => asked is null || !asked.AnsweredBefore.Contains(name))
                .Order(StringComparer.Ordinal)
                .ToList();

            var index = written.LastOrDefault(name => name.StartsWith(IndexPrefix, StringComparison.Ordinal));

            if (index is null)
            {
                // What CMake wrote in its place when the configure failed, where it wrote that much.
                return written.LastOrDefault(name => name.StartsWith(ErrorPrefix, StringComparison.Ordinal)) is { } failed
                    ? CompilerReading.None(Error(Path.Combine(replies, failed)))
                    : CompilerReading.None(asked is null
                        ? "CMake wrote no file API answer there, which it does from version 3.20"
                        : "this configure wrote no file API answer, which CMake does from version 3.20");
            }

            using var indexDocument = JsonDocument.Parse(_fileSystem.ReadAllText(Path.Combine(replies, index)));

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
    /// What <paramref name="buildDirectory"/> was last configured with, where <paramref name="project"/>
    /// is built with CMake; <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="project">The leg's project.</param>
    /// <param name="buildDirectory">Its build directory.</param>
    /// <remarks>
    /// For a leg that runs against a build it does not make itself, such as <c>test --no-build</c>:
    /// its verdict is about binaries the last configure's compilers produced.
    /// </remarks>
    public CompilerReading? Configured(ProjectConfig? project, string buildDirectory)
        => project is not null && BuildAdapters.Find(project.Type) is CMakeAdapter
            ? Read(buildDirectory)
            : null;

    /// <summary>The first word of an index CMake writes when a configure answered.</summary>
    private const string IndexPrefix = "index-";

    /// <summary>The first word of the index CMake 4 writes in its place when a configure failed.</summary>
    private const string ErrorPrefix = "error-";

    /// <summary>The answers <paramref name="buildDirectory"/> holds, by file name: every index, and every error index.</summary>
    private IEnumerable<string> Answers(string buildDirectory)
    {
        var replies = Relative(buildDirectory, ReplyRelativeDirectory);

        return _fileSystem.DirectoryExists(replies)
            ? _fileSystem.EnumerateFiles(replies, recursive: false)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => (name.StartsWith(IndexPrefix, StringComparison.Ordinal) || name.StartsWith(ErrorPrefix, StringComparison.Ordinal))
                    && Path.GetExtension(name) == ".json")
            : [];
    }

    /// <summary>Why a configure CMake answered with an error index said nothing about the compilers.</summary>
    private string Error(string file)
    {
        using var document = JsonDocument.Parse(_fileSystem.ReadAllText(file));

        return document.RootElement.TryGetProperty("reply", out var reply)
            && reply.TryGetProperty("toolchains-v1", out var answer)
            && answer.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.String
            ? $"CMake answered that it could not say: {error.GetString()}"
            : "the configure failed, and CMake's answer names no reason";
    }

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
