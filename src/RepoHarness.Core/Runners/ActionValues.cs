using RepoHarness.Core.Secrets;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>
/// The values an action reads: the plain ones, and the secret ones behind a door that has to be
/// opened deliberately.
/// </summary>
/// <remarks>
/// <para>
/// A secret's value is never a property. It is reachable only through <see cref="RevealSecrets"/>,
/// whose one legitimate caller is the code handing a child process its environment. Everything else
/// that formats a value — a log line, a rendered argument list, the message of a failure — goes
/// through <see cref="Redact(string?)"/>. A secret leaks by being convenient, not by being
/// attacked: the leak that costs a credential is a failed command echoing what it was asked to run.
/// </para>
/// <para>
/// <see cref="ToString"/> is overridden for the same reason. A record's generated one prints every
/// member, so this is a class, and its text says how many values there are and never what they are.
/// </para>
/// </remarks>
public sealed class ActionValues
{
    /// <summary>What a redacted secret is replaced with.</summary>
    public const string Mask = "***";

    /// <summary>
    /// The plain values, by name, for a step's run line to name. Secrets are deliberately absent:
    /// a value spliced into a command line reaches the process table, where anything on the machine
    /// can read it, and the environment is how a secret is handed over instead.
    /// </summary>
    /// <remarks>
    /// Ordinal, unlike the environment these same values are handed over in. A name in a run line is
    /// matched exactly because the text around it belongs to a program: an action declaring an input
    /// called <c>home</c> must not have a step's <c>${HOME}</c> replaced with its value.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Supplied =>
        _supplied ??= new Dictionary<string, string>(_values, StringComparer.Ordinal);

    private Dictionary<string, string>? _supplied;

    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, string> _secrets;
    private readonly IReadOnlyList<string> _secretTexts;

    /// <summary>
    /// Builds the values an action reads.
    /// </summary>
    /// <param name="values">Plain values, by name.</param>
    /// <param name="secrets">Secret values, by name. Never exposed as a property.</param>
    public ActionValues(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(secrets);

        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        _secrets = new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase);

        // Longest first, so a secret that contains a shorter one does not leave its tail behind
        // after the shorter one has been masked. An empty value is not redactable: replacing it
        // would mask the whole text and say nothing about what was hidden.
        _secretTexts =
        [
            .. _secrets.Values
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length)
                .ThenBy(value => value, StringComparer.Ordinal),
        ];
    }

    /// <summary>No values at all, for a repository that declares none.</summary>
    public static ActionValues Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The plain values, by name. Safe to print.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The names of the secret values. The names are safe to print; the values are not.</summary>
    public IReadOnlyCollection<string> SecretNames => _secrets.Keys;

    /// <summary>Whether <paramref name="name"/> came from the secrets directory.</summary>
    /// <param name="name">The value's name.</param>
    public bool IsSecret(string name) => _secrets.ContainsKey(name);

    /// <summary>
    /// <paramref name="text"/> with every secret value replaced by <see cref="Mask"/>.
    /// </summary>
    /// <remarks>
    /// By value rather than by name, because what reaches a log is the value: a command line
    /// already has its placeholders substituted by the time anyone wants to print it.
    /// </remarks>
    /// <param name="text">Any text about to be shown, logged or thrown.</param>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var redacted = text;

        foreach (var secret in _secretTexts)
        {
            redacted = redacted.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return redacted;
    }

    /// <summary>Every element of <paramref name="arguments"/>, redacted.</summary>
    /// <remarks>
    /// Named apart from <see cref="Redact(string?)"/> rather than overloading it. The two overloads
    /// would be ambiguous for a null literal, and the call this exists for — rendering a command
    /// line for a log — is the one that must never be the call that failed to compile and got
    /// replaced by a plain join.
    /// </remarks>
    /// <param name="arguments">An argument list about to be shown or logged.</param>
    public IReadOnlyList<string> RedactAll(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return [.. arguments.Select(Redact)];
    }

    /// <summary>
    /// The secret values themselves.
    /// </summary>
    /// <remarks>
    /// The only deliberate way to the raw values, named so that it is conspicuous at the call site
    /// and in a review. The one legitimate caller is the code that hands a child process its
    /// environment, where the value has to be the real one. Anything that formats text calls
    /// <see cref="Redact(string?)"/> instead.
    /// </remarks>
    public IReadOnlyDictionary<string, string> RevealSecrets()
        => new Dictionary<string, string>(_secrets, StringComparer.OrdinalIgnoreCase);

    /// <summary>How many values there are, and never what they are.</summary>
    public override string ToString()
        => $"{_values.Count} value(s), {_secrets.Count} secret(s)";
}

