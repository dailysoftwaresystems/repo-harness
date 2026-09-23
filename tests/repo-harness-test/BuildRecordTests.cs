using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

/// <summary>
/// The record a build keeps in its directory: read back as written, read as an earlier version wrote
/// it, and written so an earlier version still reads what it knew.
/// </summary>
public sealed class BuildRecordTests
{
    /// <summary>Every part comes back as it was written, a path holding spaces among them.</summary>
    [Fact]
    public void ARecord_ReadsBackAsItWasWritten()
    {
        var newest = new WrittenFile("obj/deep dir/app.o", new DateTime(2026, 9, 23, 10, 0, 5, DateTimeKind.Utc).AddTicks(1234567));
        var began = new DateTime(2026, 9, 23, 9, 58, 0, DateTimeKind.Utc).AddTicks(7);
        var record = new BuildRecord(
            "a moving tree: the previous build's inputs could not be shown to hold still",
            "x86_64-gcc-debug",
            newest,
            new Dictionary<string, string> { ["src/app.cpp"] = "abc123", ["docs/a guide.md"] = "def456" },
            new Dictionary<string, DateTime> { ["src/app.cpp"] = began });

        var read = BuildRecord.Parse(record.Write());

        Assert.Equal(record.Unordered, read.Unordered);
        Assert.Equal(record.Variant, read.Variant);
        Assert.Equal(newest, read.Newest);
        Assert.Equal(record.Contents.OrderBy(pair => pair.Key), read.Contents.OrderBy(pair => pair.Key));
        Assert.Equal(began, read.Written["src/app.cpp"]);
        Assert.Equal(DateTimeKind.Utc, read.Written["src/app.cpp"].Kind);
    }

    /// <summary>
    /// A record 0.5.8 wrote: its mark, its variant and a fingerprint line per input, nothing newer. It
    /// reads as a build that never said it finished, with nothing recorded of when its inputs were written.
    /// </summary>
    [Fact]
    public void ARecordAnEarlierVersionWrote_ReadsAsItMeant()
    {
        var read = BuildRecord.Parse("clean\nx86_64-gcc-debug\nin abc123 src/app.cpp\nin def456 VERSION\n");

        Assert.Null(read.Unordered);
        Assert.Equal("x86_64-gcc-debug", read.Variant);
        Assert.Null(read.Newest);
        Assert.Equal("abc123", read.Contents["src/app.cpp"]);
        Assert.Equal("def456", read.Contents["VERSION"]);
        Assert.Empty(read.Written);
    }

    /// <summary>
    /// A record an earlier version marked unordered said so and never why, and still starts the next
    /// build from clean, saying that much.
    /// </summary>
    [Fact]
    public void ARecordAnEarlierVersionMarkedUnordered_IsUnordered_WithoutAReason()
    {
        var read = BuildRecord.Parse("clock-stepped\r\nx86_64-gcc-debug\r\nin abc123 src/app.cpp\r\n");

        Assert.Equal(BuildRecord.Unexplained, read.Unordered);
        Assert.Equal("x86_64-gcc-debug", read.Variant);
    }

    /// <summary>
    /// Written so an earlier version still reads it: an unordered record keeps the mark 0.5.8 looks for on
    /// its first line, and each input's fingerprint is the line it always was.
    /// </summary>
    [Fact]
    public void AnUnorderedRecord_KeepsTheWordsAnEarlierVersionReads()
    {
        var lines = new BuildRecord(
                "a clock step: the previous build's 'build' phase spanned one",
                "x86_64-gcc-debug",
                new WrittenFile("bin/app", DateTime.UtcNow),
                new Dictionary<string, string> { ["src/app.cpp"] = "abc123" },
                new Dictionary<string, DateTime> { ["src/app.cpp"] = DateTime.UtcNow })
            .Write()
            .Split('\n');

        Assert.Equal("clock-stepped", lines[0]);
        Assert.Contains("in abc123 src/app.cpp", lines);
    }

    /// <summary>
    /// A first line saying neither mark - a record from a later version, or one damaged - is read as
    /// unordered, which costs one clean rebuild and nothing else.
    /// </summary>
    [Theory]
    [InlineData("from-the-future\nx86_64-gcc-debug\nin abc123 src/app.cpp\n")]
    [InlineData("")]
    public void AFirstLineSayingNeither_IsUnordered(string text)
        => Assert.StartsWith("an unreadable record:", BuildRecord.Parse(text).Unordered, StringComparison.Ordinal);
}
