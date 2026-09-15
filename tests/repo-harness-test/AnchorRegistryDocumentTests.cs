using System.Text.RegularExpressions;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

public sealed class AnchorRegistryDocumentTests
{
    private const string OpenRow = "| `D-AREA-TOPIC-ONE` | P1 | 🟠 OPEN | first | work | refs |";
    private const string ClosedRow = "| `D-AREA-TOPIC-TWO` | P2 | ✅ CLOSED | second | - | refs |";
    private const string NewRow = "| `D-AREA-TOPIC-NEW` | P3 | ⏳ GATED | third | - | - |";

    private static readonly AnchorIdRules Rules = new("D", 3);

    [Fact]
    public void Parse_ReadsEveryRowOfTheAnchorTable()
    {
        var document = Parse(Registry(OpenRow, ClosedRow));

        Assert.Empty(document.Findings);
        document.EnsureSound("registry.md");
        Assert.Equal(["D-AREA-TOPIC-ONE", "D-AREA-TOPIC-TWO"], document.Rows.Select(row => row.Id));

        var open = document.Rows[0];
        Assert.Equal(7, open.LineNumber);
        Assert.Equal(6, open.CellCount);
        Assert.Equal("P1", open.Priority);
        Assert.Equal("🟠 OPEN", open.Status);
        Assert.Equal("first", open.Trigger);
        Assert.False(open.IsClosed);
        Assert.True(document.Rows[1].IsClosed);
    }

