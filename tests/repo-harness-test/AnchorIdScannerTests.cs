using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;

namespace RepoHarness.Tests;

/// <summary>
/// What the scanner does and does not call a citation. The blind spot it exists to close is a
/// measured one: the guard it replaces separates an id from what precedes it with a word boundary,
/// and an id written immediately after an escape is preceded by that escape's own letter, so the
/// guard drops it and an id resolving nowhere is never reported.
/// </summary>
public sealed class AnchorIdScannerTests
{
    private static readonly AnchorIdScanner Scanner = new(AnchorIdRules.From(new AnchorSettings()));

    /// <summary>
    /// A citation that runs into a hyphen at the end of its line - trailing spaces and a carriage
    /// return aside - is cut there, as a wrapped line cuts one; an id with anything after that hyphen,
    /// or none at all, is not.
    /// </summary>
    [Fact]
    public void ACitationRunningIntoAHyphenAtTheEndOfItsLine_IsCut()
    {
        var found = Scanner.Scan("notes.md", "see D-AREA-TOPIC-\nDETAIL and D-AREA-TOPIC-TWO-  \r\nD-AREA-TOPIC-THREE-(x) D-AREA-TOPIC-FOUR\n");

        Assert.Equal(
            [("D-AREA-TOPIC", true), ("D-AREA-TOPIC-TWO", true), ("D-AREA-TOPIC-THREE", false), ("D-AREA-TOPIC-FOUR", false)],
            found.Select(citation => (citation.Id, citation.Cut)));
    }

    [Fact]
    public void AnIdWrittenStraightAfterAnEscape_IsFound()
    {
        // The exact shape the guard drops: a C++ literal whose escape ends in a letter.
        const string Line = @"    stream << ""\nD-SOME-ID: the note"";";

        Assert.Equal(["D-SOME-ID"], Scanner.ScanLine(Line));
    }

    [Theory]
    [InlineData(@"""\nD-FOO-BAR-BAZ""")]
    [InlineData(@"""\tD-FOO-BAR-BAZ""")]
    [InlineData(@"""\rD-FOO-BAR-BAZ""")]
    [InlineData(@"""\x41D-FOO-BAR-BAZ""")]
    [InlineData("see D-FOO-BAR-BAZ for this")]
    [InlineData("D-FOO-BAR-BAZ")]
    [InlineData("`D-FOO-BAR-BAZ`")]
    public void AnEscapeOrAnOrdinarySeparator_LeavesTheIdVisible(string line)
        => Assert.Equal(["D-FOO-BAR-BAZ"], Scanner.ScanLine(line));

    [Fact]
    public void AFourDigitUnicodeEscape_LeavesTheIdVisible()
    {
        // Assembled from two literals on purpose: written as one, a tool that reads this file can
        // turn the escape into the character it names, and the case would then test nothing.
        var line = "\"\\" + "u0041D-FOO-BAR-BAZ\"";

        Assert.Equal(["D-FOO-BAR-BAZ"], Scanner.ScanLine(line));
    }

    [Theory]
    [InlineData("seeD-FOO-BAR-BAZ")]
    [InlineData("word1D-FOO-BAR-BAZ")]
    [InlineData(@"""AD-FOO-BAR-BAZ""")]
    public void AnIdGluedToAWord_IsNotACitation(string line)
        => Assert.Empty(Scanner.ScanLine(line));

    [Fact]
    public void AnEscapedBackslash_IsACharacterAndNotAnEscape()
    {
        // "\\n" is a backslash and the letter n, so the id really is glued to a letter here.
        const string Line = @"""\\nD-FOO-BAR-BAZ""";

        Assert.Empty(Scanner.ScanLine(Line));
    }

    [Fact]
    public void ALongerWordWhoseTailIsIdShaped_IsNotACitation()
    {
        // The live case from a source comment: the tail is id-shaped but the phrase is not an id,
        // and a rule that lifted it would red forever on an innocent comment.
        Assert.Empty(Scanner.ScanLine("// a FIXED-32-BIT-WORD value"));
    }

    [Fact]
    public void AHeadOnName_StaysInformal()
    {
        // One segment after the prefix is not enough to be a citation; two are.
        Assert.Empty(Scanner.ScanLine("D-OPT is informal"));
        Assert.Equal(["D-OPT-ONE"], Scanner.ScanLine("D-OPT-ONE is not"));
    }

    [Fact]
    public void ARejectedCandidate_DoesNotHideARealOneBehindIt()
    {
        var found = Scanner.ScanLine("FIXED-32-BIT-WORD and then D-AREA-TOPIC-REAL");

        Assert.Equal(["D-AREA-TOPIC-REAL"], found);
    }

    [Fact]
    public void EveryCitation_CarriesItsFileAndLine()
    {
        var text = string.Join('\n', ["first", "// D-AREA-TOPIC-ONE", "third", @"""\nD-AREA-TOPIC-TWO"""]);

        var found = Scanner.Scan("src/thing.cpp", text);

        Assert.Equal(
            [("D-AREA-TOPIC-ONE", "src/thing.cpp", 2), ("D-AREA-TOPIC-TWO", "src/thing.cpp", 4)],
            found.Select(citation => (citation.Id, citation.Path, citation.LineNumber)));
    }

    [Fact]
    public void TheShapeComesFromTheConfiguredPrefix_NotFromALiteral()
    {
        var scanner = new AnchorIdScanner(AnchorIdRules.From(new AnchorSettings { IdPrefix = "XY" }));

        Assert.Equal(["XY-AREA-TOPIC"], scanner.ScanLine(@"""\nXY-AREA-TOPIC"""));
        Assert.Empty(scanner.ScanLine("D-AREA-TOPIC"));
    }
}
