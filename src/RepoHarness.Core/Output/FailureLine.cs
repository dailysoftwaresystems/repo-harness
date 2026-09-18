using System.Diagnostics.CodeAnalysis;

namespace RepoHarness.Core.Output;

/// <summary>
/// The line a command ends with when it fails, spelled once for everything that writes one and for
/// the one reader that has to find it again.
/// </summary>
/// <remarks>
/// A host runs a leg as a command of its own, and when that command refuses before any leg has a
/// verdict, its failure line is all it said about why. The machine that dispatched the leg reads the
/// line back out of what the host wrote, so the writing and the reading cannot drift apart.
/// </remarks>
public static class FailureLine
{
    /// <summary>The line <paramref name="command"/> fails with, saying <paramref name="message"/>.</summary>
    /// <param name="command">The command failing.</param>
    /// <param name="message">Why.</param>
    public static string For(string command, string message) => $"{command}: FAIL - {message}";

    /// <summary>Reads <paramref name="line"/> as the failure line of <paramref name="command"/>.</summary>
    /// <param name="line">A line a command wrote.</param>
    /// <param name="command">The command whose failure line is looked for.</param>
    /// <param name="message">What the failure said, when the line is one.</param>
    public static bool TryRead(string line, string command, [NotNullWhen(true)] out string? message)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var prefix = For(command, string.Empty);

        message = line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : null;
        return message is not null;
    }
}
