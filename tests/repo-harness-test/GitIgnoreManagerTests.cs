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

    [Theory]
    [InlineData("/wt/")]
    [InlineData("wt/")]
    [InlineData("/wt")]
    [InlineData("/wt/*")]
    [InlineData("/wt/   ")]
    public void FindOverlaps_ReportsAHandWrittenRuleForAManagedPath_WhateverItsShape(string handWritten)
    {
        // Two differently-shaped rules for one path is a state nothing else points out. A
        // whole-directory rule written by hand also silently cancels any placeholder the managed
        // block re-includes inside it.
        var manager = CreateManager();
        var content = manager.ApplyManagedBlock($"bin/\n{handWritten}\n", ["/wt/"]);

        var overlap = Assert.Single(manager.FindOverlaps(content, ["/wt/"]));

        Assert.Equal(2, overlap.LineNumber);
        Assert.Equal(handWritten.Trim(), overlap.Rule);
        Assert.Equal("wt", overlap.Path);
        Assert.False(overlap.ReIncludes);
        Assert.False(overlap.Contradicts);
    }

    [Fact]
    public void FindOverlaps_SaysWhenAHandWrittenRuleReIncludesWhatTheBlockIgnores()
    {
        // The overlap that can undo the managed block: whichever of two contradicting rules comes
        // later in the file wins, and this one would put a slot's secrets back in reach of git add.
        var manager = CreateManager();
        var content = manager.ApplyManagedBlock("!/secrets/*\n", ["/secrets/*", "!/secrets/.gitkeep"]);

        var overlap = Assert.Single(manager.FindOverlaps(content, ["/secrets/*", "!/secrets/.gitkeep"]));

        Assert.True(overlap.ReIncludes);
        Assert.True(overlap.Contradicts);
        Assert.Equal("secrets", overlap.Path);
    }

    [Fact]
    public void FindOverlaps_SaysWhenAHandWrittenRuleIgnoresWhatTheBlockReIncludes()
    {
        // Contradiction the other way round: written after the block, this rule stops the
        // placeholder from ever being tracked, and nothing else would say why.
        var manager = CreateManager();
        var content = manager.ApplyManagedBlock("/secrets/.gitkeep\n", ["/secrets/*", "!/secrets/.gitkeep"]);

        var overlap = Assert.Single(manager.FindOverlaps(content, ["/secrets/*", "!/secrets/.gitkeep"]));

        Assert.False(overlap.ReIncludes);
        Assert.True(overlap.Contradicts);
        Assert.Equal("secrets/.gitkeep", overlap.Path);
    }

    [Fact]
    public void FindOverlaps_CallsAnIdenticalReInclude_ARepetition_NotAContradiction()
    {
        // Both rules re-include the placeholder. Reporting that as a conflict would send a reader
        // looking for a disagreement that is not there.
        var manager = CreateManager();
        var content = manager.ApplyManagedBlock("!/secrets/.gitkeep\n", ["/secrets/*", "!/secrets/.gitkeep"]);

        var overlap = Assert.Single(manager.FindOverlaps(content, ["/secrets/*", "!/secrets/.gitkeep"]));

        Assert.True(overlap.ReIncludes);
        Assert.False(overlap.Contradicts);
    }

    [Fact]
    public void FindOverlaps_IgnoresTheManagedBlockItself_Comments_BlankLines_AndUnrelatedRules()
    {
        var manager = CreateManager();
        var content = manager.ApplyManagedBlock("# /wt/ is ours\n\nbin/\n/wt-other/\n", ["/wt/"]);

        Assert.Empty(manager.FindOverlaps(content, ["/wt/"]));
    }

    [Fact]
    public void FindOverlaps_CountsLinesAsTheFileHasThem_WithWindowsLineEndings()
    {
        // The note names a line a reader will look for, in a file that may use either ending.
        var manager = CreateManager();
        var content = manager.ApplyManagedBlock("bin/\r\nobj/\r\n/wt/\r\n", ["/wt/"]);

        var overlap = Assert.Single(manager.FindOverlaps(content, ["/wt/"]));

        Assert.Equal(3, overlap.LineNumber);
        Assert.Equal("/wt/", overlap.Rule);
    }

    [Fact]
    public void FindOverlaps_MatchesNothingAsAGlob_AsItsContractSays()
    {
        // Exact paths only. A rule reaching a managed path only through a wildcard is not reported,
        // and the interface says so rather than implying a completeness it does not have.
        var manager = CreateManager();
        var content = manager.ApplyManagedBlock("**/wt/\nw*/\n", ["/wt/"]);

        Assert.Empty(manager.FindOverlaps(content, ["/wt/"]));
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
