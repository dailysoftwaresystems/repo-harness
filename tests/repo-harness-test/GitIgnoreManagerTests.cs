using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;

namespace RepoHarness.Tests;

public sealed class GitIgnoreManagerTests
{
    private static readonly string[] Rules = ["/a", "!/a/.gitkeep"];

    private static GitIgnoreManager CreateManager()
        => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));

    [Fact]
    public void ApplyManagedBlock_WritesBlock_WhenFileIsEmpty()
    {
        var result = CreateManager().ApplyManagedBlock(null, Rules);

        Assert.Contains(GitIgnoreManager.BeginMarker, result, StringComparison.Ordinal);
        Assert.Contains(GitIgnoreManager.EndMarker, result, StringComparison.Ordinal);
        Assert.Contains("/a", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyManagedBlock_PreservesExistingRules()
    {
        var existing = "# mine\nbin/\nobj/\n";

        var result = CreateManager().ApplyManagedBlock(existing, Rules);

        Assert.Contains("# mine", result, StringComparison.Ordinal);
        Assert.Contains("bin/", result, StringComparison.Ordinal);
        Assert.Contains("obj/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyManagedBlock_ReplacesPreviousBlock_RatherThanAppendingASecond()
    {
        var manager = CreateManager();
        var once = manager.ApplyManagedBlock("bin/\n", ["/old"]);

        var twice = manager.ApplyManagedBlock(once, ["/new"]);

        Assert.Equal(1, CountOccurrences(twice, GitIgnoreManager.BeginMarker));
        Assert.Contains("/new", twice, StringComparison.Ordinal);
        Assert.DoesNotContain("/old", twice, StringComparison.Ordinal);
        Assert.Contains("bin/", twice, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyManagedBlock_IsStable_WhenRulesAreUnchanged()
    {
        var manager = CreateManager();
        var once = manager.ApplyManagedBlock("bin/\n", Rules);

        var twice = manager.ApplyManagedBlock(once, Rules);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void ApplyManagedBlock_KeepsCrlf_WhenTheFileUsesCrlf()
    {
        var result = CreateManager().ApplyManagedBlock("bin/\r\nobj/\r\n", Rules);

        // Every line ending must still be CRLF: rewriting a CRLF file with LF endings
        // would show every line as changed in a diff over a two line addition.
        var withoutCrlf = result.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", withoutCrlf, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_ReportsChanged_OnlyWhenContentActuallyChanges()
    {
        using var temp = new TempDirectory();
        var manager = CreateManager();
        var path = temp.Combine(".gitignore");

        Assert.True(manager.Update(path, Rules));
        Assert.False(manager.Update(path, Rules));
        Assert.True(manager.Update(path, ["/different"]));
    }

    private static int CountOccurrences(string haystack, string needle)
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
