using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Runners;

/// <summary>
/// A predefined action a step names under <c>uses</c>. The set is closed and small on purpose:
/// an action file describes what a runner does, and every step that is not one of these is a
/// program the repository or a declared tool provides, which the tool policy can vet.
/// </summary>
public enum PredefinedAction
{
    /// <summary>The step runs programs from its own <c>run</c> block rather than a predefined action.</summary>
    None = 0,

    /// <summary>
    /// <c>harness/checkout</c>: confirm the leg's tree is already at a named commit or branch before
    /// the next step. It never moves the tree: a runner that moved the tree would decide for itself
    /// what was being measured, after the sync that put it there.
    /// </summary>
    Checkout,

    /// <summary>
    /// <c>harness/read-inputs</c>: resolve the file's declared inputs from the runner value
    /// directories, so later steps see them.
    /// </summary>
    ReadInputs,
}

/// <summary>
/// How an action file spells each predefined action, and how a spelling is read back.
/// </summary>
/// <remarks>
/// The spellings live here rather than in the parser so that a refusal can list what is available.
/// An unknown <c>uses</c> that merely said "unknown action" left the author guessing at the
/// spelling, and the guess that follows is usually the GitHub one, which this tool does not have.
/// </remarks>
public static class PredefinedActions
{
    /// <summary>The spelling of <see cref="PredefinedAction.Checkout"/>.</summary>
    public const string Checkout = "harness/checkout";

    /// <summary>The spelling of <see cref="PredefinedAction.ReadInputs"/>.</summary>
    public const string ReadInputs = "harness/read-inputs";

    /// <summary>Every spelling, in the order a refusal lists them.</summary>
    /// <remarks>
    /// Derived from the actions themselves, so an action added to the enum cannot go missing from
    /// the refusal that lists what is available.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } =
        [.. Enum.GetValues<PredefinedAction>().Where(action => action != PredefinedAction.None).Select(Spell)];

    /// <summary>
    /// The action <paramref name="uses"/> names, or <see langword="null"/> when nothing does.
    /// Compared exactly: an action file is tracked, so a spelling that only differs by case is a
    /// typo, and accepting it would make two files that read differently run the same.
    /// </summary>
    /// <param name="uses">The value of a step's <c>uses</c> key.</param>
    public static PredefinedAction? Parse(string uses) => uses switch
    {
        Checkout => PredefinedAction.Checkout,
        ReadInputs => PredefinedAction.ReadInputs,
        _ => null,
    };

    /// <summary>How <paramref name="action"/> is spelled in an action file.</summary>
    /// <param name="action">The action to spell.</param>
    public static string Spell(PredefinedAction action) => action switch
    {
        PredefinedAction.Checkout => Checkout,
        PredefinedAction.ReadInputs => ReadInputs,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "This build has no spelling for that action."),
    };
}

/// <summary>
/// One value an action file declares it reads, so a file states its interface rather than failing
/// part way through on a value nobody supplied.
/// </summary>
/// <param name="Name">The value's name, as the runner value directories spell it.</param>
/// <param name="Default">What to use when nothing supplies it, or <see langword="null"/>.</param>
/// <param name="Required">Whether the run is refused when nothing supplies it and there is no default.</param>
/// <param name="Description">What the value is for, shown when it is missing.</param>
public sealed record ActionInput(string Name, string? Default, bool Required, string? Description);

/// <summary>
/// One program and its arguments, taken from one line of a step's <c>run</c> block.
/// </summary>
/// <remarks>
/// A command is an argument list, never a shell string: nothing between the action file and the
/// child process expands a variable, splits on a space or interprets a metacharacter, so what the
/// file says is what runs.
/// </remarks>
/// <param name="Line">The line as written, trimmed. Kept so a refusal can quote it back.</param>
/// <param name="LineNumber">The line's position in the file, for a refusal that can be found.</param>
/// <param name="Arguments">The program, then its arguments. Never empty.</param>
public sealed record ActionCommand(string Line, int LineNumber, IReadOnlyList<string> Arguments)
{
    /// <summary>The program, then its arguments. Never empty.</summary>
    /// <exception cref="ArgumentException">
    /// The list is empty. Enforced here rather than only in the parser, because
    /// <see cref="Program"/> is what the tool policy vets and an empty list would reach it as an
    /// index out of range: a refusal about a program nobody can see, instead of one naming the line.
    /// </exception>
    public IReadOnlyList<string> Arguments { get; init; } = Arguments.Count > 0
        ? Arguments
        : throw new ArgumentException("A command line holds a program and its arguments, so it is never empty.", nameof(Arguments));

    /// <summary>The program this line starts. The token the tool policy vets.</summary>
    public string Program => Arguments[0];
}

/// <summary>
/// A runner's action file: what it is, what it reads, and the steps it runs, already checked.
/// </summary>
/// <remarks>
/// An action file replaces a runner's <c>phases</c>, so it must carry everything a phase carries.
/// <see cref="ToPhases"/> is that guarantee made executable: every step converts to the same
/// <see cref="RunnerPhase"/> the rest of the harness already knows how to run, witness and time.
/// A field that could not survive the conversion would be a field the verdict contract silently
/// lost.
/// </remarks>
/// <param name="Path">Where the file was read from, used in every refusal about it.</param>
/// <param name="Name">The file's own name, shown in the ledger. Not the file name.</param>
/// <param name="Description">What the action does.</param>
/// <param name="Inputs">Values the action declares it reads.</param>
/// <param name="Steps">The steps, in order. Never empty.</param>
public sealed record ActionFile(
    string Path,
    string? Name,
    string? Description,
    IReadOnlyList<ActionInput> Inputs,
    IReadOnlyList<ActionStep> Steps)
{
    /// <summary>Every program invocation in the file, in the order the steps run them.</summary>
    /// <remarks>
    /// The tool policy reads this: every program a file can start is vetted before the first one
    /// runs, so a file whose last step names an undeclared tool is refused before its first step
    /// has changed anything.
    /// </remarks>
    public IEnumerable<ActionCommand> Commands => Steps.SelectMany(step => step.Commands);

    /// <summary>
    /// The name of the directory this action owns, taken from where the file was read rather than
    /// from its <see cref="Name"/> key.
    /// </summary>
    /// <remarks>
    /// The layout requires the two to match, and this is the one the file system agrees with: a step
    /// that runs in its action's own directory has to reach the directory that is actually there,
    /// not the one a <c>name:</c> key claims it is.
    /// </remarks>
    public string DirectoryName
        => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) ?? string.Empty;

    /// <summary>
    /// The file's steps as the phases the harness runs. A predefined action contributes none,
    /// because it is not a child process; the caller performs it.
    /// </summary>
    public IReadOnlyList<RunnerPhase> ToPhases()
        => [.. Steps.SelectMany(step => step.ToPhases(DirectoryName))];
}
