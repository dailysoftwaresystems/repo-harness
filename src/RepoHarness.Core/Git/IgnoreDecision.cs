namespace RepoHarness.Core.Git;

/// <summary>What git decided about one path, and which rule decided it.</summary>
/// <param name="Path">The path asked about, relative to the work tree's root.</param>
/// <param name="Source">
/// The file holding the deciding rule, as git names it - <c>.gitignore</c> for the root's own - or
/// <see langword="null"/> where no rule matches the path.
/// </param>
/// <param name="Line">The rule's line in <paramref name="Source"/>, counting from one; zero where none matches.</param>
/// <param name="Pattern">The rule as its file writes it, <c>!</c> and all; <see langword="null"/> where none matches.</param>
public sealed record IgnoreDecision(string Path, string? Source, int Line, string? Pattern)
{
    /// <summary>
    /// Whether git ignores the path: a rule matched it, and that rule is not a re-include. A rule
    /// that begins with an escaped <c>\!</c> ignores; only a bare <c>!</c> re-includes. Says nothing
    /// where git would not answer; see <see cref="Unanswered"/>.
    /// </summary>
    public bool Ignored => Pattern is { Length: > 0 } rule && rule[0] != '!';

    /// <summary>
    /// Why git would not say which rule decides the path, in its own words - one beyond a symbolic
    /// link, which git never looks past - or <see langword="null"/> where it said.
    /// </summary>
    public string? Unanswered { get; init; }
}
