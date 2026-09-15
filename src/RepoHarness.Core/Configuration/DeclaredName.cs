namespace RepoHarness.Core.Configuration;

/// <summary>Finds the spelling a configuration declares a name with.</summary>
/// <remarks>
/// Configuration keys compare ignoring case, so a name may be typed in any case. Some programs a name is
/// handed to compare exactly: ssh applies a <c>Host</c> entry only to the name spelt as the entry spells it.
/// What reaches such a program is therefore the declared spelling, never the typed one.
/// </remarks>
public static class DeclaredName
{
    /// <summary>
    /// The spelling <paramref name="names"/> declares <paramref name="typed"/> with, or <see langword="null"/>
    /// when none of them is that name.
    /// </summary>
    public static string? In(IEnumerable<string> names, string typed)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(typed);

        return names.FirstOrDefault(name => string.Equals(name, typed, StringComparison.OrdinalIgnoreCase));
    }
}
