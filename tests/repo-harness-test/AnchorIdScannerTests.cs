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

    /// <summary>
    /// An id cut at its first hyphen or its second is too short to be a citation on its own line, so
    /// it is one where the next line carries on with the segments that make it one - past that line's
    /// indentation and comment marker. 'D-' before 'day' is a wrapped D-day, and a fragment with no
    /// line after it carries on with nothing: neither is an id.
    /// </summary>
    [Fact]
    public void AnIdCutAtItsFirstOrSecondHyphen_IsCut_WhereTheNextLineMakesItACitation()
    {
        var found = Scanner.Scan(
            "notes.md",
            "see D-PP-\r\n  // PRESCAN here\nplan D-\nday off\nsee (D-\nFF3-30) there\nend D-AREA-\n\nlast D-PP-");

        Assert.Equal(
            [("D-PP", 1, true), ("D", 5, true)],
            found.Select(citation => (citation.Id, citation.LineNumber, citation.Cut)));
        Assert.Empty(Scanner.ScanLine("see D-PP-"));
    }

    /// <summary>
    /// An id cut just before a hyphen - the hyphen opening the next line, past its indentation and a
    /// comment's marker, carrying it on into a row's id - is cut too: a whole citation that would
    /// otherwise resolve to the shorter id it spells, and a stub too short to be one. A list's '- ' and
    /// an option's '--' carry nothing on, and a stub the next line does not make a citation is no id.
    /// </summary>
    [Fact]
    public void AnIdCutJustBeforeAHyphen_IsCut()
    {
        var found = Scanner.Scan(
            "notes.md",
            "see D-LK6-14\r\n  // -INTEGRATION-PAYLOAD here\nsee D-PP\n-PRESCAN\nsee D\n-PP-PRESCAN\n"
            + "see D-AREA-TOPIC\n- a list item\nsee D-AREA-TOPIC-TWO\n--flag\nplan D\n-1 point\nsee D-XX\nYY, no hyphen at the break\n"
            + "see D-AREA-TOPIC-MID here\n-INTEGRATION does not carry a citation the line went on past\nsee D-AREA-TOPIC-END",
            Rows("D-LK6-14-INTEGRATION-PAYLOAD", "D-PP-PRESCAN", "D-AREA-TOPIC-MID-INTEGRATION"));

        Assert.Equal(
            [("D-LK6-14", "D-LK6-14", 1, true), ("D-PP", "D-PP", 3, true), ("D", "D", 5, true),
             ("D-AREA-TOPIC", "D-AREA-TOPIC", 7, false), ("D-AREA-TOPIC-TWO", "D-AREA-TOPIC-TWO", 9, false),
             ("D-AREA-TOPIC-MID", "D-AREA-TOPIC-MID", 15, false), ("D-AREA-TOPIC-END", "D-AREA-TOPIC-END", 17, false)],
            found.Select(citation => (citation.Id, citation.Written, citation.LineNumber, citation.Cut)));
    }

    /// <summary>
    /// A line ending in a whole id and the next opening with a hyphen are no cut where the two do not
    /// join into a row's id: the hyphen opens an option, a figure, or a line a diff removed as often as
    /// it carries an id on - and a stub the next line would carry on into no row is no id.
    /// </summary>
    [Fact]
    public void AnIdFollowedByALineOpeningWithAHyphen_IsNoCut_WhereTheTwoJoinIntoNoRow()
    {
        var found = Scanner.Scan(
            "notes.md",
            "    # D-BUILD-WARNINGS\n    -Wall\nset by D-RANGE-LIMITS\n(-40 to 85 C).\n+// Implements D-LK6-14\n-old_function();\nsee D-PP\n-PRESCAN\n",
            Rows("D-BUILD-WARNINGS", "D-RANGE-LIMITS", "D-LK6-14"));

        Assert.Equal(
            [("D-BUILD-WARNINGS", 1, false), ("D-RANGE-LIMITS", 3, false), ("D-LK6-14", 5, false)],
            found.Select(citation => (citation.Id, citation.LineNumber, citation.Cut)));
        Assert.All(Scanner.Scan("notes.md", "see D-LK6-14\n-INTEGRATION-PAYLOAD\n"), citation => Assert.False(citation.Cut));
    }

    /// <summary>
    /// A fragment glued to the word before it is no citation, as a whole id glued there is not; and a
    /// citation cut at its end is reported once, not again as the shorter fragment it ends in.
    /// </summary>
    [Fact]
    public void AGluedFragment_IsNoCut_AndACutCitationIsReportedOnce()
    {
        var found = Scanner.Scan("notes.md", "XD-PP-\nPRESCAN\nsee D-AA-D-\nPRE-SCAN\n");

        Assert.Equal([("D-AA-D", 3, true)], found.Select(citation => (citation.Id, citation.LineNumber, citation.Cut)));
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

    /// <summary>The ids the registries hold, compared as read-anchor compares them.</summary>
    private static HashSet<string> Rows(params string[] ids) => ids.ToHashSet(AnchorIdMatch.Comparer);
}
