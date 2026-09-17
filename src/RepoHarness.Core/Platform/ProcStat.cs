using System.Globalization;

namespace RepoHarness.Core.Platform;

/// <summary>
/// Reading <c>/proc/&lt;pid&gt;/stat</c>, which is where Linux publishes what it knows about a process.
/// </summary>
/// <remarks>
/// Shared by the process table and by the identity a lock records, because both want the same two
/// facts out of the same file and a second parser would be a second thing to get wrong. The program
/// name is in parentheses and may itself hold spaces and parentheses, so every field after it is
/// found from the last closing one and never by counting from the start.
/// </remarks>
internal static class ProcStat
{
    /// <summary>The parent's id, among the fields after the name.</summary>
    public const int ParentField = 1;

    /// <summary>
    /// When the process started, in clock ticks since this machine booted, among the fields after the
    /// name. Ticks since boot, never an instant: the wall clock can step and this cannot, which is
    /// what makes it an identity rather than a timestamp.
    /// </summary>
    public const int StartTicksField = 19;

    /// <summary>Clock ticks in a second on every Linux ABI, whatever the kernel's own tick rate is.</summary>
    public const double TicksPerSecond = 100.0;

    /// <summary>Where the kernel publishes an id that changes only when the machine boots.</summary>
    public const string BootIdPath = "/proc/sys/kernel/random/boot_id";

    /// <summary>The program's name, or <see langword="null"/> when the text does not carry one.</summary>
    /// <param name="stat">The contents of one <c>stat</c> file.</param>
    public static string? Name(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);

        var close = stat.LastIndexOf(')');
        var open = stat.IndexOf('(', StringComparison.Ordinal);

        return open >= 0 && close > open ? stat[(open + 1)..close] : null;
    }

    /// <summary>
    /// The fields after the program name, or <see langword="null"/> when the text is not a stat file.
    /// </summary>
    /// <param name="stat">The contents of one <c>stat</c> file.</param>
    public static string[]? FieldsAfterName(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);

        var close = stat.LastIndexOf(')');

        return close < 0 ? null : stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// One number among the fields after the name, or <see langword="null"/> when it is not there or
    /// is not a number.
    /// </summary>
    /// <param name="fields">What <see cref="FieldsAfterName"/> returned.</param>
    /// <param name="field">Which field, counted from the first one after the name.</param>
    public static long? Number(string[]? fields, int field)
        => fields is not null
            && fields.Length > field
            && long.TryParse(fields[field], CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
