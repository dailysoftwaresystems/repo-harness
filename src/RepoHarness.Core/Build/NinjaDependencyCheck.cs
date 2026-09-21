using System.Text;
using System.Text.RegularExpressions;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
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
public sealed partial class NinjaDependencyCheck(IProcessRunner processRunner, IFileSystem fileSystem, IHostPlatform platform)
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

    /// <summary>A backslash ending a line, which joins it to the next before anything else is read.</summary>
    [GeneratedRegex(@"\\\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex Splice { get; }

    /// <summary>A directive line: its name - <c>include</c>, <c>ifdef</c> - and what follows the name.</summary>
    [GeneratedRegex(@"^\s*#\s*(?<name>\w+)(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Directive { get; }

    /// <summary>What a quoted include names: <c>"util.h"</c>.</summary>
    [GeneratedRegex(@"^\s*""(?<header>[^""\r\n]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedHeader { get; }

    /// <summary>A condition asking whether a name is defined: <c>defined(NAME)</c> or <c>defined NAME</c>.</summary>
    [GeneratedRegex(@"^defined\s*\(\s*(?<name>\w+)\s*\)$|^defined\s+(?<name>\w+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DefinedCondition { get; }

    /// <summary>A condition that is a decimal number: <c>0</c>, <c>1</c>.</summary>
    [GeneratedRegex(@"^(?<digits>\d+)[uUlL]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberCondition { get; }

    /// <summary>The name an <c>#ifdef</c> or an <c>#ifndef</c> asks about.</summary>
    [GeneratedRegex(@"^\s*(?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex AskedName { get; }

    /// <summary>The extensions cl compiles as C++ when no <c>/TP</c> or <c>/TC</c> says otherwise.</summary>
    private static readonly HashSet<string> CPlusPlusExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpp", ".cxx", ".cc", ".c++", ".cp", ".ixx", ".cppm",
    };

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

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

        var records = ReadRecords(result.StandardOutput);

        if (records.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'ninja -t deps' listed no objects for '{buildDirectory}'. An empty answer is "
                + "indistinguishable from every object having recorded its headers, so it is never read as one.");
        }

        var withoutHeaders = records.Values.Where(record => record.Count == 0).Select(record => record.Object).ToList();

        if (withoutHeaders.Count == 0)
        {
            return new NinjaDependencyReport(records.Count, [], EmptyExcuses, null);
        }

        var (flagged, excused) = Excuse(buildDirectory, records, withoutHeaders);

        return new NinjaDependencyReport(records.Count, flagged, excused, null);
    }

    /// <summary>
    /// Separates objects that legitimately recorded no headers from those that did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only an object built under <c>deps = msvc</c> - its own build line's, or else its rule's - can
    /// record zero legitimately. Ninja reads its headers from <c>/showIncludes</c>, which never names
    /// the source itself, and drops every header whose path, as ninja holds it, names
    /// <c>program files</c> or <c>microsoft visual studio</c> as the system's own; so a translation
    /// unit that includes nothing, or only the standard library and the Windows SDK, records none. So
    /// does one built from a precompiled header under <c>/Yu</c> that includes, besides, only headers
    /// the precompiled header holds and guards - with <c>#pragma once</c> or an include guard - which
    /// cl has compiled and never opens again. Under <c>deps = gcc</c> the source is always listed, so
    /// zero can never be legitimate, and an object built that way keeps the check's full strength
    /// whatever else the manifest builds.
    /// </para>
    /// <para>
    /// So a zero is excused only where ninja rebuilds the object, all the same, for every header its
    /// compile surely includes: each header its command force-includes with <c>/FI</c>, and what its
    /// source and each of those headers include by a quoted include resolving beside them, where cl
    /// looks first, of a header ninja keeps - never inside a comment, and inside a conditional block
    /// only where the block is surely compiled. A block is surely compiled where its condition is a
    /// number, or asks whether <c>__cplusplus</c> is defined, which the unit's language answers - C++
    /// under <c>/TP</c> or for a C++ source, C under <c>/TC</c> or for a <c>.c</c> one - and never where
    /// it asks anything else: one under <c>#ifndef _WIN32</c> may be compiled out. CMake force-includes
    /// the header it precompiles in the object compiling it and in every unit built from it, holding
    /// its includes under <c>#ifdef __cplusplus</c> for C++.
    /// </para>
    /// <para>
    /// What ninja rebuilds the object for is the inputs of its build line and, for each input that is
    /// itself built, the dependencies its build recorded and its own inputs, however far back. The
    /// object compiling a precompiled header records everything the header holds, and each unit built
    /// from it names its <c>.pch</c>, so a held header is rebuilt for through that object; while it
    /// records nothing, nothing is rebuilt for a header the precompiled header holds, and neither that
    /// object nor any unit built from it is excused.
    /// </para>
    /// <para>
    /// Only what the object's own build line leads to ever excuses it: another object's record may have
    /// been written by an older build, under a Visual Studio in another language, or replayed by a
    /// compiler cache, and says nothing of whether ninja read what cl named for this one.
    /// </para>
    /// <para>
    /// The manifest is read the way ninja reads it - across the files it includes, where CMake keeps its
    /// rules, with ninja's escapes undone and its variables evaluated - so the source CMake names
    /// absolutely is a file that can be read, and the command a build line ran is the one cl was given.
    /// An object no build line produces, and a source that is not there or cannot be read, are never
    /// excused.
    /// </para>
    /// </remarks>
    private (IReadOnlyList<string> Flagged, IReadOnlyDictionary<string, string> Excused) Excuse(
        string buildDirectory,
        IReadOnlyDictionary<string, DepsRecord> records,
        List<string> withoutHeaders)
    {
        var graph = new RebuildGraph(
            buildDirectory,
            NinjaManifest.Read(_fileSystem, buildDirectory),
            records,
            _fileSystem,
            StringComparer.FromComparison(_platform.PathComparison));
        var flagged = new List<string>();
        var excused = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var obj in withoutHeaders)
        {
            if (graph.EdgeFor(obj) is { Deps: "msvc", Source: { } source } edge
                && graph.SurelyIncluded(edge, source) is { } included
                && graph.RebuildsFor(edge, included))
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

    /// <summary>
    /// <paramref name="command"/> split into arguments as cl's C runtime splits its command line: at
    /// spaces and tabs outside quotes, a quote only opening or closing quoting - two inside quotes
    /// being one - and a backslash literal except before a quote, where each pair of them is one
    /// backslash and an odd one left over makes the quote literal.
    /// </summary>
    private static List<string> CommandLineArguments(string command)
    {
        var arguments = new List<string>();
        var argument = new StringBuilder();
        var quoted = false;
        var started = false;

        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];

            if (character == '\\')
            {
                var backslashes = 0;

                while (index < command.Length && command[index] == '\\')
                {
                    backslashes++;
                    index++;
                }

                if (index < command.Length && command[index] == '"')
                {
                    argument.Append('\\', backslashes / 2);

                    if (backslashes % 2 == 1)
                    {
                        argument.Append('"');
                    }
                    else
                    {
                        index--;
                    }
                }
                else
                {
                    argument.Append('\\', backslashes);
                    index--;
                }

                started = true;
            }
            else if (character == '"')
            {
                if (quoted && index + 1 < command.Length && command[index + 1] == '"')
                {
                    argument.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                started = true;
            }
            else if (character is ' ' or '\t' && !quoted)
            {
                if (started)
                {
                    arguments.Add(argument.ToString());
                    argument.Clear();
                    started = false;
                }
            }
            else
            {
                argument.Append(character);
                started = true;
            }
        }

        if (started)
        {
            arguments.Add(argument.ToString());
        }

        return arguments;
    }

    /// <summary>
    /// The language cl compiles <paramref name="source"/> in: C++ under <c>/TP</c> and C under
    /// <c>/TC</c>, the last one given deciding; otherwise C for a <c>.c</c> file and C++ for one with a
    /// C++ extension. Unknown for any other, a resource script's among them.
    /// </summary>
    private static Language LanguageOf(IReadOnlyList<string> arguments, string source)
    {
        if (arguments.LastOrDefault(argument => argument is "/TP" or "-TP" or "/TC" or "-TC") is { } told)
        {
            return told.EndsWith('P') ? Language.CPlusPlus : Language.C;
        }

        var extension = Path.GetExtension(source);

        return string.Equals(extension, ".c", StringComparison.OrdinalIgnoreCase) ? Language.C
            : CPlusPlusExtensions.Contains(extension) ? Language.CPlusPlus
            : Language.Unknown;
    }

    /// <summary>
    /// The headers <paramref name="arguments"/> force-include: <c>/FI</c> or <c>-FI</c>, with its path
    /// joined to it or as the next argument.
    /// </summary>
    private static IEnumerable<string> ForceIncluded(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (argument.Length < 3 || argument[0] is not ('/' or '-') || argument[1] != 'F' || argument[2] != 'I')
            {
                continue;
            }

            if (argument.Length > 3)
            {
                yield return argument[3..];
            }
            else if (index + 1 < arguments.Count)
            {
                yield return arguments[++index];
            }
        }
    }

    /// <summary>
    /// The headers <paramref name="text"/> names by a quoted include that a compile in
    /// <paramref name="language"/> surely compiles: outside every comment, and in no conditional block
    /// that may not be compiled.
    /// </summary>
    private static IEnumerable<string> SurelyCompiledQuotedIncludes(string text, Language language)
    {
        var blocks = new Stack<(Truth Compiled, Truth Taken)>();

        foreach (var line in Uncommented(text).Split('\n'))
        {
            if (Directive.Match(line) is not { Success: true } directive)
            {
                continue;
            }

            var rest = directive.Groups["rest"].Value;

            switch (directive.Groups["name"].Value)
            {
                case "if":
                    Open(Condition(rest, language));
                    break;

                case "ifdef":
                    Open(Defined(AskedName.Match(rest).Groups["name"].Value, language));
                    break;

                case "ifndef":
                    Open(Not(Defined(AskedName.Match(rest).Groups["name"].Value, language)));
                    break;

                case "elif":
                    Otherwise(Condition(rest, language));
                    break;

                case "elifdef":
                    Otherwise(Defined(AskedName.Match(rest).Groups["name"].Value, language));
                    break;

                case "elifndef":
                    Otherwise(Not(Defined(AskedName.Match(rest).Groups["name"].Value, language)));
                    break;

                case "else":
                    Otherwise(Truth.True);
                    break;

                case "endif":
                    blocks.TryPop(out _);
                    break;

                case "include" when blocks.All(block => block.Compiled == Truth.True)
                    && QuotedHeader.Match(rest) is { Success: true } include:
                    yield return include.Groups["header"].Value;
                    break;
            }
        }

        void Open(Truth condition) => blocks.Push((condition, condition));

        // A later branch is compiled where no earlier one was, and its own condition holds.
        void Otherwise(Truth condition)
        {
            if (blocks.TryPop(out var block))
            {
                var compiled = And(Not(block.Taken), condition);

                blocks.Push((compiled, Or(block.Taken, compiled)));
            }
        }
    }

    /// <summary>
    /// What an <c>#if</c> or <c>#elif</c> asks, where the language alone answers it: a decimal number;
    /// whether <c>__cplusplus</c> is defined; <c>__cplusplus</c> itself, which a C++ compile defines
    /// and a C one reads as <c>0</c>; or the negation of one of these. Anything else is unknown.
    /// </summary>
    private static Truth Condition(string expression, Language language)
    {
        var condition = expression.Trim();

        if (condition.StartsWith('!'))
        {
            return Not(Condition(condition[1..], language));
        }

        if (NumberCondition.Match(condition) is { Success: true } number)
        {
            return number.Groups["digits"].Value.Trim('0').Length > 0 ? Truth.True : Truth.False;
        }

        if (DefinedCondition.Match(condition) is { Success: true } defined)
        {
            return Defined(defined.Groups["name"].Value, language);
        }

        return condition == "__cplusplus" ? Defined(condition, language) : Truth.Unknown;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is defined in a compile in <paramref name="language"/>, where the
    /// language alone tells: <c>__cplusplus</c> is, in C++, and is not, in C. Any other name is unknown.
    /// </summary>
    private static Truth Defined(string name, Language language)
        => name != "__cplusplus" ? Truth.Unknown
            : language switch
            {
                Language.CPlusPlus => Truth.True,
                Language.C => Truth.False,
                _ => Truth.Unknown,
            };

    private static Truth Not(Truth truth)
        => truth switch
        {
            Truth.True => Truth.False,
            Truth.False => Truth.True,
            _ => Truth.Unknown,
        };

    private static Truth And(Truth left, Truth right)
        => left == Truth.False || right == Truth.False ? Truth.False
            : left == Truth.True && right == Truth.True ? Truth.True
            : Truth.Unknown;

    private static Truth Or(Truth left, Truth right)
        => left == Truth.True || right == Truth.True ? Truth.True
            : left == Truth.False && right == Truth.False ? Truth.False
            : Truth.Unknown;

    /// <summary>
    /// <paramref name="text"/> as the preprocessor reads its directives: every line a backslash ends
    /// joined to the next first, then every comment a space. A comment opener inside a literal, a raw
    /// string or a number is none - <c>0x1'0000</c> opens no character literal - and a literal left
    /// open is closed by the end of its line; a raw string is a space, lines and all.
    /// </summary>
    private static string Uncommented(string text)
    {
        var spliced = Splice.Replace(text, string.Empty);
        var uncommented = new StringBuilder(spliced.Length);
        var index = 0;

        while (index < spliced.Length)
        {
            var character = spliced[index];
            var next = index + 1 < spliced.Length ? spliced[index + 1] : '\0';

            if (character == '/' && next == '*')
            {
                var end = spliced.IndexOf("*/", index + 2, StringComparison.Ordinal);

                index = end < 0 ? spliced.Length : end + 2;
                uncommented.Append(' ');
            }
            else if (character == '/' && next == '/')
            {
                var end = spliced.IndexOf('\n', index);

                index = end < 0 ? spliced.Length : end;
            }
            else if (character == '"' && RawStringEnd(spliced, index) is { } rawEnd)
            {
                index = rawEnd;
                uncommented.Append(' ');
            }
            else if (character is '"' or '\'')
            {
                var start = index++;

                while (index < spliced.Length && spliced[index] != character && spliced[index] != '\n')
                {
                    index += spliced[index] == '\\' && index + 1 < spliced.Length && spliced[index + 1] != '\n' ? 2 : 1;
                }

                if (index < spliced.Length && spliced[index] == character)
                {
                    index++;
                }

                uncommented.Append(spliced, start, index - start);
            }
            else if (char.IsAsciiDigit(character) && (index == 0 || !IsIdentifierCharacter(spliced[index - 1])))
            {
                var start = index++;

                while (index < spliced.Length
                    && (IsIdentifierCharacter(spliced[index])
                        || spliced[index] == '.'
                        || (spliced[index] == '\'' && index + 1 < spliced.Length && IsIdentifierCharacter(spliced[index + 1]))
                        || (spliced[index] is '+' or '-' && spliced[index - 1] is 'e' or 'E' or 'p' or 'P')))
                {
                    index++;
                }

                uncommented.Append(spliced, start, index - start);
            }
            else
            {
                uncommented.Append(character);
                index++;
            }
        }

        return uncommented.ToString();
    }

    /// <summary>
    /// Where the raw string whose opening quote is at <paramref name="quote"/> ends - <c>R"x(...)x"</c>,
    /// with any of its prefixes - or <see langword="null"/> where that quote opens no raw string.
    /// </summary>
    private static int? RawStringEnd(string text, int quote)
    {
        var prefix = quote;

        while (prefix > 0 && IsIdentifierCharacter(text[prefix - 1]))
        {
            prefix--;
        }

        var open = text.IndexOf('(', quote + 1);

        if (text[prefix..quote] is not ("R" or "u8R" or "uR" or "UR" or "LR") || open < 0 || open - quote - 1 > 16)
        {
            return null;
        }

        var delimiter = text[(quote + 1)..open];

        if (delimiter.Any(character => character is ' ' or ')' or '\\' or '\t' or '\r' or '\n' or '"'))
        {
            return null;
        }

        var close = text.IndexOf(")" + delimiter + "\"", open + 1, StringComparison.Ordinal);

        return close < 0 ? text.Length : close + delimiter.Length + 2;
    }

    private static bool IsIdentifierCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

    /// <summary>
    /// Every object <c>ninja -t deps</c> listed, keyed as ninja canonicalizes its path: the name it
    /// printed, how many dependencies it counted, and the ones it listed beneath, one to an indented line.
    /// </summary>
    private static Dictionary<string, DepsRecord> ReadRecords(string output)
    {
        var records = new Dictionary<string, DepsRecord>(StringComparer.Ordinal);
        DepsRecord? current = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = DepsHeader.Match(line);

            if (match.Success)
            {
                current = new DepsRecord(
                    match.Groups["object"].Value,
                    int.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    []);
                records[NinjaManifest.Normalize(current.Object)] = current;
            }
            else if (current is not null && line.Length > 0 && line[0] is ' ' or '\t')
            {
                current.Dependencies.Add(line.TrimStart());
            }
            else
            {
                current = null;
            }
        }

        return records;
    }

    private static readonly Dictionary<string, string> EmptyExcuses = new(StringComparer.Ordinal);

    /// <summary>The language cl compiles a unit in, which decides whether <c>__cplusplus</c> is defined.</summary>
    private enum Language
    {
        Unknown,
        C,
        CPlusPlus,
    }

    /// <summary>Whether a condition holds, where the check can tell.</summary>
    private enum Truth
    {
        False,
        True,
        Unknown,
    }

    /// <summary>One object's dependency record, as <c>ninja -t deps</c> printed it.</summary>
    /// <param name="Object">The object, as ninja named it.</param>
    /// <param name="Count">How many dependencies its header line counted.</param>
    /// <param name="Dependencies">The dependencies listed beneath it, each as ninja spelled it.</param>
    private sealed record DepsRecord(string Object, int Count, List<string> Dependencies);

    /// <summary>
    /// A build directory's objects as ninja rebuilds them: what each one's compile surely includes,
    /// and every file a change to which rebuilds it. Every path is held as the build directory
    /// resolves it, and compared as this machine compares paths.
    /// </summary>
    private sealed class RebuildGraph(
        string buildDirectory,
        NinjaManifest manifest,
        IReadOnlyDictionary<string, DepsRecord> records,
        IFileSystem fileSystem,
        StringComparer paths)
    {
        /// <summary>What each file surely includes in each language, read once however many units read it.</summary>
        private readonly Dictionary<Language, Dictionary<string, List<string>?>> _includes = [];

        /// <summary>What a change to rebuilds what each build line builds, worked out once however many objects reach it.</summary>
        private readonly Dictionary<NinjaEdge, HashSet<string>> _rebuiltFor = new(ReferenceEqualityComparer.Instance);

        /// <inheritdoc cref="NinjaManifest.EdgeFor"/>
        public NinjaEdge? EdgeFor(string output) => manifest.EdgeFor(output);

        /// <summary>
        /// The headers ninja keeps that <paramref name="edge"/>'s compile surely includes: those its
        /// command force-includes, and those <paramref name="source"/> and each of those headers
        /// include, as <see cref="SurelyCompiledQuotedIncludes"/> reads them, resolving beside them.
        /// <see langword="null"/> where one of those files is not there, or cannot be read, so its
        /// object is never excused.
        /// </summary>
        public List<string>? SurelyIncluded(NinjaEdge edge, string source)
        {
            if (Resolved(source) is not { } file)
            {
                return null;
            }

            var arguments = CommandLineArguments(edge.Command);
            var language = LanguageOf(arguments, file);
            var directory = Path.GetDirectoryName(file) ?? string.Empty;
            var forced = ForceIncluded(arguments).Select(header => Beside(directory, header)).OfType<string>().Where(Kept).ToList();
            var included = new List<string>(forced);

            foreach (var reading in forced.Prepend(file))
            {
                if (Includes(reading, language) is not { } headers)
                {
                    return null;
                }

                included.AddRange(headers);
            }

            return included;
        }

        /// <summary>
        /// Whether ninja rebuilds what <paramref name="edge"/> builds for each of <paramref name="headers"/>:
        /// each is an input of its build line or recorded by its build, or is rebuilt for by what
        /// builds one of its inputs, however far back.
        /// </summary>
        public bool RebuildsFor(NinjaEdge edge, List<string> headers)
        {
            if (headers.Count == 0)
            {
                return true;
            }

            var own = Own(edge);
            var through = Producers(edge).Select(RebuiltFor).ToList();

            return headers.All(header => own.Contains(header) || through.Exists(rebuiltFor => rebuiltFor.Contains(header)));
        }

        /// <summary>Everything a change to which rebuilds what <paramref name="edge"/> builds, however far back.</summary>
        private HashSet<string> RebuiltFor(NinjaEdge edge)
        {
            if (_rebuiltFor.TryGetValue(edge, out var known))
            {
                return known;
            }

            // Held before it is complete, so a cycle - which ninja never builds - leads nowhere.
            var rebuiltFor = Own(edge);
            _rebuiltFor[edge] = rebuiltFor;

            foreach (var producer in Producers(edge))
            {
                rebuiltFor.UnionWith(RebuiltFor(producer));
            }

            return rebuiltFor;
        }

        /// <summary>What a change to rebuilds what <paramref name="edge"/> builds by itself: its inputs, and what its build recorded.</summary>
        private HashSet<string> Own(NinjaEdge edge)
        {
            var own = new HashSet<string>(paths);

            foreach (var output in edge.Outputs)
            {
                foreach (var dependency in records.GetValueOrDefault(NinjaManifest.Normalize(output))?.Dependencies ?? [])
                {
                    if (Resolved(dependency) is { } path)
                    {
                        own.Add(path);
                    }
                }
            }

            foreach (var input in edge.Inputs)
            {
                if (Resolved(input) is { } path)
                {
                    own.Add(path);
                }
            }

            return own;
        }

        /// <summary>The build lines that build an input of <paramref name="edge"/>.</summary>
        private IEnumerable<NinjaEdge> Producers(NinjaEdge edge) => edge.Inputs.Select(manifest.EdgeFor).OfType<NinjaEdge>();

        /// <summary>
        /// The headers ninja keeps that <paramref name="file"/> surely includes in <paramref name="language"/>,
        /// or <see langword="null"/> where it is not there or cannot be read.
        /// </summary>
        private List<string>? Includes(string file, Language language)
        {
            if (!_includes.TryGetValue(language, out var read))
            {
                _includes[language] = read = new Dictionary<string, List<string>?>(paths);
            }

            if (read.TryGetValue(file, out var known))
            {
                return known;
            }

            List<string>? headers;

            try
            {
                var directory = Path.GetDirectoryName(file) ?? string.Empty;

                headers = fileSystem.FileExists(file)
                    ? [.. SurelyCompiledQuotedIncludes(fileSystem.ReadAllText(file), language)
                        .Select(header => Beside(directory, header))
                        .OfType<string>()
                        .Where(Kept)]
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fails closed: a file that cannot be read excuses nothing.
                headers = null;
            }

            read[file] = headers;

            return headers;
        }

        /// <summary>Whether <paramref name="header"/> is there, and ninja keeps it when cl names it.</summary>
        private bool Kept(string header) => fileSystem.FileExists(header) && !NinjaTakesForTheSystems(header);

        /// <summary>
        /// Whether ninja takes <paramref name="header"/> for the system's own under <c>deps = msvc</c>, and
        /// drops it from what the object records: the path ninja holds it by - relative to the build
        /// directory, where both are on one drive - names one of these, as ninja's own
        /// <c>CLParser::IsSystemInclude</c> reads it. A tree kept under <c>Program Files</c> keeps its
        /// own headers, which ninja reaches from its build directory without naming that.
        /// </summary>
        private bool NinjaTakesForTheSystems(string header)
        {
            var held = Path.GetRelativePath(buildDirectory, header);

            return held.Contains("program files", StringComparison.OrdinalIgnoreCase)
                || held.Contains("microsoft visual studio", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary><paramref name="path"/> as the build directory resolves it - a record's and a build line's are relative to it.</summary>
        private string? Resolved(string path) => Beside(buildDirectory, path);

        /// <summary>
        /// <paramref name="path"/> resolved in <paramref name="directory"/>, or <see langword="null"/>
        /// where it names no path this machine can hold.
        /// </summary>
        private static string? Beside(string directory, string path)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(directory, path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}
