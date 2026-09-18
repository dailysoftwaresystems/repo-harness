using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Worktrees;

/// <summary>
/// Validates and generates worktree names.
/// </summary>
/// <remarks>
/// Names are short for a measured reason. A worktree's build tree sits below
/// <c>.harness-config/worktrees/&lt;name&gt;</c>, and on Windows the whole path, plus
/// <c>build/&lt;variant&gt;</c> for the longest variant this machine builds, plus the longest
/// path the build system generates below that, must stay under the 260 character limit.
/// Exceeding it does not fail as a clean error: it surfaces as compile errors in files the
/// worktree never touched.
/// </remarks>
public static partial class WorktreeName
{
    /// <summary>Length of a generated name, when the configured maximum allows it.</summary>
    public const int RandomLength = 10;

    /// <summary>
    /// Characters a generated name is drawn from: lowercase alphanumerics, so a
    /// generated name always satisfies <see cref="ValidateFormat"/> by construction.
    /// </summary>
    private const string RandomAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Validates a name's shape and its length against <paramref name="maxLength"/>.
    /// </summary>
    public static WorktreeNameResult Validate(string? name, int maxLength = WorktreeSettings.DefaultMaxNameLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var format = ValidateFormat(name);
        if (!format.TryGetName(out var accepted, out _))
        {
            return format;
        }

        if (accepted.Length > maxLength)
        {
            return WorktreeNameResult.Invalid(
                $"'{accepted}' is {accepted.Length} characters; the limit is {maxLength}. "
                + $"Longer names push build paths past the Windows {HostPlatform.WindowsMaxPath} "
                + "character limit, which surfaces as compile errors in unrelated files. "
                + "The limit is worktrees.maxNameLength in config.json.");
        }

        return format;
    }

    /// <summary>
    /// Validates a name's shape only: lowercase letters and digits joined by single
    /// hyphens. Separate from <see cref="Validate"/> because the shape never depends on
    /// configuration while the permitted length does, so the shape can be checked
    /// before a repository has even been located.
    /// </summary>
    public static WorktreeNameResult ValidateFormat(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return WorktreeNameResult.Invalid("A worktree name is required.");
        }

        if (!Pattern().IsMatch(name))
        {
            return WorktreeNameResult.Invalid(
                $"'{name}' is not a valid name. Use lowercase letters and digits "
                + "separated by single hyphens, for example 'fix-auth'.");
        }

        return WorktreeNameResult.Valid(name);
    }

    /// <summary>
    /// Generates a random name of <paramref name="length"/> characters, so a throwaway
    /// worktree does not need one invented. Avoiding a collision with an existing
    /// worktree is the caller's concern: see the generate-and-retry loop in the
    /// worktree service.
    /// </summary>
    public static string Generate(int length = RandomLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        var characters = new char[length];

        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = RandomAlphabet[RandomNumberGenerator.GetInt32(RandomAlphabet.Length)];
        }

        return new string(characters);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// Outcome of validating a worktree name. Constructed only through the factories,
/// so a result always carries either a name or a reason and never neither.
/// </summary>
public sealed record WorktreeNameResult
{
    private WorktreeNameResult(string? name, string? error)
    {
        Name = name;
        Error = error;
    }

    /// <summary>Whether the name is usable.</summary>
    public bool IsValid => Name is not null;

    /// <summary>The accepted name, when valid.</summary>
    public string? Name { get; }

    /// <summary>What is wrong with it, when invalid.</summary>
    public string? Error { get; }

    /// <summary>An accepted name.</summary>
    public static WorktreeNameResult Valid(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new WorktreeNameResult(name, null);
    }

    /// <summary>A rejected name, with the reason.</summary>
    public static WorktreeNameResult Invalid(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new WorktreeNameResult(null, error);
    }

    /// <summary>
    /// Yields the name when valid and the reason when not, so a caller never has to
    /// assert which of the two is present.
    /// </summary>
    public bool TryGetName(
        [NotNullWhen(true)] out string? name,
        [NotNullWhen(false)] out string? error)
    {
        name = Name;
        error = Error;
        return name is not null;
    }
}
