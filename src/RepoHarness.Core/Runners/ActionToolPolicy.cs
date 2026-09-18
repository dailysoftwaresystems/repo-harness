using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
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
    /// repository, read from the directory the step runs in - where the run itself reads it; existence
    /// is deliberately not required, because the program a step runs is often one the build in an
    /// earlier step produces.
    /// </remarks>
    /// <param name="program">The first token of a <c>run</c> line.</param>
    /// <param name="tools">The repository's declared tools.</param>
    /// <param name="repositoryRoot">Root of the tree the runner acts on.</param>
    /// <param name="workingDirectory">
    /// The directory the step runs in: absolute, or relative to <paramref name="repositoryRoot"/>, or
    /// <see langword="null"/> for the root itself.
    /// </param>
    public ProgramAllowance Classify(string program, IReadOnlyList<ToolConfig> tools, string repositoryRoot, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (ProcessRunner.IsPath(program))
        {
            return IsInsideRepository(program, repositoryRoot, workingDirectory)
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
    /// <param name="started">
    /// What a line of a step will start, and the directory it starts in, as the run works them out -
    /// its placeholders filled in - so the program judged is the one that starts, from where it starts.
    /// </param>
    /// <param name="redact">
    /// Masks what a line was filled in with, where a problem shows it: a value its placeholders are
    /// filled from may be a secret. Nothing is masked when absent.
    /// </param>
    public IReadOnlyList<string> Problems(
        ActionFile action,
        HarnessConfig config,
        string repositoryRoot,
        Func<ActionStep, ActionCommand, (string Program, string WorkingDirectory)> started,
        Func<string, string>? redact = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(started);

        var declared = config.Tools.Count == 0
            ? "nothing is declared under 'tools'"
            : $"declared: '{string.Join("', '", config.Tools.Select(tool => tool.Name))}'";

        return
        [
            .. action.Steps
                .SelectMany(step => step.Commands.Select(command => Problem(command, started(step, command), config.Tools, repositoryRoot, declared, redact ?? (text => text))))
                .OfType<string>(),
        ];
    }

    /// <summary>What is wrong with one line, or <see langword="null"/> when what it starts may run.</summary>
    private string? Problem(
        ActionCommand command,
        (string Program, string WorkingDirectory) started,
        IReadOnlyList<ToolConfig> tools,
        string repositoryRoot,
        string declared,
        Func<string, string> redact)
    {
        var line = $"line {command.LineNumber}: '{command.Program}'";

        // Filled in to nothing - an empty value in the runner's .env, an input whose default is
        // empty - a line starts no program at all.
        if (string.IsNullOrWhiteSpace(started.Program))
        {
            return $"{line} starts nothing once its names are filled in.";
        }

        if (Classify(started.Program, tools, repositoryRoot, started.WorkingDirectory) != ProgramAllowance.Undeclared)
        {
            return null;
        }

        return $"{line}{ReadAs(command, started, repositoryRoot, redact)} is not declared under 'tools' and is not a path inside the repository ({declared}).";
    }

    /// <summary>
    /// What a line was read as, where that is what the file does not say - its names filled in, or
    /// a relative path read from a directory other than the repository's own - and nothing where the
    /// file already says it: '../elsewhere/tool' says why a './tool' is not inside the repository.
    /// </summary>
    /// <remarks>
    /// Masked before anything is made of it, as every line that leaves a run is: a value the line was
    /// filled in with may be a secret, and a path made of it would no longer be the text a mask finds.
    /// Worked out whole inside one guard, so writing a refusal never becomes an error of its own.
    /// </remarks>
    private static string ReadAs(
        ActionCommand command,
        (string Program, string WorkingDirectory) started,
        string repositoryRoot,
        Func<string, string> redact)
    {
        try
        {
            var filled = !string.Equals(started.Program, ProcessRunner.Anchored(command.Program, started.WorkingDirectory), StringComparison.Ordinal);
            var elsewhere = ProcessRunner.IsPath(command.Program)
                && !Path.IsPathFullyQualified(command.Program)
                && !string.Equals(Path.TrimEndingDirectorySeparator(started.WorkingDirectory), Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)), StringComparison.Ordinal);

            if (!filled && !elsewhere)
            {
                return string.Empty;
            }

            var masked = redact(started.Program);

            return $" (read as '{(ProcessRunner.IsPath(masked) ? Path.GetRelativePath(repositoryRoot, masked) : masked).Replace('\\', '/')}')";
        }
        catch (ArgumentException)
        {
            // A line the platform cannot read as a path: shown as it was filled in, masked, where
            // that is not what the file says.
            return string.Equals(started.Program, command.Program, StringComparison.Ordinal)
                ? string.Empty
                : $" (read as '{redact(started.Program)}')";
        }
    }

    /// <summary>
    /// Refuses the whole file when any program in it may not run, naming every one of them.
    /// </summary>
    /// <param name="action">The parsed action file.</param>
    /// <param name="config">The repository's configuration, for its declared tools.</param>
    /// <param name="repositoryRoot">Root of the tree the runner acts on.</param>
    /// <param name="started">What each line will start, and from where, as the run works them out.</param>
    /// <param name="redact">Masks what a line was filled in with, where a problem shows it.</param>
    /// <exception cref="HarnessException">
    /// A program is neither declared nor shipped by the repository, or a line starts nothing. Nothing
    /// has run.
    /// </exception>
    public void Enforce(
        ActionFile action,
        HarnessConfig config,
        string repositoryRoot,
        Func<ActionStep, ActionCommand, (string Program, string WorkingDirectory)> started,
        Func<string, string>? redact = null)
    {
        var problems = Problems(action, config, repositoryRoot, started, redact);

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

    private bool IsInsideRepository(string program, string repositoryRoot, string? workingDirectory)
    {
        try
        {
            // Resolved as the run starts it, and contained fully, so neither '..' nor an absolute
            // path from elsewhere can pass itself off as a program the repository ships.
            return PathContainment.IsStrictlyInside(repositoryRoot, Resolved(program, repositoryRoot, workingDirectory), _platform.PathComparison);
        }
        catch (ArgumentException)
        {
            // A path the platform cannot express at all is not one this repository ships.
            return false;
        }
    }

    /// <summary>
    /// The file a program named by a path is, as the run starts it: a relative one made whole against
    /// the directory its step starts in, itself made whole against the repository.
    /// </summary>
    private static string Resolved(string program, string repositoryRoot, string? workingDirectory)
        => ProcessRunner.Anchored(program, Path.GetFullPath(workingDirectory ?? ".", repositoryRoot));
}
