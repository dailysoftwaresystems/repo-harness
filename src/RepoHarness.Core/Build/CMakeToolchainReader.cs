using System.Text.Json;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Build;

/// <summary>One compiler CMake configured a build with, for one language.</summary>
/// <param name="Language">The language as CMake names it: <c>C</c>, <c>CXX</c>, or another it reports.</param>
/// <param name="Id">CMake's own id for the compiler, such as <c>GNU</c>, <c>Clang</c> or <c>MSVC</c>.</param>
/// <param name="Version">The compiler's version as CMake reports it; empty where it reported none.</param>
public sealed record CompilerFact(string Language, string Id, string Version);

/// <summary>
/// A compiler as CMake's record of identifying it names it: what every configure of the build directory
/// after the first builds with, since CMake identifies a cached compiler once.
/// </summary>
/// <param name="Language">The language, as CMake names it: <c>C</c> or <c>CXX</c>.</param>
/// <param name="Id">CMake's id for the compiler, such as <c>MSVC</c> or <c>GNU</c>; empty where it identified none.</param>
/// <param name="Version">The version CMake identified it as; empty where it recorded none.</param>
/// <param name="Program">The compiler CMake runs: the whole path its record names, or empty where it names none.</param>
/// <param name="Arguments">
/// The words CMake runs it with before any of its own - the compiler a launcher such as ccache is given,
/// where the launcher is what was named - as the record keeps them.
/// </param>
/// <param name="MsvcOptions">Whether it takes MSVC's options, as cl and clang-cl do, rather than GCC's.</param>
public sealed record IdentifiedCompiler(
    string Language,
    string Id,
    string Version,
    string Program,
    IReadOnlyList<string> Arguments,
    bool MsvcOptions);

