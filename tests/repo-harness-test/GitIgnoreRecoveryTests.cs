using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;

namespace RepoHarness.Tests;

/// <summary>
/// A .gitignore is hand edited and merged, so the managed block will eventually be
/// found in a damaged state. What matters is that recovering from it never costs
/// the user a rule they wrote themselves.
/// </summary>
public sealed class GitIgnoreRecoveryTests
{
    private static readonly string[] Rules = ["/a", "!/a/.gitkeep"];

    private static GitIgnoreManager CreateManager()
        => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));

    [Fact]
    public void AnOrphanedBeginMarker_DoesNotProduceASecondBlock()
    {
        // A truncated write or a merge can leave a begin marker with no end. Appending
        // a fresh block would give the file two begin markers, and the next run would
        // then delete everything between the first and the real end - the user's rules
        // included.
        var damaged = string.Join(
            "\n",
            GitIgnoreManager.BeginMarker,
            "user-rule/",
            string.Empty);

        var result = CreateManager().ApplyManagedBlock(damaged, Rules);

        Assert.Equal(1, Count(result, GitIgnoreManager.BeginMarker));
        Assert.Equal(1, Count(result, GitIgnoreManager.EndMarker));
        Assert.Contains("user-rule/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveringFromDamage_IsStableAndKeepsUserRules()
    {
        var manager = CreateManager();
        var damaged = string.Join(
            "\n",
            GitIgnoreManager.BeginMarker,
            "user-rule/",
            string.Empty);

        var once = manager.ApplyManagedBlock(damaged, Rules);
        var twice = manager.ApplyManagedBlock(once, Rules);

        // The second pass is where the data loss used to happen.
        Assert.Contains("user-rule/", twice, StringComparison.Ordinal);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void AnEndMarkerBeforeABeginMarker_LosesNothing()
    {
        var damaged = string.Join(
            "\n",
            GitIgnoreManager.EndMarker,
            "user-rule/",
            GitIgnoreManager.BeginMarker,
            "/old",
            GitIgnoreManager.EndMarker,
            string.Empty);

        var result = CreateManager().ApplyManagedBlock(damaged, Rules);

        Assert.Contains("user-rule/", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/old", result, StringComparison.Ordinal);
        Assert.Equal(1, Count(result, GitIgnoreManager.BeginMarker));
    }

    [Fact]
    public void ANewFile_IsWrittenWithLineFeeds()
    {
        // .gitignore is tracked and shared, so its line ending must not depend on
        // which machine happened to run init.
        var result = CreateManager().ApplyManagedBlock(null, Rules);

        Assert.DoesNotContain("\r", result, StringComparison.Ordinal);
    }

    [Fact]
    public void LineFeedInput_StaysLineFeed()
    {
        var result = CreateManager().ApplyManagedBlock("bin/\nobj/\n", Rules);

        Assert.DoesNotContain("\r", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedLineEndings_AreEachKept()
    {
        // Rewriting every existing line in one style would show the whole file as changed
        // in a diff whose only real change is the managed block.
        var result = CreateManager().ApplyManagedBlock("crlf-rule/\r\nlf-rule/\n", Rules);

        Assert.StartsWith("crlf-rule/\r\nlf-rule/\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFile_EndsWithANewline_WhenTheBlockIsLast()
    {
        var manager = CreateManager();

        var once = manager.ApplyManagedBlock("bin/\n", Rules);
        var twice = manager.ApplyManagedBlock(once, Rules);

        Assert.EndsWith(GitIgnoreManager.EndMarker + "\n", once, StringComparison.Ordinal);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void AFileWithoutATrailingNewline_IsSeparatedFromTheBlock()
    {
        // Appending straight after the last character would glue the begin marker onto
        // the user's final rule, corrupting both.
        var result = CreateManager().ApplyManagedBlock("bin/", Rules);

        Assert.StartsWith("bin/\n\n" + GitIgnoreManager.BeginMarker, result, StringComparison.Ordinal);
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
