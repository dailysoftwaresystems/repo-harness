using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// A version as NuGet orders it: major, minor and patch, then an optional prerelease label, which
/// sorts before the release it leads up to. Build metadata after <c>+</c> is ignored, as NuGet ignores it.
/// </summary>
public sealed class SemanticVersion
{
    private readonly string[] _prerelease;
    private readonly string _text;

    private SemanticVersion(int major, int minor, int patch, string[] prerelease, string text)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
        _text = text;
    }

    /// <summary>The major version.</summary>
    public int Major { get; }

    /// <summary>The minor version.</summary>
    public int Minor { get; }

    /// <summary>The patch version.</summary>
    public int Patch { get; }

    /// <summary>Whether this carries a prerelease label, and so sorts before the release it leads up to.</summary>
    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>Reads a version such as <c>1.2.3</c> or <c>1.2.3-beta</c>.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var withoutMetadata = text.Trim().Split('+', 2)[0];
        var dash = withoutMetadata.IndexOf('-', StringComparison.Ordinal);
        var numbers = (dash < 0 ? withoutMetadata : withoutMetadata[..dash]).Split('.');
        string[] label = dash < 0 ? [] : withoutMetadata[(dash + 1)..].Split('.');

        if (numbers.Length != 3
            || !TryReadNumber(numbers[0], out var major)
            || !TryReadNumber(numbers[1], out var minor)
            || !TryReadNumber(numbers[2], out var patch)
            || label.Any(identifier => identifier.Length == 0))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, label, withoutMetadata);
        return true;
    }

    /// <summary>
    /// Orders two versions: negative when <paramref name="first"/> is lower, zero when they are the same
    /// version, positive when it is higher.
    /// </summary>
    public static int Compare(SemanticVersion first, SemanticVersion second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var order = first.Major.CompareTo(second.Major);
        order = order != 0 ? order : first.Minor.CompareTo(second.Minor);
        order = order != 0 ? order : first.Patch.CompareTo(second.Patch);

        if (order != 0)
        {
            return order;
        }

        // A release sorts after every prerelease of it.
        if (first._prerelease.Length == 0 || second._prerelease.Length == 0)
        {
            return second._prerelease.Length.CompareTo(first._prerelease.Length);
        }

        for (var index = 0; index < Math.Min(first._prerelease.Length, second._prerelease.Length); index++)
        {
            order = CompareIdentifiers(first._prerelease[index], second._prerelease[index]);

            if (order != 0)
            {
                return order;
            }
        }

        return first._prerelease.Length.CompareTo(second._prerelease.Length);
    }

    /// <summary>The version as it was written, without build metadata.</summary>
    public override string ToString() => _text;

    /// <summary>Numeric identifiers compare as numbers and sort before text; text compares ignoring case, as NuGet does.</summary>
    private static int CompareIdentifiers(string first, string second)
    {
        var firstIsNumber = TryReadNumber(first, out var firstNumber);
        var secondIsNumber = TryReadNumber(second, out var secondNumber);

        return (firstIsNumber, secondIsNumber) switch
        {
            (true, true) => firstNumber.CompareTo(secondNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.Compare(first, second, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool TryReadNumber(string text, out int number)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
