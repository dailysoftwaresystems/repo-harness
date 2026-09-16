using System.Text.RegularExpressions;

namespace RepoHarness.Core.Anchors;

/// <summary>One anchor id as a file cites it, and where.</summary>
/// <param name="Id">The id exactly as written.</param>
/// <param name="Path">The file, relative to the repository root, with forward slashes.</param>
/// <param name="LineNumber">The line the id is on, counting from one.</param>
public sealed record AnchorCitation(string Id, string Path, int LineNumber);

/// <summary>
/// Finds anchor ids cited in text. The shape comes from <see cref="AnchorIdRules"/>, so a repository
/// that spells its ids with another prefix is scanned for its own ids and not for someone else's.
/// </summary>
/// <remarks>
/// <para>
/// A citation must be separated from what precedes it, or a longer hyphenated word would donate its
/// tail: the measured case is the phrase <c>FIXED-32-BIT-WORD</c> in a source comment, whose tail is
/// id-shaped but is not an id. The guard this scanner replaces spelled that separation as a word
/// boundary, and a word boundary has a measured blind spot: an id written immediately after an
/// escape, as in the C++ literal <c>&lt;&lt; "\nD-SOME-ID: ..."</c>, is preceded by the <c>n</c> of
/// <c>\n</c>, which is a word character, so the id is DROPPED. An id that resolves nowhere and is
/// never reported is exactly the leak the check exists to stop.
/// </para>
/// <para>
/// So a preceding word character blocks a citation unless it is the tail of a recognised escape
/// sequence, which is then read as the separator it is on the page. A doubled backslash is a literal
/// backslash and not an escape, so <c>"\\nD-SOME-ID"</c> really does glue the id to a letter and is
/// not a citation: an escaped backslash is a character, not an escape.
/// </para>
/// </remarks>
public sealed class AnchorIdScanner
{
    /// <summary>
    /// Hyphen-separated groups a citation carries after the prefix: a head and at least one more.
    /// </summary>
    /// <remarks>
    /// Deliberately looser than <see cref="AnchorIdRules.MinimumSegments"/>, which governs minting.
    /// Stricter minting than resolution is the safe direction, because nothing mintable is then
    /// invisible to this scan; the reverse was measured to hide seventy registered rows in the
    /// repository this command serves, because the id-naming rule spells a compound word as one
    /// segment and so turned ids the check read into ids it ignored. A head on its own
    /// (<c>D-OPT</c>) stays informal and is not a citation.
    /// </remarks>
    public const int CitationSegments = 2;

    /// <summary>Most hex digits a numeric escape may carry before the id.</summary>
    private const int MaximumHexDigits = 8;

    /// <summary>The one-character escapes whose letter may sit immediately before an id.</summary>
    private const string SimpleEscapeLetters = "abefnrtv0";

    private readonly Regex _candidate;

    /// <summary>Builds a scanner for the ids <paramref name="rules"/> describes.</summary>
    /// <param name="rules">The repository's id spelling.</param>
    public AnchorIdScanner(AnchorIdRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Rules = rules;

        var escaped = Regex.Escape(rules.Prefix);

        // Segments as IsWellFormed spells them, so a candidate this finds is an id the registry
        // could hold. The separation from what precedes it is decided below rather than here: a
        // regex boundary cannot see the backslash two characters back.
        _candidate = new Regex(
            $@"{escaped}-[A-Za-z0-9_]+(?:-[A-Za-z0-9_]+){{{CitationSegments - 1},}}",
            RegexOptions.CultureInvariant);
    }

    /// <summary>The spelling rules this scanner derives the id shape from.</summary>
    public AnchorIdRules Rules { get; }

    /// <summary>Every id cited in <paramref name="text"/>, in the order it appears.</summary>
    /// <param name="path">The file, used only to label what is found.</param>
    /// <param name="text">The file's whole content.</param>
    public IReadOnlyList<AnchorCitation> Scan(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);

        var citations = new List<AnchorCitation>();
        var lineNumber = 0;

        foreach (var line in text.Split('\n'))
        {
            lineNumber++;

            foreach (var id in ScanLine(line))
            {
                citations.Add(new AnchorCitation(id, path, lineNumber));
            }
        }

        return citations;
    }

    /// <summary>Every id cited on one line, in the order it appears, repeats included.</summary>
    /// <param name="line">One line of text.</param>
    public IEnumerable<string> ScanLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var from = 0;

        while (from <= line.Length)
        {
            var match = _candidate.Match(line, from);

            if (!match.Success)
            {
                yield break;
            }

            if (IsSeparated(line, match.Index) && Rules.IsWellFormed(match.Value))
            {
                yield return match.Value;
                from = match.Index + match.Length;
            }
            else
            {
                // Resumed one character on rather than past the whole rejected run: a real citation
                // can begin inside text that only looked like one.
                from = match.Index + 1;
            }
        }
    }

    /// <summary>Whether an id starting at <paramref name="index"/> is separated from what precedes it.</summary>
    private static bool IsSeparated(string line, int index)
    {
        if (index == 0)
        {
            return true;
        }

        return !IsIdCharacter(line[index - 1]) || FollowsEscape(line, index);
    }

    /// <summary>
    /// Whether the text ending just before <paramref name="index"/> is a recognised escape sequence,
    /// which reads on the page as a separator even though its last character is a letter or a digit.
    /// </summary>
    private static bool FollowsEscape(string line, int index)
    {
        // A one-character escape: \n, \t and the rest. The blind spot this scanner exists to close.
        if (index >= 2 && SimpleEscapeLetters.Contains(line[index - 1], StringComparison.Ordinal)
            && IsIntroducedByBackslash(line, index - 1))
        {
            return true;
        }

        // A numeric escape: its hex digits, the marker that opened them, and the backslash.
        for (var digits = 1; digits <= MaximumHexDigits && index - digits - 1 >= 0; digits++)
        {
            if (!Uri.IsHexDigit(line[index - digits]))
            {
                break;
            }

            var opens = line[index - digits - 1] switch
            {
                'x' => true,
                'u' => digits == 4,
                'U' => digits == 8,
                _ => false,
            };

            if (opens && IsIntroducedByBackslash(line, index - digits - 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the character at <paramref name="index"/> is introduced by a backslash that is not
    /// itself escaped. Counted rather than tested, because <c>\\n</c> is a backslash and the letter
    /// <c>n</c>, not a newline, and the id after it really is glued to a letter.
    /// </summary>
    private static bool IsIntroducedByBackslash(string line, int index)
    {
        var backslashes = 0;

        for (var scan = index - 1; scan >= 0 && line[scan] == '\\'; scan--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    /// <summary>What counts as part of a word for the purpose of separating an id from one.</summary>
    private static bool IsIdCharacter(char character)
        => character == '_' || char.IsLetterOrDigit(character);
}
