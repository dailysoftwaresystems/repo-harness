using System.Text;
using RepoHarness.Core.Git;

namespace RepoHarness.Tests;

/// <summary>How a name git listed as bytes becomes text.</summary>
public sealed class GitNameTests
{
    [Fact]
    public void AUtf8Name_IsItsOwnText()
        => Assert.Equal(new GitName("docs/naïve ✅.md", true), GitName.FromBytes(AsListed("docs/naïve ✅.md"u8)));

    /// <summary>
    /// A byte that begins no UTF-8 character is written as git's quoting writes it, and the characters
    /// around it are still themselves: a stray byte, and a character cut short at the end of the name.
    /// </summary>
    [Fact]
    public void ANameThatIsNotUtf8_IsWrittenAsGitQuotesIt()
    {
        Assert.Equal(new GitName(@"é caf\351.md", false), GitName.FromBytes(AsListed([.. "é caf"u8, 0xE9, .. ".md"u8])));
        Assert.Equal(new GitName(@"euro\342\202", false), GitName.FromBytes(AsListed([.. "euro"u8, 0xE2, 0x82])));
    }

    /// <summary>The name's bytes as git's output reaches <see cref="GitName.FromBytes"/>: one to a character.</summary>
    private static string AsListed(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}
