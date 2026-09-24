using RepoHarness.Core.Anchors;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

public sealed class AnchorStatusTests
{
    [Theory]
    [InlineData("open", AnchorState.Open)]
    [InlineData("GATED", AnchorState.Gated)]
    [InlineData(" Disclosed ", AnchorState.Disclosed)]
    [InlineData("closed", AnchorState.Closed)]
    [InlineData("🟠 OPEN", AnchorState.Open)]
    [InlineData("✅ CLOSED", AnchorState.Closed)]
    public void TryParse_AcceptsTheWordInAnyCase_OrTheExactCell(string input, AnchorState expected)
    {
        Assert.True(AnchorStatus.TryParse(input, out var state));
        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("close")]
    [InlineData("✅")]
    [InlineData("CLOSED ✅")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_AcceptsNoOtherSpelling(string? input)
    {
        // Every extra spelling is one more thing each reader of a registry has to know.
        Assert.False(AnchorStatus.TryParse(input, out _));
    }

    [Fact]
    public void Render_WritesTheGlyphThenTheWord()
    {
        Assert.Equal(["🟠 OPEN", "⏳ GATED", "🔵 DISCLOSED", "✅ CLOSED"], AnchorStatus.Cells);
        Assert.Equal("🔵 DISCLOSED", AnchorStatus.Render(AnchorState.Disclosed));
    }

    [Theory]
    [InlineData("✅ CLOSED", true)]
    [InlineData("**✅ CLOSED 2026-01-02**", true)]
    [InlineData("  _✅_ closed", true)]
    [InlineData("🟠 OPEN", false)]
    [InlineData("🟣 A STATUS NOBODY ANTICIPATED", false)]
    [InlineData("🟠 OPEN, with one ✅ half done", false)]
    [InlineData("CLOSED", false)]
    public void IsClosed_ReadsOnlyTheMarkThatOpensTheCell(string cell, bool closed)
    {
        // An unknown glyph reads as open: a row wrongly open stays visible as work, while a row
        // wrongly closed would vanish from every count.
        Assert.Equal(closed, AnchorStatus.IsClosed(cell));
    }

    [Fact]
    public void IsDisclosed_ReadsOnlyTheMarkThatOpensTheCell()
    {
        Assert.True(AnchorStatus.IsDisclosed("🔵 DISCLOSED"));
        Assert.False(AnchorStatus.IsDisclosed("🟠 OPEN, disclosed 🔵 later"));
    }

    [Fact]
    public void IsCanonical_AcceptsOnlyTheExactSpellings()
    {
        Assert.True(AnchorStatus.IsCanonical(" ⏳ GATED "));
        Assert.False(AnchorStatus.IsCanonical("⏳ gated"));
        Assert.False(AnchorStatus.IsCanonical("✅ CLOSED 2026-01-02"));
    }

    /// <summary>
    /// A Status and a Trigger state one verdict where both read closed, or neither does, emphasis ignored as
    /// the closed test ignores it; each other pair states two, and says which reads closed.
    /// </summary>
    [Theory]
    [InlineData("✅ CLOSED", "✅ **CLOSED 2026-09-23** - fixed", null)]
    [InlineData("🟠 OPEN", "tokens expire mid-request", null)]
    [InlineData("✅ CLOSED", "tokens expire mid-request", "the Status reads closed")]
    [InlineData("🟠 OPEN", "✅ **CLOSED** - fixed", "the Trigger opens with the closed mark")]
    [InlineData("⏳ GATED", "**✅ CLOSED**", "the Trigger opens with the closed mark")]
    [InlineData("✅ CLOSED", "CLOSED in words, with no mark", "the Status reads closed")]
    public void SplitVerdict_IsAPairThatReadsClosedOnOneSideOnly(string status, string trigger, string? expected)
    {
        var split = AnchorStatus.SplitVerdict(status, trigger);

        if (expected is null)
        {
            Assert.Null(split);
            return;
        }

        Assert.NotNull(split);
        Assert.StartsWith(expected, split, StringComparison.Ordinal);
    }
}

public sealed class AnchorPriorityTests
{
    [Theory]
    [InlineData("P0", "P0")]
    [InlineData("p3", "P3")]
    [InlineData(" P5 ", "P5")]
    public void TryNormalize_AcceptsABandInEitherCase(string input, string expected)
    {
        Assert.True(AnchorPriority.TryNormalize(input, out var band));
        Assert.Equal(expected, band);
    }

    [Theory]
    [InlineData("P6")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(null)]
    public void TryNormalize_RefusesAnythingElse(string? input)
    {
        Assert.False(AnchorPriority.TryNormalize(input, out _));
    }
}

public sealed class AnchorIdRulesTests
{
    private static readonly AnchorIdRules Rules = new("D", 3);