/// <summary>
/// Reads the values an action reads from the runner's two value directories.
/// </summary>
/// <remarks>
/// <para>
/// <c>.harness-config/runner/.env</c> and <c>.harness-config/runner/.secrets</c> are each a
/// directory of dotenv files rather than a single file, so one repository can hold several
/// unrelated sets and a machine can be given only the ones it needs. Every file in a directory is
/// merged.
/// </para>
/// <para>
/// A name defined twice is refused rather than letting the later file win. Silent precedence over a
/// set of files whose order is a directory listing is a rule nobody can read off the files: the
/// value that reached a run would depend on a file name, and renaming a file would change what ran
/// without changing anything a reviewer looks at. That holds across the two directories too — a
/// name in both would leave <see cref="ActionValues.Redact(string?)"/> masking one value while the
/// run used the other.
/// </para>
/// </remarks>
public sealed class ActionValuesReader(IFileSystem fileSystem, IHarnessOutput output)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Reads and merges both directories.
    /// </summary>
    /// <param name="envDirectory">The plain value directory. Absent means no plain values.</param>
    /// <param name="secretsDirectory">The secret value directory. Absent means no secrets.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="HarnessException">
    /// A file is malformed, or a name is defined more than once. Every problem is reported together.
    /// </exception>
    public Task<ActionValues> ReadAsync(
        string envDirectory,
        string secretsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDirectory);

        cancellationToken.ThrowIfCancellationRequested();

        var problems = new List<string>();
        var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Merge(envDirectory, values, origins, problems, cancellationToken);
        Merge(secretsDirectory, secrets, origins, problems, cancellationToken);

        if (problems.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"the runner value directories have {problems.Count} problem(s):"
                + $"{Environment.NewLine}{detail}");
        }

        // Names only. The count of secrets is worth seeing; a secret is not.
        _output.Detail(
            "run",
            $"read {values.Count} value(s) and {secrets.Count} secret(s) for this action");

        return Task.FromResult(new ActionValues(values, secrets));
    }

    /// <summary>
    /// The definitions one dotenv file holds.
    /// </summary>
    /// <remarks>
    /// <c>NAME=value</c> lines, blank lines skipped, and a line whose first non-space character is
    /// <c>#</c> treated as a comment. A <c>#</c> anywhere else is an ordinary character and stays in
    /// the value: a generated credential contains one often enough that stripping it would produce a
    /// value that is almost right, which fails later and somewhere else. A value wrapped in a
    /// matching pair of quotes has them removed, so a value with leading or trailing spaces can be
    /// written down.
    /// </remarks>
    /// <param name="text">The file's contents.</param>
    /// <param name="path">Where it came from, quoted in any problem.</param>
    /// <param name="problems">Collects what was wrong with the file.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseDotEnv(
        string text,
        string path,
        List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(problems);

        var entries = new List<KeyValuePair<string, string>>();
        var lines = text.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            var number = index + 1;

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
            {
                problems.Add($"'{path}' line {number}: no '=', so nothing is defined here.");
                continue;
            }

            var name = line[..separator].Trim();

            if (name.Length == 0)
            {
                problems.Add($"'{path}' line {number}: the name before '=' is empty.");
                continue;
            }

            if (name.Any(char.IsWhiteSpace))
            {
                problems.Add($"'{path}' line {number}: '{name}' is not a name; "
                    + "an environment variable's name holds no spaces.");
                continue;
            }

            entries.Add(new KeyValuePair<string, string>(name, DotEnvFile.Unquote(line[(separator + 1)..].Trim())));
        }

        return entries;
    }

    private void Merge(
        string directory,
        Dictionary<string, string> target,
        Dictionary<string, string> origins,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        if (!_fileSystem.DirectoryExists(directory))
        {
            // Both directories are gitignored, so a fresh clone has neither. An action that needs a
            // value it did not get is refused where the value is used, with the name it wanted.
            return;
        }

        // Ordered so that "defined twice" names the same two files whatever order the file system
        // enumerated them in, which is what makes the refusal reproducible.
        var files = _fileSystem
            .EnumerateFiles(directory, recursive: false)
            .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (name, value) in ParseDotEnv(_fileSystem.ReadAllText(file), file, problems))
            {
                if (origins.TryGetValue(name, out var first))
                {
                    problems.Add($"'{name}' is defined in '{first}' and again in '{file}'; "
                        + "nothing can say which value a run used.");
                    continue;
                }

                origins[name] = file;
                target[name] = value;
            }
        }
    }
}
