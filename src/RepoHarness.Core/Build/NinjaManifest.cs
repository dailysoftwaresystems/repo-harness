using System.Text;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Build;

/// <summary>How a ninja manifest says one output is built.</summary>
/// <param name="Rule">The rule that builds it.</param>
/// <param name="Outputs">Every path its build line produces, the implicit ones too.</param>
/// <param name="Inputs">
/// Every path a change to which rebuilds it: its explicit inputs, then its implicit ones. What it is
/// only ordered after, or validated by, never rebuilds it, and is left out.
/// </param>
/// <param name="Source">Its first explicit input, the source a compile rule compiles; <see langword="null"/> when it has none.</param>
/// <param name="Deps">
/// How the edge records its dependencies - <c>gcc</c> or <c>msvc</c> - as ninja reads its <c>deps</c>:
/// the build line's own binding, else its rule's, else its file's; <see langword="null"/> when none says.
/// </param>
/// <param name="Command">
/// The command it runs, as ninja evaluates it for this build line, followed by what its response file
/// holds where its rule writes one; empty for a phony.
/// </param>
public sealed record NinjaEdge(
    string Rule,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Inputs,
    string? Source,
    string? Deps,
    string Command);

/// <summary>
/// What a ninja build directory's manifest declares about how each output is built, read the way
/// ninja reads it: <c>build.ninja</c> and every file it includes, with ninja's escapes undone and its
/// variables evaluated.
/// </summary>
/// <remarks>
/// <para>
/// Across the files, because CMake writes its rules to <c>CMakeFiles/rules.ninja</c> and only includes
/// that file from <c>build.ninja</c>: read alone, <c>build.ninja</c> never says how any object records
/// its dependencies. A file an <c>include</c> names shares the scope it is included into; one a
/// <c>subninja</c> names sees the rules and variables declared before it, and its own are its own.
/// Every path is read from the build directory, as ninja run there reads it, and a file read once is
/// not read again.
/// </para>
/// <para>
/// With the escapes undone, because a path in a build line is spelled with them: CMake names a
/// source by its absolute path, so on Windows its drive's colon arrives as <c>$:</c> and a space in
/// it as <c>$ </c>, and read raw <c>C$:\src\main.c</c> is no file at all. A line ending in an
/// unescaped <c>$</c> continues on the next.
/// </para>
/// <para>
/// With the variables evaluated when ninja evaluates them: a path, a file's own binding and a build
/// line's binding as they are read, in the file's scope; a rule's binding - its command among them -
/// for each build line once every file is read, where <c>$in</c> and <c>$out</c> are the line's own
/// explicit paths and the line's own binding outranks its rule's, which outranks its file's. So the
/// options a compile was given are read from the command it ran: CMake's rule adds <c>/TP</c> to
/// every C++ compile, and each build line's <c>FLAGS</c> the header it force-includes with <c>/FI</c>.
/// </para>
/// </remarks>
public sealed class NinjaManifest
{
    private readonly Dictionary<string, NinjaEdge> _edges = new(StringComparer.Ordinal);

    /// <summary>The build lines read, each with what it is evaluated with once every file is read.</summary>
    private readonly List<(BuildStatement Line, Rule? Rule, Dictionary<string, string> Bindings, Scope Scope)> _read = [];

    private NinjaManifest()
    {
    }

    /// <summary>Reads the manifest <paramref name="buildDirectory"/> holds.</summary>
    /// <param name="fileSystem">Reads the files.</param>
    /// <param name="buildDirectory">The directory ninja runs in, whose <c>build.ninja</c> is read first.</param>
    /// <remarks>
    /// A file the manifest names that is not there, or cannot be read, is passed over: what it would
    /// have declared is simply not known, and no object it would have said how to build is excused.
    /// </remarks>
    public static NinjaManifest Read(IFileSystem fileSystem, string buildDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        var manifest = new NinjaManifest();
        var read = new HashSet<string>(StringComparer.Ordinal);

        manifest.ReadFile(fileSystem, buildDirectory, NinjaDependencyCheck.ManifestFileName, new Scope(null), read);

        // A rule's bindings are evaluated when the build runs, so against every file's final values.
        foreach (var (line, rule, bindings, scope) in manifest._read)
        {
            var edge = Edge(line, rule, bindings, scope);

            foreach (var output in line.Outputs)
            {
                manifest._edges[Normalize(output)] = edge;
            }
        }

        return manifest;
    }

