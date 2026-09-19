using System.Text;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Build;

/// <summary>How a ninja manifest says one output is built.</summary>
/// <param name="Rule">The rule that builds it.</param>
/// <param name="Source">Its first explicit input, the source a compile rule compiles; <see langword="null"/> when it has none.</param>
/// <param name="Deps">
/// How the edge records its dependencies - <c>gcc</c> or <c>msvc</c> - its own binding or else its
/// rule's; <see langword="null"/> when neither says.
/// </param>
public sealed record NinjaEdge(string Rule, string? Source, string? Deps);

/// <summary>
/// What a ninja build directory's manifest declares about how each output is built, read the way
/// ninja reads it: <c>build.ninja</c> and every file it includes, with ninja's escapes undone.
/// </summary>
/// <remarks>
/// <para>
/// Across the files, because CMake writes its rules to <c>CMakeFiles/rules.ninja</c> and only includes
/// that file from <c>build.ninja</c>: read alone, <c>build.ninja</c> never says how any object records
/// its dependencies. A file an <c>include</c> names shares the scope it is included into; one a
/// <c>subninja</c> names sees the rules declared before it, and its own are its own. Every path is
/// read from the build directory, as ninja run there reads it, and a file read once is not read again.
/// </para>
/// <para>
/// With the escapes undone, because a path in a build line is spelled with them: CMake names a
/// source by its absolute path, so on Windows its drive's colon arrives as <c>$:</c> and a space in
/// it as <c>$ </c>, and read raw <c>C$:\src\main.c</c> is no file at all. A line ending in an
/// unescaped <c>$</c> continues on the next.
/// </para>
/// </remarks>
public sealed class NinjaManifest
{
    private readonly Dictionary<string, NinjaEdge> _edges = new(StringComparer.Ordinal);

    private NinjaManifest()
    {
    }

    /// <summary>Reads the manifest <paramref name="buildDirectory"/> holds.</summary>
    /// <param name="fileSystem">Reads the files.</param>
    /// <param name="buildDirectory">The directory ninja runs in, whose <c>build.ninja</c> is read first.</param>
    /// <remarks>A file the manifest names that is not there is passed over: what it would have declared is simply not known.</remarks>
    public static NinjaManifest Read(IFileSystem fileSystem, string buildDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        var manifest = new NinjaManifest();
        var read = new HashSet<string>(StringComparer.Ordinal);

        manifest.ReadFile(fileSystem, buildDirectory, NinjaDependencyCheck.ManifestFileName, new Scope(null), read);

        return manifest;
    }

    /// <summary>
    /// How <paramref name="output"/> is built, or <see langword="null"/> where no build line produces
    /// it. Separators compare as one, since <c>ninja -t deps</c> spells a path with forward slashes
    /// where the manifest holds backslashes.
    /// </summary>
    /// <param name="output">The output, relative to the build directory.</param>
    public NinjaEdge? EdgeFor(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return _edges.GetValueOrDefault(Normalize(output));
    }

    /// <summary>A path in one spelling of its separators.</summary>
    internal static string Normalize(string path) => path.Replace('\\', '/');

    private void ReadFile(IFileSystem fileSystem, string buildDirectory, string name, Scope scope, HashSet<string> read)
    {
        var path = Path.GetFullPath(Path.Combine(buildDirectory, name));

        if (!read.Add(path) || !fileSystem.FileExists(path))
        {
            return;
        }

        // What the indented lines that follow a statement bind: its rule's, or its build's.
        Rule? rule = null;
        List<string>? building = null;
        string? buildRule = null;
        string? buildSource = null;
        string? buildDeps = null;

        void Built()
        {
            if (building is null)
            {
                return;
            }

            var deps = buildDeps ?? scope.Find(buildRule!)?.Deps;

            foreach (var output in building)
            {
                _edges[Normalize(output)] = new NinjaEdge(buildRule!, buildSource, deps);
            }

            building = null;
            buildDeps = null;
        }

        foreach (var line in LogicalLines(fileSystem.ReadAllText(path)))
        {
            if (line.TrimStart().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                if (Binding(line) is not ("deps", var value))
                {
                    continue;
                }

                if (rule is not null)
                {
                    rule.Deps = value;
                }
                else if (building is not null)
                {
                    buildDeps = value;
                }

                continue;
            }

            Built();
            rule = null;

            var (keyword, remainder) = Statement(line);

            switch (keyword)
            {
                case "rule":
                    rule = new Rule();
                    scope.Rules[remainder.Trim()] = rule;
                    break;

                case "build":
                    (building, buildRule, buildSource) = BuildLine(remainder);
                    break;

                case "include":
                    ReadFile(fileSystem, buildDirectory, Unescape(remainder.Trim()), scope, read);
                    break;

                case "subninja":
                    ReadFile(fileSystem, buildDirectory, Unescape(remainder.Trim()), new Scope(scope), read);
                    break;
            }
        }

        Built();
    }

