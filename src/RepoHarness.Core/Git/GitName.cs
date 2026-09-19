using System.Buffers;
using System.Text;
using System.Text.Unicode;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <summary>A name git holds - a path, from the repository's root - as git listed it.</summary>
/// <param name="Text">
/// The name as text: exactly the name, where it is UTF-8. git holds a name as bytes and nothing makes
/// them UTF-8, so a byte that begins no UTF-8 character is read as U+FFFD, as .NET reads the same name
/// from a directory: a character nothing takes for a separator, so a root or a pattern matched against
/// the text finds the name where it lies. Such a text names no file.
/// </param>
/// <param name="IsUtf8">
/// Whether the name is UTF-8 throughout, so that <paramref name="Text"/> is the name itself: one a
/// file can be opened by, and git asked about.
/// </param>
public sealed record GitName(string Text, bool IsUtf8)
{
    /// <summary>
    /// The name for a person, as git's quoting writes it: a byte that begins no UTF-8 character as its
    /// octal escape (<c>\351</c>), and a backslash doubled, so no escape reads two ways. The text itself
    /// where the name is UTF-8.
    /// </summary>
    /// <remarks>
    /// For saying which file is meant, never for matching: read as a path, the escape's backslash is a
    /// separator, and it moved a name into a root or an exclusion it is not in.
    /// </remarks>
    public string Quoted { get; init; } = Text;

    /// <summary>The name whose bytes <paramref name="bytes"/> carries, one to a character.</summary>
    /// <param name="bytes">What git printed for the name, read as Latin-1.</param>
    internal static GitName FromBytes(string bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var raw = Encoding.Latin1.GetBytes(bytes);
        var text = Encoding.UTF8.GetString(raw);

        return Utf8.IsValid(raw)
            ? new GitName(text, IsUtf8: true)
            : new GitName(text, IsUtf8: false) { Quoted = Quote(raw) };
    }

    /// <summary>
    /// The refusal for a file a command must read whose name is not UTF-8. No file opens by such a
    /// name here, on disk or in a commit, and skipped, the file passed a check that read nothing in it.
    /// </summary>
    /// <param name="consequence">What cannot be done with the file, to end a sentence.</param>
    public HarnessException Unreadable(string consequence)
        => new(
            HarnessExit.Refused,
            $"'{Quoted}' is not named in UTF-8: each \\ooo in it is a byte of the name that is not, as git quotes it. "
            + $"This tool opens a file only by a UTF-8 name, so {consequence}. Rename it in UTF-8.");

    /// <summary>A name that is not UTF-8, as git's quoting writes it.</summary>
    private static string Quote(ReadOnlySpan<byte> raw)
    {
        var quoted = new StringBuilder();

        while (!raw.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(raw, out var character, out var consumed) == OperationStatus.Done)
            {
                quoted.Append(character.Value == '\\' ? @"\\" : character.ToString());
            }
            else
            {
                foreach (var stray in raw[..consumed])
                {
                    quoted.Append('\\').Append(Convert.ToString(stray, 8).PadLeft(3, '0'));
                }
            }

            raw = raw[consumed..];
        }

        return quoted.ToString();
    }
}