    [Theory]
    [InlineData("D-AREA-TOPIC-DETAIL")]
    [InlineData("D-A1-b2-C_3")]
    [InlineData("D-CSUBSET-PTR-TO-NARROW-INT-TRUNCATE")]
    public void IsMintable_AcceptsThePrefixAndEnoughSegments(string id)
    {
        Assert.True(Rules.IsMintable(id));
    }

    [Theory]
    [InlineData("D-AREA-TOPIC")]
    [InlineData("D-area-TOPIC-DETAIL")]
    [InlineData("X-AREA-TOPIC-DETAIL")]
    [InlineData("D-AREA--DETAIL")]
    [InlineData("D-AREA TOPIC-DETAIL")]
    [InlineData("D-AREA-TOPIC-DETAIL\n")]
    public void IsMintable_RefusesANewIdTheRuleDoesNotAllow(string id)
    {
        Assert.False(Rules.IsMintable(id));
    }

    [Theory]
    [InlineData("D-OPT")]
    [InlineData("D-AREA-topic")]
    public void IsWellFormed_AcceptsExistingIds_TheMintingRuleWouldRefuse(string id)
    {
        // An id already in a registry is maintained whatever rule was in force when it was written.
        Assert.False(Rules.IsMintable(id));
        Assert.True(Rules.IsWellFormed(id));
    }

    [Theory]
    [InlineData("D-")]
    [InlineData("D-has space")]
    [InlineData("D-A|B")]
    public void IsWellFormed_RefusesWhatNoRegistryCouldHold(string id)
    {
        Assert.False(Rules.IsWellFormed(id));
    }

    [Fact]
    public void TheRules_FollowTheConfiguration()
    {
        var rules = new AnchorIdRules("TASK", 1);

        Assert.True(rules.IsMintable("TASK-ONE"));
        Assert.False(rules.IsMintable("D-AREA-TOPIC-DETAIL"));
        Assert.True(rules.IsMintable(rules.Example()));
    }

    [Theory]
    [InlineData("`D-AREA-TOPIC-DETAIL`", "D-AREA-TOPIC-DETAIL")]
    [InlineData("~~`D-AREA-TOPIC-DETAIL`~~ retired", "D-AREA-TOPIC-DETAIL")]
    [InlineData("**D-AREA.SUB-DETAIL**", "D-AREA.SUB-DETAIL")]
    [InlineData(" `D7` ", "D7")]
    [InlineData("   ", "<blank>")]
    public void Identify_FindsTheIdThroughDecoration(string cell, string expected)
    {
        Assert.Equal(expected, Rules.Identify(cell));
    }

    [Fact]
    public void Example_IsAnIdTheRulesWouldMint()
    {
        var wide = new AnchorIdRules("X", 5);

        Assert.True(Rules.IsMintable(Rules.Example()));
        Assert.True(wide.IsMintable(wide.Example()));
        Assert.Equal(5, wide.Example().Split('-').Length - 1);
    }

    [Fact]
    public void IsBareBacktickedId_AcceptsOnlyOneIdInBackticks()
    {
        Assert.True(Rules.IsBareBacktickedId(" `D-AREA-TOPIC-DETAIL` "));
        Assert.False(Rules.IsBareBacktickedId("D-AREA-TOPIC-DETAIL"));
        Assert.False(Rules.IsBareBacktickedId("`D-AREA-TOPIC-DETAIL` and `D-OTHER-ONE-TWO`"));
    }
}

public sealed class AnchorCellsTests
{
    [Fact]
    public void Split_TreatsAnEscapedPipeAsText()
    {
        var pieces = AnchorCells.Split(@"| `D-A-B-C` | a \| b | c |");

        Assert.Equal(["", " `D-A-B-C` ", " a | b ", " c ", ""], pieces);
    }

    [Fact]
    public void Format_EscapesPipes_AndCollapsesLineBreaks()
    {
        // Unescaped, the pipe would add a column; the line break would split the row in two.
        Assert.Equal(" a \\| b c\t d ", AnchorCells.Format("a | b\nc\t d", "Trigger"));
    }

    /// <summary>
    /// Every character of a line is kept as given - a run of spaces, a tab, no-break spaces - since a cell's
    /// runs are often its evidence: quoted tool output, aligned figures. Each was stored as one space.
    /// </summary>
    [Theory]
    [InlineData("line one  keeps  its runs")]
    [InlineData("a\tb")]
    [InlineData("4  +  38")]
    [InlineData("two\u00a0\u00a0no-break\u00a0spaces")]
    public void Format_KeepsEveryCharacterOfALine(string value)
    {
        Assert.Equal($" {value} ", AnchorCells.Format(value, "Trigger"));
    }