    /// <summary>The file's lines, each one ending in an unescaped <c>$</c> joined to the next.</summary>
    internal static IEnumerable<string> LogicalLines(string text)
    {
        var pending = new StringBuilder();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // A continued line starts where its first character that is not a space does.
            var piece = pending.Length > 0 ? line.TrimStart(' ') : line;

            if (ContinuesOnTheNextLine(piece))
            {
                pending.Append(piece, 0, piece.Length - 1);
                continue;
            }

            pending.Append(piece);
            yield return pending.ToString();
            pending.Clear();
        }

        if (pending.Length > 0)
        {
            yield return pending.ToString();
        }
    }

    /// <summary>Whether <paramref name="line"/> ends in a <c>$</c> that escapes the line's end rather than another <c>$</c>.</summary>
    private static bool ContinuesOnTheNextLine(string line)
    {
        var dollars = 0;

        for (var index = line.Length - 1; index >= 0 && line[index] == '$'; index--)
        {
            dollars++;
        }

        return dollars % 2 == 1;
    }

    /// <summary>A statement's keyword and what follows it.</summary>
    private static (string Keyword, string Remainder) Statement(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..]);
    }

    /// <summary>An indented <c>name = value</c>, or <see langword="null"/> for a line that is not one.</summary>
    private static (string Name, string Value)? Binding(string line)
    {
        var equals = line.IndexOf('=', StringComparison.Ordinal);

        return equals < 0 ? null : (line[..equals].Trim(), line[(equals + 1)..].Trim());
    }

    /// <summary>
    /// A build line after <c>build</c>: its explicit outputs, its rule, and its first explicit input -
    /// each path with ninja's escapes undone. Implicit outputs, implicit and order-only inputs and
    /// validations are not what a compile is of, and are left out.
    /// </summary>
    internal static (List<string> Outputs, string Rule, string? Source) BuildLine(string text)
    {
        var outputs = new List<string>();
        var inputs = new List<string>();
        string? rule = null;
        var part = Part.Outputs;
        var token = new StringBuilder();

        void Flush()
        {
            if (token.Length == 0)
            {
                return;
            }

            var value = token.ToString();
            token.Clear();

            switch (part)
            {
                case Part.Outputs when value == "|":
                    part = Part.ImplicitOutputs;
                    break;

                case Part.Outputs:
                    outputs.Add(value);
                    break;

                case Part.Rule:
                    rule = value;
                    part = Part.Inputs;
                    break;

                case Part.Inputs when value is "|" or "||" or "|@":
                    part = Part.Others;
                    break;

                case Part.Inputs:
                    inputs.Add(value);
                    break;
            }
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '$' && index + 1 < text.Length && text[index + 1] is ' ' or ':' or '$')
            {
                token.Append(text[++index]);
            }
            else if (character == ' ')
            {
                Flush();
            }
            else if (character == ':' && part is Part.Outputs or Part.ImplicitOutputs)
            {
                Flush();
                part = Part.Rule;
            }
            else
            {
                token.Append(character);
            }
        }

        Flush();

        return (outputs, rule ?? string.Empty, inputs.Count > 0 ? inputs[0] : null);
    }

    /// <summary>A path an <c>include</c> or <c>subninja</c> names, with ninja's escapes undone.</summary>
    private static string Unescape(string path)
    {
        var unescaped = new StringBuilder(path.Length);

        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] == '$' && index + 1 < path.Length && path[index + 1] is ' ' or ':' or '$')
            {
                index++;
            }

            unescaped.Append(path[index]);
        }

        return unescaped.ToString();
    }

    /// <summary>Where a build line's words go, in the order ninja reads them.</summary>
    private enum Part
    {
        Outputs,
        ImplicitOutputs,
        Rule,
        Inputs,
        Others,
    }

    /// <summary>A rule, and how it records dependencies.</summary>
    private sealed class Rule
    {
        public string? Deps { get; set; }
    }

    /// <summary>The rules a file sees: its own, then those of the file it is a subninja of.</summary>
    private sealed class Scope(Scope? parent)
    {
        public Dictionary<string, Rule> Rules { get; } = new(StringComparer.Ordinal);

        public Rule? Find(string name) => Rules.TryGetValue(name, out var rule) ? rule : parent?.Find(name);
    }
}