/// <summary>What CMake said about the compilers it configured a build with.</summary>
/// <param name="Compilers">One per language it identified a compiler for.</param>
/// <param name="Unread">
/// Why it said nothing, where it said nothing: no answer, or one this build cannot read.
/// </param>
public sealed record CompilerReading(IReadOnlyList<CompilerFact> Compilers, string? Unread)
{
    /// <summary>
    /// The languages CMake's answer lists without identifying their compiler, each with why, to end a
    /// sentence: the resource compiler it lists on Windows, which it never identifies, and any language
    /// whose identification could not be read where CMake keeps it, or could not be tied to the answer.
    /// </summary>
    public IReadOnlyDictionary<string, string> Unidentified { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
    /// A language CMake identified no compiler for is not a pass: the declaration asked for a fact
    /// nothing established - an older CMake writes no answer, a misspelled language is never
    /// answered, and one CMake lists with no id this build can tie to its answer is no id to hold it
    /// to - so the leg is unwitnessed, naming why for each. Asked of a build that configures, and of a test run
    /// against a directory it did not configure, which holds the binaries to the same rule.
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

        if (unanswered.Count == 0)
        {
            return null;
        }

        // A language CMake's answer does not list, and one it lists without identifying its compiler,
        // are different facts: the second is one CMake enabled, and only its compiler's identity is missing.
        var listed = unanswered.Where(reading.Unidentified.ContainsKey).ToList();
        var unlisted = unanswered.Except(listed, StringComparer.OrdinalIgnoreCase).ToList();
        var said = new List<string>();

        if (unlisted.Count > 0)
        {
            said.Add($"CMake named none for {(unlisted.Count == unanswered.Count ? Pronoun(unlisted) : string.Join(", ", unlisted))}"
                + (reading.Unread is { } why ? $": {why}" : string.Empty));
        }

        said.AddRange(listed.Select(language =>
            $"CMake identified none for {(unanswered.Count == 1 ? "it" : language)}: {reading.Unidentified[language]}"));

        return ReachedVerdict.Of(
            LegVerdict.Unwitnessed,
            $"toolchain '{toolchainName}' declares the compiler for {string.Join(", ", unanswered)}, and "
            + $"{string.Join("; ", said)}; nothing established which compiler this build used");

        static string Pronoun(List<string> languages) => languages.Count == 1 ? "it" : "them";
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
/// <para>
/// The compilers CMake resolved, not the ones a toolchain names: resolved by CMake, a name can reach
/// another compiler than the one it names - how a leg named msvc built with MinGW's gcc on every run.
/// The query is a file the build directory keeps, so every configure answers it, with the
/// <c>toolchains-v1</c> object that CMake 3.20 and later write.
/// </para>
/// <para>
/// That answer holds what the top-level directory holds, so a language only a subdirectory enables -
/// C, where a C++ project fetches a dependency whose <c>project()</c> declares C and C++ - comes
/// without the id and the version CMake identified in that subdirectory alone: with its compiler's
/// path where CMake caches one, and under a Visual Studio generator, which caches none, without even
/// that. CMake keeps each language's identification once for the whole build directory, in
/// <c>CMakeFiles/&lt;its version&gt;/CMake&lt;language&gt;Compiler.cmake</c>, which enabling the
/// language in any directory loads - the file its configure log's "The C compiler identification is
/// MSVC" is written from - so a language the answer names no id for is identified from that record.
/// </para>
/// <para>
/// The record is held to the answer, because it can be a later configure's: one that identifies the
/// compiler again - given another, or run with <c>--fresh</c> - rewrites it, and one that then fails
/// writes no answer, which leaves the last one's beside a record of a compiler that built nothing
/// there. So the record must name the compiler the answer names, where the answer names one - the
/// program CMake makes of it, since the answer keeps a toolchain file's value as written: a list's first
/// item, a path tidied as CMake tidies it, a name alone found as a program - and must have been written
/// no later than the answer, which a configure writes after every record it writes. Each tie leaves a
/// case to the other: the time alone tells a later record apart where the answer names no compiler, as
/// under a Visual Studio generator, where it names one by its name alone, which a program of that name
/// elsewhere answers to, and where the same file was replaced in place; the compiler alone does where a
/// clock stepped back since the answer was written. Measured with CMake 3.29 and 4.3: MSVC through
/// Ninja and through Visual Studio 18 2026, gcc on Windows and on Linux, toolchain files naming the
/// compiler each way the comparison lists, and a configure that failed after identifying the compiler
/// again.
/// </para>
/// </remarks>
public sealed partial class CMakeToolchainReader(IFileSystem fileSystem)
{
    /// <summary>The query, relative to a build directory.</summary>
    public const string QueryRelativePath = ".cmake/api/v1/query/toolchains-v1";

    /// <summary>Where CMake writes its answers, relative to a build directory.</summary>
    public const string ReplyRelativeDirectory = ".cmake/api/v1/reply";

    /// <summary>
    /// A line of CMake's record of identifying a language's compiler: <c>set(CMAKE_C_COMPILER_ID "MSVC")</c>,
    /// the name and the value CMake wrote between the quotes.
    /// </summary>
    [GeneratedRegex(@"^\s*set\((?<name>CMAKE_\w+)\s+""(?<value>.*)""\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordedSetting { get; }

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
    /// named for the moment it was written. A language the answer names with no id is identified from
    /// CMake's own record of it, as the remarks on this class say. One CMake identified nowhere, such as
    /// the resource compiler it lists on Windows, is not one this build can be held to: it is left out
    /// of the compilers rather than read as a mismatch, and named among the unidentified, with why.
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

            var indexFile = Path.Combine(replies, index);

            using var indexDocument = JsonDocument.Parse(_fileSystem.ReadAllText(indexFile));

            // When CMake answered: a record of identifying a compiler written since is a later configure's.
            var answeredAt = _fileSystem.LastWriteTimeUtc(indexFile);

            // The version that answered, under which CMake keeps its record of each language it identified.
            var version = VersionIn(indexDocument);

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
            var unidentified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var toolchain in list.EnumerateArray())
            {
                if (Text(toolchain, "language") is not { } language)
                {
                    continue;
                }

                // A missing compiler object reads as one naming nothing, as an empty one does.
                var compiler = toolchain.TryGetProperty("compiler", out var named) ? named : default;

                if (Text(compiler, "id") is { } id)
                {
                    compilers.Add(new CompilerFact(language, id, Text(compiler, "version") ?? string.Empty));
                    continue;
                }

                var (fact, why) = Recorded(buildDirectory, version, language, Text(compiler, "path"), answeredAt);

                if (fact is not null)
                {
                    compilers.Add(fact);
                }
                else
                {
                    unidentified[language] = why!;
                }
            }

            return new CompilerReading(compilers, compilers.Count == 0 ? "CMake identified no compiler" : null) { Unidentified = unidentified };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Nothing CMake wrote is taken on trust: an answer this build cannot read says nothing
            // about the compiler, and is reported as that rather than as any compiler at all.
            return CompilerReading.None($"CMake's file API answer could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The C and C++ compilers the next configure of <paramref name="buildDirectory"/> builds with, as
    /// CMake's records of identifying them name them, under the version of CMake that last answered
    /// there; and, for any record that could not be read, why, to end a sentence.
    /// </summary>
    /// <param name="buildDirectory">The build directory.</param>
    /// <remarks>
    /// CMake identifies a cached compiler once, when the directory is first configured, and every
    /// configure after that loads the record: what it names is what builds there, whatever is at its
    /// path now. A directory no configure has answered in comes back with none, as does a language whose
    /// record is not there: nothing was identified that a configure would load. A compiler CMake could
    /// not identify comes back as recorded, with an empty id and version: which compilers can be asked
    /// anything is the asker's to decide, and one of no id it knows is one it never asks, so it is never
    /// one that could not be read. A record missing a line CMake always writes is one that could not.
    /// </remarks>
    public (IReadOnlyList<IdentifiedCompiler> Compilers, IReadOnlyList<string> Unread) Identified(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        string? version;

        try
        {
            version = Answers(buildDirectory)
                .Where(name => name.StartsWith(IndexPrefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .LastOrDefault() is { } index
                ? VersionThatAnswered(Path.Combine(Relative(buildDirectory, ReplyRelativeDirectory), index))
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ([], [$"CMake's file API answer could not be read: {ex.Message.TrimEnd('.')}"]);
        }

        if (version is null)
        {
            return ([], []);
        }

        var compilers = new List<IdentifiedCompiler>();
        var unread = new List<string>();

        foreach (var language in new[] { "C", "CXX" })
        {
            var record = Path.Combine(buildDirectory, "CMakeFiles", version, $"CMake{language}Compiler.cmake");

            try
            {
                if (!_fileSystem.FileExists(record))
                {
                    continue;
                }

                var settings = Settings(record);

                string? Setting(string suffix) => settings.GetValueOrDefault($"CMAKE_{language}_COMPILER{suffix}");

                // CMake writes these three lines in every such record, each empty where it identified nothing:
                // one missing is a record CMake did not write whole, and none of it can be relied on.
                if (Setting(string.Empty) is not { } program || Setting("_ID") is not { } id || Setting("_VERSION") is not { } identified)
                {
                    unread.Add($"'{record}' lacks a line CMake writes in every record of a compiler: its path, id or version, for {language}");
                    continue;
                }

                // Recorded from CMake 3.14 on, for every compiler; MSVC's own takes its options before then.
                var variant = Setting("_FRONTEND_VARIANT") ?? string.Empty;

                compilers.Add(new IdentifiedCompiler(
                    language,
                    id,
                    identified,
                    program,
                    (Setting("_ARG1") ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
                    variant.Length > 0 ? variant == "MSVC" : id == "MSVC"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unread.Add($"'{record}' could not be read: {ex.Message.TrimEnd('.')}");
            }
        }

        return (compilers, unread);
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

    /// <summary>The version of CMake that wrote the index at <paramref name="indexFile"/>, or <see langword="null"/> where it names none.</summary>
    private string? VersionThatAnswered(string indexFile)
    {
        using var index = JsonDocument.Parse(_fileSystem.ReadAllText(indexFile));

        return VersionIn(index);
    }

    /// <summary>The version of CMake an index names, under which CMake keeps its record of each language it identified.</summary>
    private static string? VersionIn(JsonDocument index)
        => index.RootElement.TryGetProperty("cmake", out var cmake) && cmake.TryGetProperty("version", out var version)
            ? Text(version, "string")
            : null;

    /// <summary>
    /// The settings a record of identifying a compiler holds, by name: each <c>set(NAME "value")</c> line's
    /// name and the value between its quotes.
    /// </summary>
    private Dictionary<string, string> Settings(string record)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in _fileSystem.ReadAllText(record).Split('\n'))
        {
            // A line CRLF ends keeps its '\r', which the pattern's closing spaces take.
            if (RecordedSetting.Match(line) is { Success: true } setting)
            {
                settings[setting.Groups["name"].Value] = setting.Groups["value"].Value;
            }
        }

        return settings;
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

    /// <summary>
    /// <paramref name="language"/>'s compiler as CMake's own record of identifying it names it, for an
    /// answer that names no id for it - or, where that record does not identify it, why, to end a sentence.
    /// </summary>
    /// <param name="buildDirectory">The build directory.</param>
    /// <param name="version">The CMake version that answered, whose directory holds the record; <see langword="null"/> where its index named none.</param>
    /// <param name="language">The language, as CMake names it.</param>
    /// <param name="path">The compiler the answer names for it, as the top-level directory spells it; <see langword="null"/> where it names none.</param>
    /// <param name="answeredAt">When the answer was written.</param>
    /// <remarks>
    /// Held to the answer, as the remarks on this class say: to the compiler it names, where it names
    /// one, and to when it was written.
    /// </remarks>
    private (CompilerFact? Fact, string? Why) Recorded(string buildDirectory, string? version, string language, string? path, DateTime answeredAt)
    {
        var answered = $"its answer names {(path is null ? "no compiler" : $"'{path}'")} for {language} and no id, and";

        if (version is null)
        {
            return (null, $"{answered} its index names no CMake version, which its record of identifying {language}'s compiler is kept under");
        }

        var record = Path.Combine(buildDirectory, "CMakeFiles", version, $"CMake{language}Compiler.cmake");
        var of = $"{answered} its record of identifying {language}'s compiler, '{record}',";

        try
        {
            if (!_fileSystem.FileExists(record))
            {
                return (null, $"{of} is not there");
            }

            var settings = Settings(record);

            if (settings.GetValueOrDefault($"CMAKE_{language}_COMPILER_ID") is not { Length: > 0 } id)
            {
                return (null, $"{of} names no id either");
            }

            if (path is not null)
            {
                if (settings.GetValueOrDefault($"CMAKE_{language}_COMPILER") is not { Length: > 0 } recorded)
                {
                    return (null, $"{of} names no compiler");
                }

                if (!SameProgram(path, recorded))
                {
                    return (null, $"{of} names '{recorded}', another compiler");
                }
            }

            if (_fileSystem.LastWriteTimeUtc(record) > answeredAt)
            {
                return (null, $"{of} was written after that answer");
            }

            return (new CompilerFact(language, id, settings.GetValueOrDefault($"CMAKE_{language}_COMPILER_VERSION") ?? string.Empty), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fails as unidentified, never as any compiler: a record this build cannot read says nothing.
            return (null, $"{of} could not be read: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>
    /// Whether <paramref name="recorded"/>, the compiler a record names, is the one <paramref name="answered"/>
    /// names, as CMake makes a compiler of what a toolchain file sets: the first item of a list, the rest of
    /// which are its arguments; a path, tidied of <c>.</c>, <c>..</c> and doubled separators, with either
    /// separator where the host takes both; and a name alone, found as a program - with <c>.com</c> or
    /// <c>.exe</c> after it, on Windows.
    /// </summary>
    /// <remarks>
    /// The answer keeps what the toolchain file set, and the record what CMake made of it, in
    /// <c>Modules/CMakeDetermineCompiler.cmake</c>: measured with CMake 3.29 and 4.3, a toolchain file's
    /// <c>C:\Strawberry\c\bin\gcc.exe</c>, <c>C:/Strawberry/c/bin/../bin/gcc.exe</c>,
    /// <c>c:/Strawberry/c/bin//gcc.exe</c>, <c>C:/Strawberry/c/bin/gcc.exe;-m64</c>, <c>gcc</c> and
    /// <c>x86_64-w64-mingw32-gcc-13.2.0</c> left each as written in the answer and the file it tidied or
    /// found in the record, in the case written. Both sides are tidied, as a CMake that tidies nothing
    /// leaves the two alike.
    /// </remarks>
    private static bool SameProgram(string answered, string recorded)
    {
        // The list's first item. Off Windows, CMake splits the value at ':' as well, reading it as a search
        // path; a compiler's path holding one is left whole here, since CMake could not find that compiler.
        var program = answered.Split(';')[0];

        // A NUL names no file, and no path holding one can be tidied.
        if (program.Contains('\0', StringComparison.Ordinal) || recorded.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (Path.GetDirectoryName(program) is { Length: > 0 })
        {
            return string.Equals(Path.GetFullPath(program), Path.GetFullPath(recorded), StringComparison.Ordinal);
        }

        var file = Path.GetFileName(recorded);

        return string.Equals(program, file, StringComparison.Ordinal)
            || (OperatingSystem.IsWindows()
                && (string.Equals($"{program}.com", file, StringComparison.Ordinal) || string.Equals($"{program}.exe", file, StringComparison.Ordinal)));
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
