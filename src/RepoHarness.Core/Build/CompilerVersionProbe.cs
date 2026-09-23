using System.Globalization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Build;

/// <summary>What asking a compiler its version found.</summary>
/// <param name="Version">Its version now, spelled as CMake spells one, where it gave one.</param>
/// <param name="Replaced">
/// Why what is there now is not the compiler CMake identified at all, to end a sentence: it is gone, or
/// it is another kind of compiler. <see langword="null"/> where it is the kind CMake identified.
/// </param>
/// <param name="Unanswered">
/// Why it could not be asked, to end a sentence, where it could not; <see langword="null"/> otherwise.
/// </param>
public sealed record CompilerVersionAnswer(string? Version, string? Replaced, string? Unanswered)
{
    /// <summary>The compiler gave <paramref name="version"/>.</summary>
    /// <param name="version">The version, spelled as CMake spells one.</param>
    public static CompilerVersionAnswer Gave(string version) => new(version, null, null);

    /// <summary>What is there now is not the compiler CMake identified.</summary>
    /// <param name="why">Why not, to end a sentence.</param>
    public static CompilerVersionAnswer Unlike(string why) => new(null, why, null);

    /// <summary>The compiler could not be asked.</summary>
    /// <param name="why">Why not, to end a sentence.</param>
    public static CompilerVersionAnswer NotAsked(string why) => new(null, null, why);
}

/// <summary>
/// Asks a compiler its version as CMake identifies one: from the macros the compiler predefines, read by
/// preprocessing a line that names them, and put together as CMake puts them together.
/// </summary>
/// <remarks>
/// CMake identifies a cached compiler once, when a build directory is first configured, and every
/// configure after that loads its record of it. A compiler updated in place - the same path, another
/// version, as a Visual Studio update rewrites cl.exe where it stands - is never identified again, and a
/// build system has no edge on the compiler: a consumer's first build after one failed every precompiled
/// header it had with C1853, "from a different version of the compiler". What the compiler says now,
/// held to that record, is the one way to see it.
/// <para>
/// Preprocessed, not compiled: the line names the macros CMake's identification reads - _MSC_VER,
/// _MSC_FULL_VER and _MSC_BUILD for MSVC; __GNUC__, its minor and its patch level for GNU; __clang_major__
/// and its fellows for Clang, with __apple_build_version__ for AppleClang - and takes a fraction of a
/// second, where a scratch configure took 19 with MSVC. Measured against CMake 4.3's own records, MSVC
/// 19.51.36260.0 through Visual Studio 18, MinGW gcc 13.2.0, Linux gcc 13.3.0 and clang 18.1.3 each came
/// out as CMake wrote it. Which macros a version is made of is decided by the id CMake recorded, never by
/// which are defined: clang defines __GNUC__ as well, as 4.2.1.
/// </para>
/// </remarks>
public sealed class CompilerVersionProbe(IProcessRunner processRunner, IFileSystem fileSystem)
{
    /// <summary>The word the probed line starts with, which no compiler defines.</summary>
    private const string Marker = "repo_harness_compiler_version";

    /// <summary>
    /// Every macro a known id's version is made of, in the order the probed line names them: one the
    /// compiler does not define comes back as its own name.
    /// </summary>
    private static readonly string[] Macros =
    [
        "_MSC_VER", "_MSC_FULL_VER", "_MSC_BUILD",
        "__GNUC__", "__GNUG__", "__GNUC_MINOR__", "__GNUC_PATCHLEVEL__",
        "__clang_major__", "__clang_minor__", "__clang_patchlevel__", "__apple_build_version__",
    ];

    /// <summary>
    /// How long a compiler is given to preprocess one line: far past any that is working, and short of
    /// holding a build up for long on one that is not.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>
    /// What <paramref name="compiler"/> says its version is now, or <see langword="null"/> where CMake's id
    /// for it is one this build does not know how to put a version together for.
    /// </summary>
    /// <param name="compiler">The compiler as CMake's record of identifying it names it.</param>
    /// <param name="scratchDirectory">Where the line to preprocess is written: never the build directory, whose files date its changes.</param>
    /// <param name="environment">The environment the build's phases start in, a developer environment's among it.</param>
    /// <param name="programDirectories">The directories the build's phases have added to their PATH.</param>
    /// <param name="cancellationToken">Stops the compiler.</param>
    public async Task<CompilerVersionAnswer?> AskAsync(
        IdentifiedCompiler compiler,
        string scratchDirectory,
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<string> programDirectories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);

        if (!Knows(compiler.Id))
        {
            return null;
        }

        // A record that identified the kind but left out what a question needs: nothing to hold an answer
        // to, or nothing to ask.
        if (compiler.Version.Length == 0)
        {
            return CompilerVersionAnswer.NotAsked("CMake recorded no version of it");
        }

        if (compiler.Program.Length == 0)
        {
            return CompilerVersionAnswer.NotAsked("CMake's record of it names no program");
        }

        // CMake records a whole path, and one that is not there is a compiler that is gone.
        if (Path.IsPathRooted(compiler.Program) && !_fileSystem.FileExists(compiler.Program))
        {
            return CompilerVersionAnswer.Unlike("it is not there now");
        }

        var source = Path.Combine(scratchDirectory, $"compiler-version-{compiler.Language}{(compiler.Language == "C" ? ".c" : ".cpp")}");

        _fileSystem.CreateDirectory(scratchDirectory);
        _fileSystem.WriteAllTextAtomic(source, $"{Marker} {string.Join(' ', Macros)}\n");

        // The options for preprocessing alone, as the compiler's kind takes them, after whatever CMake runs it with.
        string[] preprocess = compiler.MsvcOptions ? ["/nologo", "/EP", source] : ["-E", "-P", source];
        ProcessResult result;

