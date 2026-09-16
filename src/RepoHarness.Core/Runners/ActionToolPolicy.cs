using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>Why one program an action file names is allowed to run, or is not.</summary>
public enum ProgramAllowance
{
    /// <summary>Nothing declares it and it is not in the repository, so it may not run.</summary>
    Undeclared = 0,

    /// <summary>A <c>tools</c> entry declares it, which is what makes it installed and probed.</summary>
    DeclaredTool,

    /// <summary>It is a path inside the repository, so the repository itself ships it.</summary>
    RepositoryPath,
}

/// <summary>
/// Decides whether the programs an action file names are allowed to run.
/// </summary>
/// <remarks>
/// <para>
/// The first token of every <c>run</c> line must be a program declared under <c>tools</c>, which is
/// what <c>install-missing-tools</c> guarantees is present and at a known version, or a path inside
/// the repository, because a repository ships programs of its own. Anything else is refused
/// <em>before anything runs</em>: an action file that reaches its fourth step and then fails on a
/// program nobody declared has already changed the tree, and the run has to be understood before it
/// can be repeated.
/// </para>
/// <para>
/// Separate from the parser so the decision is testable on its own. Whether a program may run is a
/// policy about this repository's configuration, not a fact about the file's syntax, and the two
/// go wrong for different reasons.
/// </para>
/// </remarks>
public sealed class ActionToolPolicy(IHostPlatform platform)
{
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// Why <paramref name="program"/> may run, or <see cref="ProgramAllowance.Undeclared"/>.
    /// </summary>
    /// <remarks>
    /// A bare name is matched against <see cref="ToolConfig.Name"/> case-insensitively, with and
    /// without an executable extension, because <c>cmake</c> and <c>cmake.exe</c> name one tool.
    /// Anything carrying a separator is treated as a path and must resolve strictly inside the
    /// repository; existence is deliberately not required, because the program a step runs is
    /// often one the build in an earlier step produces.
    /// </remarks>
    /// <param name="program">The first token of a <c>run</c> line.</param>
    /// <param name="tools">The repository's declared tools.</param>
    /// <param name="repositoryRoot">Root of the tree the runner acts on.</param>
    public ProgramAllowance Classify(string program, IReadOnlyList<ToolConfig> tools, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (LooksLikePath(program))
        {
            return IsInsideRepository(program, repositoryRoot)
                ? ProgramAllowance.RepositoryPath
                : ProgramAllowance.Undeclared;
        }

        var bare = Path.GetFileNameWithoutExtension(program);

        return tools.Any(tool =>
            tool.Name.Equals(program, StringComparison.OrdinalIgnoreCase)
            || tool.Name.Equals(bare, StringComparison.OrdinalIgnoreCase))
            ? ProgramAllowance.DeclaredTool
            : ProgramAllowance.Undeclared;
    }

    /// <summary>
    /// Every program in <paramref name="action"/> that may not run, one problem each, in the order
    /// the file names them.
    /// </summary>
    /// <param name="action">The parsed action file.</param>
    /// <param name="config">The repository's configuration, for its declared tools.</param>
    /// <param name="repositoryRoot">Root of the tree the runner acts on.</param>
    public IReadOnlyList<string> Problems(ActionFile action, HarnessConfig config, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var declared = config.Tools.Count == 0
            ? "nothing is declared under 'tools'"
            : $"declared: '{string.Join("', '", config.Tools.Select(tool => tool.Name))}'";

        return
        [
            .. action.Commands
                .Where(command => Classify(command.Program, config.Tools, repositoryRoot)
                    == ProgramAllowance.Undeclared)
                .Select(command =>
                    $"line {command.LineNumber}: '{command.Program}' is not declared under 'tools' "
                    + $"and is not a path inside the repository ({declared})."),
        ];
    }

    /// <summary>
    /// Refuses the whole file when any program in it may not run, naming every one of them.
    /// </summary>
    /// <param name="action">The parsed action file.</param>
    /// <param name="config">The repository's configuration, for its declared tools.</param>
    /// <param name="repositoryRoot">Root of the tree the runner acts on.</param>
    /// <exception cref="HarnessException">
    /// A program is neither declared nor shipped by the repository. Nothing has run.
    /// </exception>
    public void Enforce(ActionFile action, HarnessConfig config, string repositoryRoot)
    {
        var problems = Problems(action, config, repositoryRoot);

        if (problems.Count == 0)
        {
            return;
        }

        var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

        throw new HarnessException(
            HarnessExit.Refused,
            $"'{action.Path}' names {problems.Count} program(s) that may not run:"
            + $"{Environment.NewLine}{detail}");
    }

    /// <summary>
    /// Whether <paramref name="program"/> is written as a path rather than as a bare program name.
    /// </summary>
    /// <param name="program">The first token of a <c>run</c> line.</param>
    public static bool LooksLikePath(string program)
    {
        ArgumentNullException.ThrowIfNull(program);

        return program.Contains('/', StringComparison.Ordinal)
            || program.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(program);
    }

    private bool IsInsideRepository(string program, string repositoryRoot)
    {
        try
        {
            // Combine resolves a relative path against the tree and leaves a rooted one alone, and
            // containment resolves both fully, so neither '..' nor an absolute path from elsewhere
            // can pass itself off as a program the repository ships.
            var resolved = Path.Combine(repositoryRoot, program);

            return PathContainment.IsStrictlyInside(repositoryRoot, resolved, _platform.PathComparison);
        }
        catch (ArgumentException)
        {
            // A path the platform cannot express at all is not one this repository ships.
            return false;
        }
    }
}