    /// <summary>
    /// A line break goes, with the whitespace either side of it, as one space, at every boundary a reader
    /// of the file might split a line at, and a blank line vanishes: a row is one physical line.
    /// </summary>
    [Theory]
    [InlineData("a  \r\n  b")]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("a\vb")]
    [InlineData("a\fb")]
    [InlineData("a\u001cb")]
    [InlineData("a\u001db")]
    [InlineData("a\u001eb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\n\n \n\tb")]
    [InlineData("\n  a\n\nb  \n")]
    public void Flatten_CollapsesEveryLineBreak_WithTheWhitespaceEitherSide(string value)
    {
        Assert.Equal("a b", AnchorCells.Flatten(value));
    }

    [Fact]
    public void Format_WritesAnEmptyValueAsOneSpace()
    {
        Assert.Equal(" ", AnchorCells.Format(null, "Closing work"));
        Assert.Equal(" ", AnchorCells.Format("  \n ", "Closing work"));
    }

    [Fact]
    public void Format_RefusesAPipeEscapedByHand_AndSaysWhatToDoInstead()
    {
        var exception = Assert.Throws<HarnessException>(() => AnchorCells.Format(@"a \| b", "Trigger"));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Contains("read-anchor --json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[|]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACellReadAndWrittenBack_IsUnchanged()
    {
        // set-anchor carries cells through this round trip.
        const string Line = @"| `D-A-B-C` | a \| b |";

        var value = AnchorCells.Split(Line)[2].Trim();

        Assert.Equal(AnchorCells.RawCells(Line)[1], AnchorCells.Format(value, "Trigger"));
    }

    [Fact]
    public void RawCells_KeepsEscapesAndPadding()
    {
        Assert.Equal([" `D-A-B-C` ", @"  a \| b ", " c "], AnchorCells.RawCells("| `D-A-B-C` |  a \\| b | c |\r"));
    }

    [Fact]
    public void StripDecoration_RemovesEmphasis_ButKeepsCase()
    {
        Assert.Equal("D-Mixed-Case text", AnchorCells.StripDecoration("**~~`D-Mixed-Case`~~**   text"));
    }
}

public sealed class AnchorRegistryTests
{
    private static readonly AnchorRegistries Registries = new(
        new AnchorRegistry(AnchorRegistryKind.Pending, "pending.md", "pending.md", IsIgnored: false),
        new AnchorRegistry(AnchorRegistryKind.Done, "done.md", "done.md", IsIgnored: false));

    [Theory]
    [InlineData("✅ CLOSED", AnchorRegistryKind.Done)]
    [InlineData("**✅ CLOSED 2026-01-02**", AnchorRegistryKind.Done)]
    [InlineData("🟠 OPEN", AnchorRegistryKind.Pending)]
    [InlineData("⏳ GATED", AnchorRegistryKind.Pending)]
    [InlineData("🔵 DISCLOSED", AnchorRegistryKind.Pending)]
    [InlineData("🟣 A STATUS NOBODY ANTICIPATED", AnchorRegistryKind.Pending)]
    public void HomeOf_IsTheDoneRegistry_OnlyForAClosedRow(string status, AnchorRegistryKind expected)
    {
        Assert.Equal(expected, Registries.HomeOf(status).Kind);
    }

    [Fact]
    public void Misfiling_IsReported_OnlyForARowOutsideItsHome()
    {
        // Placement and both checks ask the same question, so a row a command wrote is never misfiled.
        var closed = Row("| `D-AREA-TOPIC-ONE` | P1 | ✅ CLOSED | t | - | - |");
        var open = Row("| `D-AREA-TOPIC-TWO` | P1 | 🟠 OPEN | t | - | - |");

        Assert.Null(Registries.Done.Misfiling(closed));
        Assert.Null(Registries.Pending.Misfiling(open));
        Assert.Contains(
            "closed anchor 'D-AREA-TOPIC-ONE' is in the pending registry",
            Registries.Pending.Misfiling(closed)?.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "live anchor 'D-AREA-TOPIC-TWO' is in the done registry",
            Registries.Done.Misfiling(open)?.Message,
            StringComparison.Ordinal);
    }

    private static AnchorRow Row(string line)
        => AnchorRegistryDocument.Parse(
            $"{AnchorRegistryDocument.TableHeader}\n{AnchorRegistryDocument.SeparatorRow}\n{line}\n",
            new AnchorIdRules("D", 3)).Rows[0];
}

public sealed class AnchorRegistryLocatorTests
{
    [Theory]
    [InlineData(".plans/pending.md", ".plans/pending.md")]
    [InlineData("./.plans/pending.md", ".plans/pending.md")]
    [InlineData(@".plans\pending.md", ".plans/pending.md")]
    [InlineData(".plans//./pending.md", ".plans/pending.md")]
    [InlineData("././pending.md", "pending.md")]
    public void Normalize_SpellsEveryWayOfWritingOnePath_TheSameWay(string configured, string expected)
    {
        // The validator compares these spellings and git is handed them, so they must agree.
        Assert.Equal(expected, AnchorRegistryLocator.Normalize(configured));
    }
}
