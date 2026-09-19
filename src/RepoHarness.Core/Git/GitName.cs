using System.Buffers;
using System.Text;
using System.Text.Unicode;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <summary>A name git holds - a path, from the repository's root - as git listed it.</summary>
/// <param name="Text">
/// The name as text. git holds a name as bytes and nothing makes them UTF-8, so a byte that begins no
/// UTF-8 character is written as the octal escape git's own quoting writes it in (<c>\351</c>): the
/// text still tells a person which file is meant, but it is not the name.
/// </param>
/// <param name="IsUtf8">
/// Whether the name is UTF-8 throughout, so that <paramref name="Text"/> is the name itself: one a
/// file can be opened by, and git asked about.
/// </param>
public sealed record GitName(string Text, bool IsUtf8)
{
    /// <summary>The name whose bytes <paramref name="bytes"/> carries, one to a character.</summary>
    /// <param name="bytes">What git printed for the name, read as Latin-1.</param>
    internal static GitName FromBytes(string bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var raw = Encoding.Latin1.GetBytes(bytes);

        if (Utf8.IsValid(raw))
        {
            return new GitName(Encoding.UTF8.GetString(raw), IsUtf8: true);
        }

        var text = new StringBuilder();
        ReadOnlySpan<byte> rest = raw;

        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(rest, out var character, out var consumed) == OperationStatus.Done)
            {
                text.Append(character.ToString());
            }
            else
            {
                foreach (var stray in rest[..consumed])
                {
                    text.Append('\\').Append(Convert.ToString(stray, 8).PadLeft(3, '0'));
                }
            }

            rest = rest[consumed..];
        }

        return new GitName(text.ToString(), IsUtf8: false);
    }

    /// <summary>
    /// The refusal for a file a command must read whose name is not UTF-8. No file opens by such a
    /// name here, on disk or in a commit, and skipped, the file passed a check that read nothing in it.
    /// </summary>
    /// <param name="consequence">What cannot be done with the file, to end a sentence.</param>
    public HarnessException Unreadable(string consequence)
        => new(
            HarnessExit.Refused,
            $"'{Text}' is not named in UTF-8: each \\ooo in it is a byte of the name that is not, as git quotes it. "
            + $"This tool opens a file only by a UTF-8 name, so {consequence}. Rename it in UTF-8.");
}