    /// <summary>
    /// How <paramref name="output"/> is built, or <see langword="null"/> where no build line produces
    /// it. Two spellings of one path find the same build line, as ninja reads them as one node.
    /// </summary>
    /// <param name="output">The output, relative to the build directory.</param>
    public NinjaEdge? EdgeFor(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return _edges.GetValueOrDefault(Normalize(output));
    }

    /// <summary>
    /// A path as ninja canonicalizes it: one kind of separator - <c>ninja -t deps</c> spells with
    /// forward slashes what the manifest holds with backslashes - and no <c>.</c> component, doubled
    /// separator or <c>..</c> that follows a name, save the pair a network path opens with, which
    /// ninja on Windows keeps. CMake declares a precompiled header's <c>.pch</c> as
    /// <c>CMakeFiles\t.dir\.\\cmake_pch.c.pch</c>, and every unit built from it names it without them.
    /// </summary>
    internal static string Normalize(string path)
    {
        var components = new List<string>();

        foreach (var component in path.Replace('\\', '/').Split('/'))
        {
            if (component is "" or ".")
            {
                continue;
            }

            if (component == ".." && components.Count > 0 && components[^1] != "..")
            {
                components.RemoveAt(components.Count - 1);
                continue;
            }

            components.Add(component);
        }

        var canonical = string.Join('/', components);

        return path.Length > 1 && path[0] is '/' or '\\' && path[1] is '/' or '\\' ? "//" + canonical
            : path.StartsWith('/') || path.StartsWith('\\') ? "/" + canonical
            : canonical;
    }

    /// <summary>
    /// <paramref name="text"/> as ninja evaluates it: <c>$$</c>, <c>$ </c> and <c>$:</c> undone, and
    /// <c>$name</c> and <c>${name}</c> replaced by what <paramref name="variable"/> gives for the name.
    /// </summary>
    internal static string Evaluate(string text, Func<string, string> variable)
    {
        var evaluated = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '$' || index + 1 == text.Length)
            {
                evaluated.Append(text[index]);
                continue;
            }

            var next = text[++index];
            var end = next == '{' ? text.IndexOf('}', index + 1) : -1;

