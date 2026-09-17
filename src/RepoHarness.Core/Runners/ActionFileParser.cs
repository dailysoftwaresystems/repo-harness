using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RepoHarness.Core.Runners;

/// <summary>Reads a runner's action file.</summary>
public interface IActionFileParser
{
    /// <summary>
    /// Parses <paramref name="text"/> as the action file at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Where the text came from, quoted in every refusal about it.</param>
    /// <param name="text">The file's contents.</param>
    /// <exception cref="HarnessException">
    /// The file is not usable. Every problem found is reported together.
    /// </exception>
    ActionFile Parse(string path, string text);

    /// <summary>
    /// Reads the action file <paramref name="action"/> names from a runner's actions directory.
    /// </summary>
    /// <param name="actionsDirectory">The directory action directories live in.</param>
    /// <param name="action">
    /// The path a runner's <c>action</c> key gives, as <c>&lt;name&gt;/&lt;name&gt;.yml</c>.
    /// </param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="HarnessException">
    /// The path leaves the actions directory, the file is absent, or it is not usable.
    /// </exception>
    Task<ActionFile> LoadAsync(
        string actionsDirectory,
        string action,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IActionFileParser"/>
/// <remarks>
/// <para>
/// Parsed through YamlDotNet's representation model rather than its serialiser. The serialiser
/// would reach a typed object faster and lose the two things this file needs most: the position of
/// the key that was wrong, and the fact that a key was not recognised at all. A silently ignored
/// key is a rule nobody applied, and the configuration reader elsewhere in this repository refuses
/// one for the same reason.
/// </para>
/// <para>
/// Every problem in a file is collected and reported together. Fixing one error only to be shown
/// the next is a poor way to correct a file, and an action file usually goes wrong in the same way
/// several times over.
/// </para>
/// </remarks>
public sealed class ActionFileParser(
    IFileSystem fileSystem,
    IHarnessOutput output,
    IHostPlatform platform) : IActionFileParser
{
    /// <summary>
    /// How long a declared pattern may take to be compiled or matched. A pattern is author-supplied
    /// and an action file is tracked, so a pathological one is a mistake rather than an attack; the
    /// bound exists so the mistake surfaces as a refusal instead of a run that never starts.
    /// </summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Top-level keys an action file may carry.</summary>
    private static readonly string[] FileKeys = ["name", "description", "inputs", "steps"];

    /// <summary>Keys one declared input may carry.</summary>
    private static readonly string[] InputKeys = ["default", "required", "description"];

    /// <summary>Keys one step may carry.</summary>
    private static readonly string[] StepKeys =
    [
        "name",
        "uses",
        "ref",
        "run",
        "workingDirectory",
        "workingDirectoryRoot",
        "env",
        "successPattern",
        "stallSeconds",
        "continueOnError",
        "watchContention",
        "requireInputsUnmoved",
        "outputs",
        "persist",
    ];

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly IHostPlatform _platform = platform;

    public Task<ActionFile> LoadAsync(
        string actionsDirectory,
        string action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        cancellationToken.ThrowIfCancellationRequested();

        // Checked here as well as when config.json is read, and again after every link is followed.
        // This is the last line of defence, and the file is about to be run: it does not get to
        // assume a caller validated first, because the one caller that did not would be the one
        // that ran a file from somewhere else.
        var path = ActionPath.Resolve(actionsDirectory, action, _fileSystem, _platform.PathComparison);

        _output.Detail("run", $"reading action file '{path}'");

        // The directory as the runner spelled it, so a grouped action keeps every segment above
        // its own. Derived from the configured value rather than from the resolved path, which has
        // followed links and no longer says where in the actions tree the author put it.
        return Task.FromResult(Parse(path, _fileSystem.ReadAllText(path)) with
        {
            Directory = ActionPath.DirectoryOf(action),
        });
    }

    public ActionFile Parse(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        var problems = new List<string>();
        var root = LoadRoot(text, problems);

        if (root is null)
        {
            throw Refuse(path, problems);
        }

        string? name = null;
        string? description = null;
        var inputs = new List<ActionInput>();
        var steps = new List<ActionStep>();
        var sawSteps = false;

        foreach (var (keyNode, valueNode) in root.Children)
        {
            var key = KeyOf(keyNode, problems);

            if (key is null)
            {
                continue;
            }

            switch (key)
            {
                case "name":
                    name = RequireScalar(valueNode, "name", problems);
                    break;

                case "description":
                    description = RequireScalar(valueNode, "description", problems);
                    break;

                case "inputs":
                    inputs.AddRange(ReadInputs(valueNode, problems));
                    break;

                case "steps":
                    sawSteps = true;
                    steps.AddRange(ReadSteps(valueNode, problems));
                    break;

                default:
                    problems.Add(Unknown(keyNode, key, "top-level key", FileKeys));
                    break;
            }
        }

        if (!sawSteps)
        {
            problems.Add("the file declares no 'steps'; an action file that runs nothing is a file "
                + "whose runner reports success having done no work.");
        }

        return problems.Count == 0
            ? new ActionFile(path, name, description, inputs, steps)
            : throw Refuse(path, problems);
    }

    /// <summary>
    /// Whether <paramref name="line"/> can be split without losing arguments.
    /// </summary>
    /// <remarks>
    /// <c>CommandLineParser.SplitCommandLine</c> honours double quotes only and understands no
    /// escape, and an unterminated double quote makes it return everything <em>before</em> the
    /// quote and drop the rest of the line with no error at all. Measured, on System.CommandLine
    /// 2.0.12: <c>dotnet build "unterminated -c Release</c> splits to <c>[dotnet, build]</c>, and
    /// <c>tool --x "a b" "c</c> splits to <c>[tool, --x, a b]</c>. A file that lost its last three
    /// arguments still runs, still exits zero, and still looks like it did the work. So the line is
    /// refused when the file is read instead.
    /// </remarks>
    /// <param name="line">One trimmed line of a <c>run</c> block.</param>
    public static bool HasBalancedQuotes(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Count(character => character == '"') % 2 == 0;
    }

    /// <summary>
    /// The lines of a <c>run</c> block that are program invocations: each trimmed, with blank lines
    /// and comment lines dropped.
    /// </summary>
    /// <remarks>
    /// Trimming is what keeps indentation from changing what runs. A block scalar carries whatever
    /// indentation the author used to keep the YAML readable, and a program name with leading
    /// spaces is not the same program. A line whose first non-space character is <c>#</c> is a
    /// comment; <c>#</c> anywhere else is an ordinary character, because it is ordinary in a
    /// commit range, a colour and a fragment.
    /// </remarks>
    /// <param name="block">The block's text, as the file wrote it.</param>
    public static IReadOnlyList<(string Line, int Offset)> RunLines(string block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var lines = new List<(string, int)>();
        var raw = block.Split('\n');

        for (var index = 0; index < raw.Length; index++)
        {
            var line = raw[index].Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            lines.Add((line, index));
        }

        return lines;
    }

    private static YamlMappingNode? LoadRoot(string text, List<string> problems)
    {
        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException exception)
        {
            problems.Add($"line {exception.Start.Line}, column {exception.Start.Column}: "
                + $"the file is not valid YAML ({exception.Message}).");
            return null;
        }

        if (stream.Documents.Count == 0)
        {
            problems.Add("the file is empty.");
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            problems.Add($"the file holds {stream.Documents.Count} YAML documents; an action file "
                + "holds one, so that nothing has to decide which of them a runner ran.");
            return null;
        }

        if (stream.Documents[0].RootNode is YamlMappingNode mapping)
        {
            return mapping;
        }

        problems.Add(At(stream.Documents[0].RootNode, "the file's top level is not a mapping of keys."));
        return null;
    }

    private static IEnumerable<ActionInput> ReadInputs(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(At(node, "'inputs' is not a mapping of name to declaration."));
            yield break;
        }

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var inputName = KeyOf(keyNode, problems);

            if (inputName is null)
            {
                continue;
            }

            if (valueNode is not YamlMappingNode declaration)
            {
                problems.Add(At(valueNode, $"input '{inputName}' is not a mapping; it declares "
                    + $"'{string.Join("', '", InputKeys)}'."));
                continue;
            }

            string? fallback = null;
            string? inputDescription = null;
            var required = false;

            foreach (var (declarationKeyNode, declarationValueNode) in declaration.Children)
            {
                var key = KeyOf(declarationKeyNode, problems);

                switch (key)
                {
                    case null:
                        break;

                    case "default":
                        fallback = RequireScalar(declarationValueNode, $"inputs.{inputName}.default", problems);
                        break;

                    case "required":
                        required = RequireBoolean(declarationValueNode, $"inputs.{inputName}.required", problems)
                            ?? false;
                        break;

                    case "description":
                        inputDescription = RequireScalar(
                            declarationValueNode,
                            $"inputs.{inputName}.description",
                            problems);
                        break;

                    default:
                        problems.Add(Unknown(declarationKeyNode, key, $"key of input '{inputName}'", InputKeys));
                        break;
                }
            }

            yield return new ActionInput(inputName, fallback, required, inputDescription);
        }
    }

    private static IEnumerable<ActionStep> ReadSteps(YamlNode node, List<string> problems)
    {
        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(At(node, "'steps' is not a sequence."));
            yield break;
        }

        if (sequence.Children.Count == 0)
        {
            problems.Add(At(node, "'steps' is empty; an action file that runs nothing is a file "
                + "whose runner reports success having done no work."));
            yield break;
        }

        foreach (var child in sequence.Children)
        {
            var step = ReadStep(child, problems);

            if (step is not null)
            {
                yield return step;
            }
        }
    }

    private static ActionStep? ReadStep(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(At(node, "a step is not a mapping of keys."));
            return null;
        }

        string? name = null;
        string? uses = null;
        YamlNode? usesNode = null;
        string? reference = null;
        YamlNode? referenceNode = null;
        string? run = null;
        YamlNode? runNode = null;
        string? workingDirectory = null;
        YamlNode? workingDirectoryNode = null;
        string? workingDirectoryKey = null;
        var workingDirectoryRoot = Runners.WorkingDirectoryRoot.Tree;
        var watchContention = false;
        var requireInputsUnmoved = false;
        string? successPattern = null;
        int? stallSeconds = null;
        var continueOnError = false;
        var outputs = (IReadOnlyList<string>)[];
        var persist = false;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = KeyOf(keyNode, problems);

            switch (key)
            {
                case null:
                    break;

                case "name":
                    name = RequireScalar(valueNode, "a step's name", problems);
                    break;

                case "uses":
                    usesNode = valueNode;
                    uses = RequireScalar(valueNode, "a step's uses", problems);
                    break;

                case "ref":
                    referenceNode = valueNode;
                    reference = RequireScalar(valueNode, "a step's ref", problems);
                    break;

                case "run":
                    runNode = valueNode;
                    run = RequireScalar(valueNode, "a step's run", problems);
                    break;

                case "workingDirectory":
                    workingDirectoryNode = valueNode;
                    workingDirectoryKey = key;
                    workingDirectory = ReadWorkingDirectory(valueNode, problems);
                    break;

                case "watchContention":
                    watchContention = ReadFlag(valueNode, "watchContention", problems);
                    break;

                case "requireInputsUnmoved":
                    requireInputsUnmoved = ReadFlag(valueNode, "requireInputsUnmoved", problems);
                    break;

                case "outputs":
                    outputs = ReadOutputs(valueNode, problems);
                    break;

                case "persist":
                    persist = ReadFlag(valueNode, "persist", problems);
                    break;

                case "workingDirectoryRoot":
                    workingDirectoryNode ??= valueNode;
                    workingDirectoryKey ??= key;
                    workingDirectoryRoot = ReadWorkingDirectoryRoot(valueNode, problems);
                    break;

                case "env":
                    ReadEnv(valueNode, env, problems);
                    break;

                case "successPattern":
                    successPattern = ReadPattern(valueNode, problems);
                    break;

                case "stallSeconds":
                    stallSeconds = ReadStallSeconds(valueNode, problems);
                    break;

                case "continueOnError":
                    continueOnError = RequireBoolean(valueNode, "a step's continueOnError", problems) ?? false;
                    break;

                default:
                    problems.Add(Unknown(keyNode, key, "step key", StepKeys));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(At(node, "a step has no 'name'; a step names its own log file, and two "
                + "unnamed steps would write one file, the second overwriting the first's evidence."));
            name = string.Empty;
        }

        var action = ReadUses(usesNode, uses, run is not null, node, problems);
        var commands = run is null
            ? []
            : ReadCommands(runNode ?? node, run, problems);

        if (action != PredefinedAction.Checkout && reference is not null)
        {
            problems.Add(At(referenceNode ?? node, $"'ref' applies only to '{PredefinedActions.Checkout}'."));
        }

        // A predefined action is performed by the harness, not started as a child process, so it has
        // no working directory to run in. Accepted silently, these would be a rule nobody applied:
        // the file would read as though the action ran somewhere chosen, and it never did.
        if (action != PredefinedAction.None && workingDirectoryNode is not null)
        {
            problems.Add(At(
                workingDirectoryNode,
                $"'{workingDirectoryKey}' applies only to a step's 'run' block; "
                + $"'{PredefinedActions.Spell(action)}' is performed by the harness rather than run "
                + "as a program, so it has no working directory."));
        }

        return new ActionStep
        {
            Name = name,
            Uses = action,
            Reference = reference,
            Commands = commands,
            WorkingDirectory = workingDirectory,
            WorkingDirectoryRoot = workingDirectoryRoot,
            WatchContention = watchContention,
            RequireInputsUnmoved = requireInputsUnmoved,
            Env = env,
            SuccessPattern = successPattern,
            StallSeconds = stallSeconds,
            ContinueOnError = continueOnError,
            Outputs = outputs,
            Persist = persist,
        };
    }

    /// <summary>
    /// The paths a step declares it produces, each relative to its own directory under the action's
    /// build directory.
    /// </summary>
    /// <param name="node">The <c>outputs</c> value.</param>
    /// <param name="problems">Where problems are collected.</param>
    /// <remarks>
    /// Relative, and refused otherwise. An output is something this step wrote in the directory the
    /// harness gave it; a path that climbs out of that directory names a file the harness did not
    /// create, cannot clean up, and would move somewhere else when the step asked to persist it.
    /// </remarks>
    private static IReadOnlyList<string> ReadOutputs(YamlNode node, List<string> problems)
    {
        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(At(node, "a step's 'outputs' is a list of paths the step produces."));
            return [];
        }

        var outputs = new List<string>();

        foreach (var item in sequence.Children)
        {
            if (RequireScalar(item, "a step's output", problems) is not { Length: > 0 } path)
            {
                continue;
            }

            var normalised = path.Replace('\\', '/').Trim();

            if (Path.IsPathRooted(normalised)
                || normalised.StartsWith('/')
                || normalised.Split('/').Any(segment => segment is ".." or "." or ""))
            {
                problems.Add(At(
                    item,
                    $"a step's output '{path}' is not a path inside the step's own directory; an "
                    + "output is something the step wrote where the harness put it, without '.' or "
                    + "'..' and never rooted."));
                continue;
            }

            outputs.Add(normalised);
        }

        return outputs;
    }

    private static PredefinedAction ReadUses(
        YamlNode? usesNode,
        string? uses,
        bool hasRun,
        YamlNode step,
        List<string> problems)
    {
        // Both, or neither, and nothing can say what the step was meant to do. A step that declared
        // both would have to pick one silently, and the file would then read as one thing and run
        // as another.
        if (uses is not null && hasRun)
        {
            problems.Add(At(step, "a step declares both 'uses' and 'run'; it does one or the other."));
            return PredefinedAction.None;
        }

        if (uses is null)
        {
            if (!hasRun)
            {
                problems.Add(At(step, "a step declares neither 'uses' nor 'run', so it does nothing."));
            }

            return PredefinedAction.None;
        }

        var action = PredefinedActions.Parse(uses);

        if (action is not null)
        {
            return action.Value;
        }

        problems.Add(At(
            usesNode ?? step,
            $"'{uses}' is not a predefined action. Available: '{string.Join("', '", PredefinedActions.All)}'."));

        return PredefinedAction.None;
    }

    private static IReadOnlyList<ActionCommand> ReadCommands(YamlNode node, string run, List<string> problems)
    {
        var lines = RunLines(run);

        if (lines.Count == 0)
        {
            problems.Add(At(node, "a step's 'run' holds no command; every line is blank or a comment."));
            return [];
        }

        var commands = new List<ActionCommand>();

        // A block scalar's own mark sits on the '|' indicator, so its first content line is the
        // next one; a plain or quoted scalar's content starts on the mark's own line. Without the
        // distinction every refusal about a run block would point one line above the offending
        // command, which is the line that is already correct. YamlDotNet counts lines as a long;
        // the number only ever points at a line in a refusal, so an int holds every file anyone edits.
        var firstLine = (int)node.Start.Line
            + (node is YamlScalarNode { Style: ScalarStyle.Literal or ScalarStyle.Folded } ? 1 : 0);

        foreach (var (line, offset) in lines)
        {
            var lineNumber = firstLine + offset;

            if (!HasBalancedQuotes(line))
            {
                problems.Add($"line {lineNumber}: '{line}' has an unbalanced double quote. "
                    + "The command splitter drops everything after an unterminated quote without a "
                    + "word, so this line would run with its remaining arguments missing.");
                continue;
            }

            var arguments = CommandLineParser.SplitCommandLine(line).ToArray();

            if (arguments.Length == 0 || string.IsNullOrEmpty(arguments[0]))
            {
                problems.Add($"line {lineNumber}: '{line}' names no program.");
                continue;
            }

            commands.Add(new ActionCommand(line, lineNumber, arguments));
        }

        return commands;
    }

    private static void ReadEnv(YamlNode node, Dictionary<string, string> env, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(At(node, "a step's 'env' is not a mapping of name to value."));
            return;
        }

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = KeyOf(keyNode, problems);

            if (key is null)
            {
                continue;
            }

            var value = RequireScalar(valueNode, $"env.{key}", problems);

            if (value is null)
            {
                continue;
            }

            if (!env.TryAdd(key, value))
            {
                problems.Add(At(keyNode, $"'env.{key}' is set twice; nothing can say which value "
                    + "the step would have run with."));
            }
        }
    }

    /// <summary>
    /// A step's <c>workingDirectory</c>, refused unless it stays under the root it is resolved
    /// against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both rules matter, and neither is theoretical. <c>Path.Combine</c> discards everything before
    /// a rooted second argument, so <c>workingDirectoryRoot: action</c> with an absolute
    /// <c>workingDirectory</c> would silently run at that absolute path, having named a root it
    /// never used; and <c>..</c> climbs out of any root by arithmetic the combine performs for it.
    /// A step that declares a root and then leaves it has declared nothing.
    /// </para>
    /// <para>
    /// Refused when the file is read, as a runner's action path is, rather than checked where the
    /// directory is finally composed: there the tree root is known but the file is not, and a
    /// refusal naming neither the file nor the line would be one nobody could act on.
    /// </para>
    /// </remarks>
    private static string? ReadWorkingDirectory(YamlNode node, List<string> problems)
    {
        var path = RequireScalar(node, "a step's workingDirectory", problems);

        if (path is null)
        {
            return null;
        }

        // Rooted by this platform's rules and by the other's: a drive letter written on Linux is not
        // rooted there, and a step must not mean two different directories on two machines.
        if (Path.IsPathRooted(path) || path.Length == 0 || path[0] is '/' or '\\'
            || (path.Length >= 2 && path[1] == ':'))
        {
            problems.Add(At(node, $"a step's 'workingDirectory' is '{path}', which is an absolute "
                + "path; it names a directory under the step's own root, so that a step runs where "
                + "its 'workingDirectoryRoot' says it does."));
            return null;
        }

        if (path.Split('/', '\\').Any(segment => segment == ".."))
        {
            problems.Add(At(node, $"a step's 'workingDirectory' is '{path}', which climbs out of "
                + "the step's own root with '..'; it names a directory under that root, so that a "
                + "step runs where its 'workingDirectoryRoot' says it does."));
            return null;
        }

        return path;
    }

    /// <summary>
    /// A step's yes-or-no key, refused rather than guessed at when it says something else.
    /// </summary>
    /// <param name="node">The value as written.</param>
    /// <param name="key">The key, which a refusal names.</param>
    /// <param name="problems">Where problems are collected.</param>
    /// <remarks>
    /// Anything that is not exactly true or false is refused, rather than read as false. These keys
    /// turn guards on, so a misspelling read as "off" is a step that looks guarded in the file and
    /// is not — which is the failure the guards exist to make impossible.
    /// </remarks>
    private static bool ReadFlag(YamlNode node, string key, List<string> problems)
    {
        var value = RequireScalar(node, $"a step's {key}", problems);

        if (value is null)
        {
            return false;
        }

        if (bool.TryParse(value, out var flag))
        {
            return flag;
        }

        problems.Add(At(node, $"'{value}' is not a value for {key}. Available: 'true', 'false'."));
        return false;
    }

    private static WorkingDirectoryRoot ReadWorkingDirectoryRoot(YamlNode node, List<string> problems)
    {
        var value = RequireScalar(node, "a step's workingDirectoryRoot", problems);

        if (value is null)
        {
            return Runners.WorkingDirectoryRoot.Tree;
        }

        if (WorkingDirectoryRoots.Parse(value) is { } root)
        {
            return root;
        }

        problems.Add(At(
            node,
            $"'{value}' is not a working directory root. Available: "
            + $"'{string.Join("', '", WorkingDirectoryRoots.All)}'."));

        return Runners.WorkingDirectoryRoot.Tree;
    }

    private static string? ReadPattern(YamlNode node, List<string> problems)
    {
        var pattern = RequireScalar(node, "a step's successPattern", problems);

        if (pattern is null)
        {
            return null;
        }

        if (pattern.Length == 0)
        {
            // An empty pattern matches everything, so a step declaring one would be witnessed by
            // nothing while appearing to be witnessed. Refused where the file is read, as the
            // phase runner refuses one read from config.json.
            problems.Add(At(node, "a step's 'successPattern' is empty; an empty pattern matches any "
                + "output, which is a witness that proves nothing."));
            return null;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.None, PatternTimeout);
        }
        catch (ArgumentException exception)
        {
            problems.Add(At(node, $"'successPattern' does not compile: {exception.Message}"));
            return null;
        }

        return pattern;
    }

    private static int? ReadStallSeconds(YamlNode node, List<string> problems)
    {
        var text = RequireScalar(node, "a step's stallSeconds", problems);

        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            problems.Add(At(node, $"'stallSeconds' is '{text}', which is not a whole number of seconds."));
            return null;
        }

        if (seconds < 0)
        {
            problems.Add(At(node, $"'stallSeconds' is {seconds}; a stall bound is never negative, "
                + "and zero already means no bound."));
            return null;
        }

        return seconds;
    }

    private static string? KeyOf(YamlNode node, List<string> problems)
    {
        if (node is YamlScalarNode { Value: { } value })
        {
            return value;
        }

        problems.Add(At(node, "a key is not a plain name."));
        return null;
    }

    private static string? RequireScalar(YamlNode node, string what, List<string> problems)
    {
        if (node is YamlScalarNode { Value: { } value })
        {
            return value;
        }

        problems.Add(At(node, $"'{what}' is not a single value."));
        return null;
    }

    private static bool? RequireBoolean(YamlNode node, string what, List<string> problems)
    {
        var text = RequireScalar(node, what, problems);

        if (text is null)
        {
            return null;
        }

        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        problems.Add(At(node, $"'{what}' is '{text}', which is not true or false."));
        return null;
    }

    private static string Unknown(YamlNode node, string key, string what, IReadOnlyList<string> known)
        => At(node, $"'{key}' is not a {what}. Known: '{string.Join("', '", known)}'.");

    private static string At(YamlNode node, string message)
        => $"line {node.Start.Line}, column {node.Start.Column}: {message}";

    private static HarnessException Refuse(string path, IReadOnlyList<string> problems)
    {
        var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

        return new HarnessException(
            HarnessExit.ConfigInvalid,
            $"'{path}' has {problems.Count} problem(s):{Environment.NewLine}{detail}");
    }
}