    [Fact]
    public void Parse_ReportsAMissingTable_AndTheFileIsRefused()
    {
        var document = Parse("# A registry with no table\n\nJust prose.\n");

        Assert.Contains(document.Findings, finding =>
            finding.Severity == AnchorFindingSeverity.Fatal && finding.Message.Contains("no anchor table", StringComparison.Ordinal));

        var exception = Assert.Throws<HarnessException>(() => document.EnsureSound("registry.md"));
        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
        Assert.Contains("line 1: no anchor table", exception.Message, StringComparison.Ordinal);
        Assert.Contains("read-anchors --lint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsASecondAnchorTable_AndTheFileIsRefused()
    {
        // With two tables, where a new row belongs would be a guess nobody reading the file could see.
        var text = Registry(OpenRow) + "\n" + AnchorRegistryDocument.TableHeader + "\n" + AnchorRegistryDocument.SeparatorRow + "\n" + ClosedRow + "\n";

        var document = Parse(text);

        Assert.Equal(2, document.Rows.Count);
        Assert.Contains(document.Findings, finding => finding.Message.Contains("2 anchor tables", StringComparison.Ordinal));
        Assert.Throws<HarnessException>(() => document.EnsureSound("registry.md"));
    }

    [Fact]
    public void Parse_ReportsAnAnchorRowOutsideAnyTable_AndTheFileIsRefused()
    {
        var document = Parse(Registry(OpenRow) + "\nA paragraph.\n\n" + ClosedRow + "\n");

        var finding = Assert.Single(document.Findings);
        Assert.Equal(AnchorFindingSeverity.Fatal, finding.Severity);
        Assert.Contains("outside any table", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Rows, row => row.Id == "D-AREA-TOPIC-TWO");
        Assert.Throws<HarnessException>(() => document.EnsureSound("registry.md"));
    }

    [Fact]
    public void Parse_ReportsAnchorRowsHeldByAnotherTable_AndTheFileIsRefused()
    {
        var document = Parse(Registry(OpenRow) + "\n| Name | Value |\n|---|---|\n| `D-OTHER-TABLE-ROW` | v |\n");

        var finding = Assert.Single(document.Findings);
        Assert.Contains("not the anchor table", finding.Message, StringComparison.Ordinal);
        Assert.Throws<HarnessException>(() => document.EnsureSound("registry.md"));
    }

    [Fact]
    public void Parse_IgnoresATableThatHoldsNoAnchors()
    {
        var document = Parse("| Name | Value |\n|---|---|\n| colour | blue |\n\n" + Registry(OpenRow));

        Assert.Empty(document.Findings);
        Assert.Single(document.Rows);
        document.EnsureSound("registry.md");
    }

    [Fact]
    public void Parse_ReadsRowsPastAnHtmlComment_AndWarns_WithoutRefusingTheFile()
    {
        // A renderer ends the table at the comment; the rows below it are still anchors.
        var document = Parse(Registry(OpenRow, "<!-- reviewed -->", ClosedRow));

        Assert.Equal(2, document.Rows.Count);
        var finding = Assert.Single(document.Findings);
        Assert.Equal(AnchorFindingSeverity.Warning, finding.Severity);
        document.EnsureSound("registry.md");
    }

    [Fact]
    public void Parse_ReadsARowWithTheWrongNumberOfCells()
    {
        var row = Assert.Single(Parse(Registry("| `D-AREA-TOPIC-SHORT` | P1 |")).Rows);

        Assert.Equal("D-AREA-TOPIC-SHORT", row.Id);
        Assert.Equal(2, row.CellCount);
        Assert.Equal(string.Empty, row.Status);
    }

    [Fact]
    public void Parse_IgnoresAByteOrderMark()
    {
        Assert.Single(Parse("﻿" + Registry(OpenRow)).Rows);
    }

    [Fact]
    public void AppendRow_AddsAtTheEndOfTheTable_BeforeTheProseThatFollows()
    {
        var document = Parse(Registry(OpenRow, ClosedRow));

        document.AppendRow(NewRow);

        Assert.Contains(ClosedRow + "\n" + NewRow + "\n\nFooter prose.", document.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceRow_AndRemoveRow_TouchOnlyThatLine()
    {
        var replaced = Parse(Registry(OpenRow, ClosedRow));
        replaced.ReplaceRow(replaced.Rows[0], NewRow);

        var removed = Parse(Registry(OpenRow, ClosedRow));
        removed.RemoveRow(removed.Rows[0]);

        Assert.Equal(Registry(NewRow, ClosedRow), replaced.ToText());
        Assert.Equal(Registry(ClosedRow), removed.ToText());
    }

    [Fact]
    public void Changes_KeepTheFilesOwnLineEndings()
    {
        var crlf = Registry(OpenRow, ClosedRow).Replace("\n", "\r\n", StringComparison.Ordinal);

        var appended = Parse(crlf);
        appended.AppendRow(NewRow);

        var replaced = Parse(crlf);
        replaced.ReplaceRow(replaced.Rows[1], NewRow);

        foreach (var text in new[] { appended.ToText(), replaced.ToText() })
        {
            Assert.Contains(NewRow + "\r\n", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex("(?<!\r)\n"), text);
        }
    }

    [Fact]
    public void ADocument_TakesOneChange()
    {
        // Row positions are read at parse time; a second change would land on moved lines.
        var document = Parse(Registry(OpenRow, ClosedRow));
        document.AppendRow(NewRow);

        Assert.Throws<InvalidOperationException>(() => document.RemoveRow(document.Rows[0]));
    }

    [Theory]
    [InlineData(AnchorRegistryKind.Pending, "# Deferred-Anchor Registry\n")]
    [InlineData(AnchorRegistryKind.Done, "# Deferred-Anchor Registry — Done\n")]
    public void TheSkeleton_IsAnEmptyRegistry_ThatParsesCleanly(AnchorRegistryKind kind, string title)
    {
        var settings = new AnchorSettings();
        var skeleton = AnchorRegistrySkeleton.Render(kind, settings);
        var document = Parse(skeleton);

        Assert.StartsWith(title, skeleton, StringComparison.Ordinal);
        Assert.Empty(document.Rows);
        Assert.Empty(document.Findings);
        document.EnsureSound("registry.md");
        Assert.DoesNotContain("\r", skeleton, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", skeleton, StringComparison.Ordinal);
        Assert.EndsWith(AnchorRegistryDocument.SeparatorRow + "\n", skeleton, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSkeleton_SpellsItsExampleWithTheConfiguredRule()
    {
        var skeleton = AnchorRegistrySkeleton.Render(
            AnchorRegistryKind.Pending,
            new AnchorSettings { IdPrefix = "TASK", MinimumIdSegments = 4 });

        Assert.Contains("`TASK-AREA-TOPIC-DETAIL-PART4`", skeleton, StringComparison.Ordinal);
    }

    private static AnchorRegistryDocument Parse(string text) => AnchorRegistryDocument.Parse(text, Rules);

    private static string Registry(params string[] rows)
        => string.Join(
            "\n",
            ["# Registry", string.Empty, "Some prose.", string.Empty, AnchorRegistryDocument.TableHeader, AnchorRegistryDocument.SeparatorRow, .. rows, string.Empty, "Footer prose."])
            + "\n";
}
