using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using RepoHarness.Core.Execution;
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
        "runOn",
        "env",
        "successPattern",
        "stallSeconds",
        "continueOnError",
        "watchContention",
        "requireInputsUnmoved",
        "outputs",
        "persist",
        "manual",
        "needs",
        "inputs",
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
                    inputs.AddRange(ReadInputs(valueNode, "inputs", problems));
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

        foreach (var step in steps)
        {
            foreach (var input in step.Inputs.Where(input => inputs.Any(declared => string.Equals(declared.Name, input.Name, StringComparison.Ordinal))))
            {
                problems.Add($"step '{step.Name}' declares input '{input.Name}', which the action declares too: "
                    + $"{CommandLineInputs.Option} {input.Name}=... would give both one value, and a run line naming "
                    + "it could only mean one of them. Name it once: under the action's inputs for every step, or "
                    + "under the step's for that step alone.");
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

    /// <param name="node">The <c>inputs</c> mapping.</param>
    /// <param name="owner">
    /// Whose inputs these are, as a problem names them: <c>inputs</c> for the action's own, or a step's,
    /// so that a problem in one step's inputs is never read as one in the action's.
    /// </param>
    /// <param name="problems">Where problems are collected.</param>
    private static IEnumerable<ActionInput> ReadInputs(YamlNode node, string owner, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(At(node, $"'{owner}' is not a mapping of name to declaration."));
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
                problems.Add(At(valueNode, $"{Named(owner, inputName)} is not a mapping; it declares "
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
                        fallback = RequireScalar(declarationValueNode, $"{owner}.{inputName}.default", problems);
                        break;

                    case "required":
                        required = RequireBoolean(declarationValueNode, $"{owner}.{inputName}.required", problems)
                            ?? false;
                        break;

                    case "description":
                        inputDescription = RequireScalar(
                            declarationValueNode,
                            $"{owner}.{inputName}.description",
                            problems);
                        break;

                    default:
                        problems.Add(Unknown(declarationKeyNode, key, $"key of {Named(owner, inputName)}", InputKeys));
                        break;
                }
            }

            RefuseShadowedInput(keyNode, inputName, problems);

            yield return new ActionInput(inputName, fallback, required, inputDescription);
        }
    }

    /// <summary>How a problem names one declared input: the action's as it always has, a step's under its step.</summary>
    private static string Named(string owner, string inputName)
        => owner == "inputs" ? $"input '{inputName}'" : $"{owner}.{inputName}";

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

        // The steps read so far, which a step's 'needs' may name: steps run in the order declared, so
        // a step can only need one that has already run by the time it starts.
        var earlier = new List<ActionStep>();

        foreach (var child in sequence.Children)
        {
            var step = ReadStep(child, earlier, problems);

            if (step is null)
            {
                continue;
            }

            // Steps of one name are one step to everything that names it - a run, a runner's steps,
            // another step's needs - which a leg's operating system then narrows to the one it runs.
            // One manual and one not would have a run that names no step run the one and not the
            // other, and '--manual-step' could not say which it meant.
            if (earlier.FirstOrDefault(declared => string.Equals(declared.Name, step.Name, StringComparison.Ordinal) && declared.Manual != step.Manual) is { } other)
            {
                problems.Add(At(child, $"step '{step.Name}' is {(step.Manual ? "manual" : "not manual")}, and a step declared before it "
                    + $"by that name is {(other.Manual ? "manual" : "not")}: a run that names no step would run one of them and "
                    + "not the other. Give them names of their own, or make both manual or neither."));
            }

            earlier.Add(step);
            yield return step;
        }
    }

    private static ActionStep? ReadStep(YamlNode node, IReadOnlyList<ActionStep> earlier, List<string> problems)
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
        var runOn = (IReadOnlyList<string>)[];
        var watchContention = false;
        var requireInputsUnmoved = false;
        string? successPattern = null;
        int? stallSeconds = null;
        var continueOnError = false;
        var outputs = (IReadOnlyList<string>)[];
        var persist = false;
        var manual = false;
        YamlNode? needsNode = null;
        YamlNode? inputsNode = null;
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

                case "manual":
                    manual = ReadFlag(valueNode, "manual", problems);
                    break;

                // Read once the step's name is known, which a problem in either names.
                case "needs":
                    needsNode = valueNode;
                    break;

                case "inputs":
                    inputsNode = valueNode;
                    break;

                case "workingDirectoryRoot":
                    workingDirectoryNode ??= valueNode;
                    workingDirectoryKey ??= key;
                    workingDirectoryRoot = ReadWorkingDirectoryRoot(valueNode, problems);
                    break;

                case "runOn":
                    runOn = ReadRunOn(valueNode, problems);
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
        else if (RunSegments.FileNameFor(name).Trim('.', ' ') is { Length: 0 })
        {
            // A step's name becomes a directory under the action's build directory as well as a log
            // file's name. Everything a path cannot carry is already replaced, but a name that is
            // only dots survives that and means 'the directory above' to every file system there is.
            problems.Add(At(node, $"a step named '{name}' has no name a directory can carry; a step "
                + "writes into a directory of its own, and this one would name the directory above "
                + "it."));
        }

        var action = ReadUses(usesNode, uses, run is not null, node, problems);
        var commands = run is null
            ? []
            : ReadCommands(runNode ?? node, run, problems);

        var needs = needsNode is null ? [] : ReadNeeds(needsNode, name, manual, runOn, earlier, problems);
        IReadOnlyList<ActionInput> stepInputs = inputsNode is null ? [] : [.. ReadInputs(inputsNode, $"step '{name}' inputs", problems)];

        // A predefined action is performed by the harness to settle what the steps after it read: it
        // starts no program, so it has nothing of its own to read and no output to witness. A step
        // that must have it first names it under 'needs'.
        if (action != PredefinedAction.None && manual)
        {
            problems.Add(At(node, $"step '{name}' is '{PredefinedActions.Spell(action)}', which cannot be manual: the "
                + "harness performs it to settle what later steps read. A manual step that must have it first "
                + "names it under 'needs'."));
        }

        if (action != PredefinedAction.None && inputsNode is not null)
        {
            problems.Add(At(inputsNode, $"'inputs' applies only to a step's 'run' block; '{PredefinedActions.Spell(action)}' "
                + "is performed by the harness rather than run as a program, so it reads none of its own."));
        }

        // Witnessed like any other step, and more to the point than most: a step that runs only when a
        // run names it is the one whose green line is read as having done that work, so it may not pass
        // on an exit code alone.
        if (action == PredefinedAction.None && manual && successPattern is null)
        {
            problems.Add(At(node, $"step '{name}' is manual and declares no 'successPattern'. A manual step is "
                + "witnessed like any other: it runs only when a run names it, and a line of its own output is "
                + "what says it did the work it was named for."));
        }

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
            RunOn = runOn,
            WatchContention = watchContention,
            RequireInputsUnmoved = requireInputsUnmoved,
            Env = env,
            SuccessPattern = successPattern,
            StallSeconds = stallSeconds,
            ContinueOnError = continueOnError,
            Outputs = outputs,
            Persist = persist,
            Manual = manual,
            Needs = needs,
            Inputs = stepInputs,
        };
    }

    /// <summary>
    /// The steps <paramref name="node"/> says <paramref name="name"/> needs, each one declared before it.
    /// </summary>
    /// <param name="node">The step's <c>needs</c>.</param>
    /// <param name="name">The step, as a problem names it.</param>
    /// <param name="manual">Whether the step is manual.</param>
    /// <param name="runOn">The operating systems the step runs on; empty, every one.</param>
    /// <param name="earlier">Every step declared before it.</param>
    /// <param name="problems">Where problems are collected.</param>
    private static IReadOnlyList<string> ReadNeeds(
        YamlNode node,
        string name,
        bool manual,
        IReadOnlyList<string> runOn,
        IReadOnlyList<ActionStep> earlier,
        List<string> problems)
    {
        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(At(node, $"step '{name}' 'needs' is a list of the steps declared before it that run first."));
            return [];
        }

        if (sequence.Children.Count == 0)
        {
            problems.Add(At(node, $"step '{name}' 'needs' names no step. Leave the key out for a step that needs none."));
            return [];
        }

        var before = earlier.Select(step => step.Name).ToList();
        var needs = new List<string>();

        foreach (var item in sequence.Children)
        {
            if (RequireScalar(item, $"a step '{name}' needs", problems) is not { Length: > 0 } needed)
            {
                continue;
            }

            if (needs.Contains(needed, StringComparer.Ordinal))
            {
                problems.Add(At(item, $"step '{name}' names '{needed}' under 'needs' more than once."));
                continue;
            }

            needs.Add(needed);

            // Steps run in the order declared, so a step can need only one that has run by the time it
            // starts: itself, or one declared after it, would have to run before it had.
            if (earlier.FirstOrDefault(step => string.Equals(step.Name, needed, StringComparison.Ordinal)) is not { } step)
            {
                problems.Add(At(item, string.Equals(needed, name, StringComparison.Ordinal)
                    ? $"step '{name}' needs itself; a step can need only one declared before it."
                    : $"step '{name}' needs '{needed}', and no step declared before it has that name: steps run in "
                        + "the order declared, so a step can need only one declared before it. "
                        + (before.Count == 0 ? "No step is declared before it." : $"Declared before it: {string.Join(", ", before)}.")));
                continue;
            }

            // A run that names no step runs every step that is not manual. One of those needing a manual
            // step would have that run run it too - the very thing 'manual' says a plain run never does.
            if (!manual && step.Manual)
            {
                problems.Add(At(item, $"step '{name}' runs in every run of the action and needs '{needed}', which is "
                    + $"manual: a run that names no step would have to run '{needed}' too. Make '{name}' manual, or "
                    + $"'{needed}' not."));
            }

            // What a step needs runs first on every leg the step runs on. A leg's operating system
            // leaves out the steps that do not run on it, and what this step needs left out there
            // would have it run on that leg without it.
            var provided = earlier
                .Where(other => string.Equals(other.Name, needed, StringComparison.Ordinal))
                .SelectMany(other => other.RunOn.Count == 0 ? PlatformNames.OperatingSystems : other.RunOn)
                .ToHashSet(StringComparer.Ordinal);

            var unmet = (runOn.Count == 0 ? PlatformNames.OperatingSystems : runOn)
                .Where(system => !provided.Contains(system))
                .ToList();

            if (unmet.Count > 0)
            {
                problems.Add(At(item, $"step '{name}' needs '{needed}', which does not run on {string.Join(" or ", unmet)}: "
                    + $"a leg there would run '{name}' without it. Name {string.Join(" and ", unmet)} in '{needed}''s runOn, "
                    + $"or leave {(unmet.Count == 1 ? "it" : "them")} out of '{name}''s."));
            }
        }

        return needs;
    }

    /// <summary>
    /// Records a problem for a declared input whose name is one this tool already fills in.
    /// </summary>
    /// <param name="node">The input's own node, for the line number.</param>
    /// <param name="name">The declared name.</param>
    /// <param name="problems">Where problems are collected.</param>
    /// <remarks>
    /// Refused rather than resolved one way or the other, because the two halves would answer
    /// differently: a run line naming it gets this tool's value, while the environment a
    /// <c>harness/read-inputs</c> step fills gets the file's. An action declaring <c>config</c> would
    /// invoke its program with the leg's build configuration while telling it, in
    /// <c>INPUT_CONFIG</c>, the corpus the author meant — and both halves would report success.
    /// </remarks>
    private static void RefuseShadowedInput(YamlNode node, string name, List<string> problems)
    {
        if (!LegPathNames.All.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        problems.Add(At(
            node,
            $"an input named '{name}' is a name this tool already fills in, so a run line naming "
            + $"'{{{name}}}' would get this tool's value while INPUT_{name.ToUpperInvariant()} carried "
            + "the action's. Give the input another name."));
    }

    /// <summary>
    /// A step's <c>runOn</c>: the operating systems it runs on, each once. An empty list is refused,
    /// since a step naming none would never run: leaving the key out is how a step says every one.
    /// </summary>
    private static IReadOnlyList<string> ReadRunOn(YamlNode node, List<string> problems)
    {
        var available = $"'{string.Join("', '", PlatformNames.OperatingSystems)}'";

        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(At(node, $"a step's 'runOn' is a list of the operating systems it runs on: {available}."));
            return [];
        }

        if (sequence.Children.Count == 0)
        {
            problems.Add(At(
                node,
                "a step's 'runOn' names no operating system, so the step would never run. Leave the key "
                + "out for a step every leg runs."));
            return [];
        }

        var systems = new List<string>();

        foreach (var item in sequence.Children)
        {
            if (RequireScalar(item, "an operating system in runOn", problems) is not { Length: > 0 } system)
            {
                continue;
            }

            if (!PlatformNames.OperatingSystems.Contains(system, StringComparer.Ordinal))
            {
                problems.Add(At(item, $"'{system}' is not an operating system runOn can name. Available: {available}."));
                continue;
            }

            if (!systems.Contains(system, StringComparer.Ordinal))
            {
                systems.Add(system);
            }
        }

        return systems;
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

            if (PlatformPaths.IsRootedOnAnyPlatform(normalised)
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

        // Rooted by every platform's rules: a drive letter written on Linux is not rooted there, and
        // a step must not mean two different directories on two machines.
        if (path.Length == 0 || PlatformPaths.IsRootedOnAnyPlatform(path))
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