        try
        {
            result = await _processRunner.RunAsync(
                    new ProcessRequest
                    {
                        FileName = compiler.Program,
                        Arguments = [.. compiler.Arguments, .. preprocess],
                        WorkingDirectory = scratchDirectory,
                        Environment = environment,
                        AppendToPath = programDirectories,
                        Timeout = Budget,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            return CompilerVersionAnswer.NotAsked(ex.Message.TrimEnd('.'));
        }

        if (result.TimedOut)
        {
            return CompilerVersionAnswer.NotAsked($"it had not preprocessed one line after {Budget.TotalMinutes} minutes");
        }

        if (result.ExitCode != 0)
        {
            return CompilerVersionAnswer.NotAsked($"preprocessing one line, it exited {result.ExitCode}: {Said(result, Path.GetFileName(source))}");
        }

        return Read(compiler.Id, result.StandardOutput);
    }

    /// <summary>
    /// The version the probed line in <paramref name="output"/> gives a compiler CMake identified as
    /// <paramref name="id"/>, or why it gives none.
    /// </summary>
    /// <param name="id">CMake's id for the compiler.</param>
    /// <param name="output">What the compiler printed, preprocessing the line.</param>
    internal static CompilerVersionAnswer Read(string id, string output)
    {
        var line = output
            .Split('\n')
            .Select(text => text.Trim())
            .FirstOrDefault(text => text.StartsWith(Marker + " ", StringComparison.Ordinal));

        if (line is null)
        {
            return CompilerVersionAnswer.NotAsked($"preprocessing one line, it printed no line starting '{Marker}'");
        }

        var words = line[(Marker.Length + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length != Macros.Length)
        {
            return CompilerVersionAnswer.NotAsked($"preprocessing one line naming {Macros.Length} macros, it printed {words.Length} words: '{line}'");
        }

        var defined = new Dictionary<string, long>(StringComparer.Ordinal);

        for (var index = 0; index < Macros.Length; index++)
        {
            // A macro the compiler does not define is left as its own name.
            if (string.Equals(words[index], Macros[index], StringComparison.Ordinal))
            {
                continue;
            }

            if (!long.TryParse(words[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return CompilerVersionAnswer.NotAsked($"preprocessing one line, it printed '{words[index]}' for {Macros[index]}");
            }

            defined[Macros[index]] = value;
        }

        return VersionOf(id, defined) is { } version
            ? CompilerVersionAnswer.Gave(version)
            : CompilerVersionAnswer.Unlike($"what is there now defines none of the macros a {id} compiler's version is made of");
    }

    /// <summary>
    /// The version CMake identifies a compiler of <paramref name="id"/> as, from the values of the macros
    /// it predefines, or <see langword="null"/> where the one a version starts with is not among them.
    /// </summary>
    /// <param name="id">CMake's id for the compiler.</param>
    /// <param name="defined">The macros the compiler defines, with their values.</param>
    /// <remarks>
    /// CMake's own formulas, from <c>Modules/Compiler/&lt;id&gt;-DetermineCompiler.cmake</c>, and its own way
    /// of putting the parts together: the first always, then each further one only while every one
    /// before it is there, in decimal, without leading zeros.
    /// </remarks>
    internal static string? VersionOf(string id, IReadOnlyDictionary<string, long> defined)
    {
        long? Macro(string name) => defined.TryGetValue(name, out var value) ? value : null;

        long?[] parts = id switch
        {
            // CMake takes the build from _MSC_FULL_VER's last five digits from _MSC_VER 1400 on, and its last
            // four before. The fifth from the end is _MSC_VER's own last digit, 0 in every release before
            // 1400, so the last five read the same for all of them.
            "MSVC" => Macro("_MSC_VER") is { } version
                ? [version / 100, version % 100, Macro("_MSC_FULL_VER") is { } full ? full % 100000 : null, Macro("_MSC_BUILD")]
                : [],
            "GNU" => [Macro("__GNUC__") ?? Macro("__GNUG__"), Macro("__GNUC_MINOR__"), Macro("__GNUC_PATCHLEVEL__")],
            "Clang" => [Macro("__clang_major__"), Macro("__clang_minor__"), Macro("__clang_patchlevel__")],
            "AppleClang" => [Macro("__clang_major__"), Macro("__clang_minor__"), Macro("__clang_patchlevel__"), Macro("__apple_build_version__")],
            _ => [],
        };

        var present = parts.TakeWhile(part => part is not null).Select(part => part!.Value.ToString(CultureInfo.InvariantCulture)).ToList();

        return present.Count == 0 ? null : string.Join('.', present);
    }

    /// <summary>Whether CMake's formula for <paramref name="id"/>'s version is one this build knows.</summary>
    /// <param name="id">CMake's id for a compiler.</param>
    internal static bool Knows(string id) => id is "MSVC" or "GNU" or "Clang" or "AppleClang";

    /// <summary>
    /// The first thing a compiler that failed said: the first line of its standard error, or of its
    /// standard output where that has none, passing over <paramref name="source"/>'s own name.
    /// </summary>
    /// <param name="result">How the compiler ended.</param>
    /// <param name="source">The name of the file it preprocessed.</param>
    /// <remarks>
    /// cl names each file it reads before anything it says about it, and under /EP it names it on its
    /// standard error: quoted from the first line, every failure of MSVC's said only which file it read.
    /// </remarks>
    private static string Said(ProcessResult result, string source)
        => new[] { result.StandardError, result.StandardOutput }
            .SelectMany(text => text.Split('\n'))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !string.Equals(line, source, StringComparison.OrdinalIgnoreCase))
            ?? "it said nothing";
}