            if (next is '$' or ' ' or ':')
            {
                evaluated.Append(next);
            }
            else if (end > 0)
            {
                evaluated.Append(variable(text[(index + 1)..end]));
                index = end;
            }
            else if (IsVariableCharacter(next))
            {
                var start = index;

                while (index + 1 < text.Length && IsVariableCharacter(text[index + 1]))
                {
                    index++;
                }

                evaluated.Append(variable(text[start..(index + 1)]));
            }
            else
            {
                evaluated.Append('$').Append(next);
            }
        }

        return evaluated.ToString();
    }

    /// <summary>Whether <paramref name="character"/> may be part of a name ninja reads after a bare <c>$</c>.</summary>
    private static bool IsVariableCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

    /// <summary>
    /// The edge <paramref name="line"/> declares, its variables looked up as ninja looks them up for it:
    /// <c>$in</c> and <c>$out</c> the line's own explicit paths, then the line's bindings, then its
    /// rule's - evaluated for it, a rule binding naming itself evaluating to nothing - then its file's.
    /// </summary>
    private static NinjaEdge Edge(BuildStatement line, Rule? rule, Dictionary<string, string> bindings, Scope scope)
    {
        var evaluating = new HashSet<string>(StringComparer.Ordinal);

        string Variable(string name)
        {
            switch (name)
            {
                case "in":
                    return string.Join(' ', line.Inputs.Take(line.ExplicitInputs).Select(Quoted));

                case "in_newline":
                    return string.Join('\n', line.Inputs.Take(line.ExplicitInputs));

                case "out":
                    return string.Join(' ', line.Outputs.Take(line.ExplicitOutputs).Select(Quoted));
            }

            if (bindings.TryGetValue(name, out var bound))
            {
                return bound;
            }

            if (rule is not null && rule.Bindings.TryGetValue(name, out var unevaluated))
            {
                if (!evaluating.Add(name))
                {
                    return string.Empty;
                }

                try
                {
                    return Evaluate(unevaluated, Variable);
                }
                finally
                {
                    evaluating.Remove(name);
                }
            }

            return scope.Variable(name);
        }

        var deps = Variable("deps");
        var command = Variable("command");
        var response = Variable("rspfile_content");

        return new NinjaEdge(
            line.Rule,
            line.Outputs,
            line.Inputs,
            line.Source,
            deps.Length > 0 ? deps : null,
            response.Length > 0 ? command + " " + response : command);
    }

    /// <summary>A path as ninja puts it in a command for <c>$in</c> or <c>$out</c> on Windows: quoted where a space or a quote would split it.</summary>
    private static string Quoted(string path)
        => path.Any(character => character is ' ' or '\t' or '"')
            ? "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : path;

    private void ReadFile(IFileSystem fileSystem, string buildDirectory, string name, Scope scope, HashSet<string> read)
    {
        var path = Path.GetFullPath(Path.Combine(buildDirectory, name));

        if (!read.Add(path) || !fileSystem.FileExists(path))
        {
            return;
        }

        // What the indented lines that follow a statement bind: its rule's, or its build's.
        Rule? rule = null;
        Dictionary<string, string>? building = null;

        string text;

        try
        {
            text = fileSystem.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var line in LogicalLines(text))
        {
            if (line.TrimStart().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                if (Binding(line) is not { } binding)
                {
                    continue;
                }

                // A rule's bindings are evaluated for each build line; a build line's, as it is read.
                if (rule is not null)
                {
                    rule.Bindings[binding.Name] = binding.Value;
                }
                else if (building is not null)
                {
                    building[binding.Name] = scope.Evaluate(binding.Value);
                }

                continue;
            }

            rule = null;
            building = null;

            var (keyword, remainder) = Statement(line);

            switch (keyword)
            {
                case "rule":
                    rule = new Rule();
                    scope.Rules[remainder.Trim()] = rule;
                    break;

                case "build":
                    // Its rule is the one declared by now, whatever a subninja declares later.
                    var statement = BuildLine(remainder, scope.Variable);
                    building = new Dictionary<string, string>(StringComparer.Ordinal);
                    _read.Add((statement, scope.Find(statement.Rule), building, scope));
                    break;

                case "include":
                    ReadFile(fileSystem, buildDirectory, scope.Evaluate(remainder.Trim()), scope, read);
                    break;

                case "subninja":
                    ReadFile(fileSystem, buildDirectory, scope.Evaluate(remainder.Trim()), new Scope(scope), read);
                    break;

                case "default" or "pool":
                    break;

                default:
                    if (Binding(line) is { } variable)
                    {
                        scope.Variables[variable.Name] = scope.Evaluate(variable.Value);
                    }

                    break;
            }
        }
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

    /// <summary>
    /// A <c>name = value</c>, or <see langword="null"/> for a line that is not one. The value starts
    /// where the first character after the <c>=</c> that is not a space does, and keeps every one it
    /// ends with, as ninja reads it.
    /// </summary>
    private static (string Name, string Value)? Binding(string line)
    {
        var equals = line.IndexOf('=', StringComparison.Ordinal);

        return equals < 0 ? null : (line[..equals].Trim(), line[(equals + 1)..].TrimStart(' ', '\t'));
    }

    /// <summary>
    /// A build line after <c>build</c>, each path with ninja's escapes undone and its variables
    /// evaluated by <paramref name="variable"/>: what it produces, its rule, and what rebuilds it. A
    /// <c>|</c>, <c>||</c> or <c>|@</c> ends a path as a space does, as ninja reads it, spaced or not;
    /// what follows <c>||</c> or <c>|@</c> is only ordered before it, or validates it, and never
    /// rebuilds it, so it is left out.
    /// </summary>
    internal static BuildStatement BuildLine(string text, Func<string, string>? variable = null)
    {
        variable ??= _ => string.Empty;

        var outputs = new List<string>();
        var inputs = new List<string>();
        var explicitOutputs = 0;
        var explicitInputs = 0;
        string? rule = null;
        var part = Part.Outputs;
        var token = new StringBuilder();

        void Flush()
        {
            if (token.Length == 0)
            {
                return;
            }

            var written = token.ToString();
            token.Clear();

            if (part == Part.Rule)
            {
                rule = written;
                part = Part.Inputs;
                return;
            }

            var path = Evaluate(written, variable);

            switch (part)
            {
                case Part.Outputs:
                    outputs.Add(path);
                    explicitOutputs++;
                    break;

                case Part.ImplicitOutputs:
                    outputs.Add(path);
                    break;

                case Part.Inputs:
                    inputs.Add(path);
                    explicitInputs++;
                    break;

                case Part.ImplicitInputs:
                    inputs.Add(path);
                    break;
            }
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '$' && index + 1 < text.Length)
            {
                // An escape or a variable stays as written, to be undone when the path is evaluated.
                token.Append(character).Append(text[++index]);
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
            else if (character == '|')
            {
                Flush();

                var separator = index + 1 < text.Length && text[index + 1] is '|' or '@' ? "|" + text[++index] : "|";

                part = (part, separator) switch
                {
                    (Part.Outputs, "|") => Part.ImplicitOutputs,
                    (Part.Inputs, "|") => Part.ImplicitInputs,
                    (Part.Inputs or Part.ImplicitInputs, _) => Part.Others,
                    _ => part,
                };
            }
            else
            {
                token.Append(character);
            }
        }

        Flush();

        return new BuildStatement(outputs, explicitOutputs, rule ?? string.Empty, inputs, explicitInputs);
    }

    /// <summary>A build line as <see cref="BuildLine"/> reads it; see <see cref="NinjaEdge"/> for its parts.</summary>
    /// <param name="Outputs">What it produces: the explicit outputs, then the implicit ones.</param>
    /// <param name="ExplicitOutputs">How many of <paramref name="Outputs"/> are explicit: <c>$out</c>'s.</param>
    /// <param name="Rule">Its rule.</param>
    /// <param name="Inputs">What rebuilds it: the explicit inputs, then the implicit ones.</param>
    /// <param name="ExplicitInputs">How many of <paramref name="Inputs"/> are explicit: <c>$in</c>'s.</param>
    internal sealed record BuildStatement(
        IReadOnlyList<string> Outputs,
        int ExplicitOutputs,
        string Rule,
        IReadOnlyList<string> Inputs,
        int ExplicitInputs)
    {
        /// <summary>Its first explicit input, or <see langword="null"/> where it has none.</summary>
        public string? Source => ExplicitInputs > 0 ? Inputs[0] : null;
    }

    /// <summary>Where a build line's words go, in the order ninja reads them.</summary>
    private enum Part
    {
        Outputs,
        ImplicitOutputs,
        Rule,
        Inputs,
        ImplicitInputs,
        Others,
    }

    /// <summary>A rule's bindings, as written: each is evaluated for the build line it builds.</summary>
    private sealed class Rule
    {
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The rules and variables a file sees: its own, then those of the file it is a subninja of.</summary>
    private sealed class Scope(Scope? parent)
    {
        public Dictionary<string, Rule> Rules { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        public Rule? Find(string name) => Rules.TryGetValue(name, out var rule) ? rule : parent?.Find(name);

        /// <summary>What <paramref name="name"/> is bound to here, or where this file is read from; empty where nothing binds it.</summary>
        public string Variable(string name)
            => Variables.TryGetValue(name, out var value) ? value : parent?.Variable(name) ?? string.Empty;

        /// <summary><paramref name="text"/> evaluated in this scope.</summary>
        public string Evaluate(string text) => NinjaManifest.Evaluate(text, Variable);
    }
}
