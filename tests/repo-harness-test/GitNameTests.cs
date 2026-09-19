using System.Text;
using RepoHarness.Core.Git;

namespace RepoHarness.Tests;

/// <summary>How a name git listed as bytes becomes text.</summary>
public sealed class GitNameTests
{
    [Fact]
    public void AUtf8Name_IsItsOwnText_AndItsOwnQuotedForm()
    {
        var name = GitName.FromBytes(AsListed("docs/naïve ✅.md"u8));

        Assert.Equal(new GitName("docs/naïve ✅.md", true), name);
        Assert.Equal("docs/naïve ✅.md", name.Quoted);
    }

    /// <summary>
    /// A byte that begins no UTF-8 character reads as U+FFFD, as .NET reads the name from a directory,
    /// and which nothing takes for a separator. For a person it is quoted as git quotes it, with a
    /// backslash doubled so no escape reads two ways: a stray byte, and a character cut short at the
    /// end of the name.
    /// </summary>
    [Fact]
    public void ANameThatIsNotUtf8_ReadsAsDotNetReadsIt_AndIsQuotedAsGitQuotesIt()
    {
        Assert.Equal(
            new GitName("é\\caf�.md", false) { Quoted = @"é\\caf\351.md" },
            GitName.FromBytes(AsListed([.. "é\\caf"u8, 0xE9, .. ".md"u8])));
        Assert.Equal(
            new GitName("euro�", false) { Quoted = @"euro\342\202" },
            GitName.FromBytes(AsListed([.. "euro"u8, 0xE2, 0x82])));
    }

    /// <summary>
    /// Two names that read alike are still two names: one holding U+FFFD of its own, and one whose
    /// stray byte reads as U+FFFD.
    /// </summary>
    [Fact]
    public void TwoNamesThatReadAlike_AreTwoNames()
    {
        var own = GitName.FromBytes(AsListed("caf�"u8));
        var stray = GitName.FromBytes(AsListed([.. "caf"u8, 0xFF]));

        Assert.Equal(own.Text, stray.Text);
        Assert.NotEqual(own, stray);
    }

    /// <summary>The name's bytes as git's output reaches <see cref="GitName.FromBytes"/>: one to a character.</summary>
    private static string AsListed(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}
