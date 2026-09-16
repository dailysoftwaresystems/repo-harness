using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Runners;

/// <summary>
/// The runner an expected exception was read from, and the legs it is available on.
/// </summary>
/// <remarks>
/// An entry is scoped by the runner carrying it and the legs that runner declares. A confound
/// measured under emulation is a fact about emulation: offered to a native leg it would excuse the
/// regression it was written to explain, on the one leg where nothing explains it. The scope is a
/// type rather than a convention so that no caller can match an entry without having said which
/// leg it is matching for.
/// </remarks>
/// <param name="RunnerName">The predefined runner carrying the entries.</param>
/// <param name="Legs">
/// The legs the entries are available on. Empty means the runner declared none, so the scope
/// narrows to the runner alone; a caller that has resolved the default leg set passes it instead.
/// </param>
/// <param name="Entries">The expected exceptions the runner declares, in file order.</param>
public sealed record RunnerScope(
    string RunnerName,
    IReadOnlyList<string> Legs,
    IReadOnlyList<ExpectedException> Entries)
{
    /// <summary>
    /// The scope of <paramref name="runner"/>'s entries.
    /// </summary>
    /// <param name="runnerName">The runner's name, as <c>predefinedRunners</c> keys it.</param>
    /// <param name="runner">The runner's configuration.</param>
    /// <param name="resolvedLegs">
    /// The legs the run actually selected, for a runner whose own <c>legs</c> list is empty and
    /// therefore means the default set. Leaving it out keeps the scope at the runner alone.
    /// </param>
    public static RunnerScope From(
        string runnerName,
        RunnerConfig runner,
        IReadOnlyList<string>? resolvedLegs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerName);
        ArgumentNullException.ThrowIfNull(runner);

        var legs = runner.Legs.Count > 0 ? runner.Legs : resolvedLegs ?? [];

        return new RunnerScope(runnerName, [.. legs], runner.ExpectedExceptions);
    }

    /// <summary>Whether this scope's entries are available on <paramref name="leg"/>.</summary>
    /// <param name="leg">The leg whose failure is being explained.</param>
    public bool Covers(string leg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        return Legs.Count == 0
            || Legs.Contains(leg, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Finds the expected exception, if any, that explains a failure.
/// </summary>
/// <remarks>
/// <para>
/// A match has two halves. The type must be the entry's exact class name, case included: two
/// exceptions whose names differ only by case are two different exceptions, and an entry that
/// matched both would excuse one of them for a reason measured on the other. The message must be
/// recognised by at least one of the entry's <c>messages</c>, each of which is tried as a regular
/// expression when it compiles as one and as ordinary text when it does not — a match by either
/// counts, because an author writing plain text should not have to know which characters this
/// engine treats as syntax.
/// </para>
/// <para>
/// Every doubtful case answers no. A pattern that times out, an entry naming no messages, a leg
/// outside the scope: none of them excuse anything. The measurement is biased toward ABSENT because
/// a wrong ABSENT costs a failure somebody reads, and a wrong PRESENT costs a regression nobody
/// ever sees.
/// </para>
/// </remarks>
public sealed class ExpectedExceptionMatcher(IHarnessOutput output)
{
    /// <summary>
    /// How long one declared message may take to match. An entry is tracked configuration, so a
    /// pattern that backtracks forever is a mistake; the bound turns it into no match rather than
    /// into a run that never reports.
    /// </summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// The entry explaining <paramref name="failure"/> on <paramref name="leg"/>, or
    /// <see langword="null"/> when the failure stays unexplained.
    /// </summary>
    /// <param name="scope">The runner the entries came from, and the legs they are available on.</param>
    /// <param name="leg">The leg that failed.</param>
    /// <param name="failure">What the failure reported.</param>
    public ExpectedException? Find(RunnerScope scope, string leg, RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentNullException.ThrowIfNull(failure);

        if (!scope.Covers(leg))
        {
            _output.Detail(
                "run",
                $"'{scope.RunnerName}' declares no expected exception for leg '{leg}', "
                + $"so this failure stays genuine");

            return null;
        }

        var entry = scope.Entries.FirstOrDefault(candidate => Matches(candidate, failure));

        if (entry is not null)
        {
            _output.Detail(
                "run",
                $"'{failure.ExceptionType}' on leg '{leg}' matches the entry earned on "
                + $"{entry.EarnedOn} at {entry.EarnedAt} ({entry.Anchor})");
        }

        return entry;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> recognises <paramref name="failure"/>, leaving scope aside.
    /// </summary>
    /// <param name="entry">The declared expected exception.</param>
    /// <param name="failure">What the failure reported.</param>
    public static bool Matches(ExpectedException entry, RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(failure);

        return MatchesType(entry.ExceptionType, failure.ExceptionType)
            && entry.Messages.Any(message => failure.Texts.Any(text => MatchesMessage(message, text)));
    }

    /// <summary>
    /// Whether a declared type name names the type a run reported.
    /// </summary>
    /// <remarks>
    /// An entry declares a class name, so a report that carries a namespace is reduced to its class
    /// name before comparing; an entry that spells a namespace itself is compared whole, so an
    /// author who needs to tell two same-named classes apart still can. The comparison is ordinal.
    /// </remarks>
    /// <param name="declared">The entry's <c>exceptionType</c>.</param>
    /// <param name="reported">The type name the run reported.</param>
    public static bool MatchesType(string declared, string reported)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(reported);

        if (string.Equals(declared, reported, StringComparison.Ordinal))
        {
            return true;
        }

        if (declared.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var lastDot = reported.LastIndexOf('.');

        return lastDot >= 0
            && string.Equals(declared, reported[(lastDot + 1)..], StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether one declared message recognises <paramref name="text"/>, as a regular expression
    /// where it compiles as one and as ordinary text either way.
    /// </summary>
    /// <param name="message">One entry of <c>messages</c>.</param>
    /// <param name="text">The failure's message or its output.</param>
    public static bool MatchesMessage(string message, string text)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(text);

        if (message.Length == 0)
        {
            // An empty message recognises every failure of its type, which is an unconditional
            // claim spelled as a scope. Refused here as it is refused by the lint.
            return false;
        }

        if (text.Contains(message, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return Regex.IsMatch(text, message, RegexOptions.None, PatternTimeout);
        }
        catch (ArgumentException)
        {
            // Not a regular expression at all, so plain text was the only reading; already tried.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            // An answer this expensive is no answer. Biased toward ABSENT: the failure stays genuine.
            return false;
        }
    }
}
