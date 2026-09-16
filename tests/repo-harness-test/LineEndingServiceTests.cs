using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.LineEndings;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// fix-line-endings against a real repository and a real <c>.gitattributes</c>. The policy is never
/// restated in the test either: every expectation below is what the attributes file declares, read
/// back through git, so a test that passed while the tool invented its own policy is not possible.
/// </summary>
public sealed class LineEndingServiceTests
{
    private const string Attributes =
        """
        *.txt text eol=lf
        *.win text eol=crlf
        *.blob -text
        """;

    [Fact]
    public async Task All_RewritesExactlyTheFilesTheAttributesFileNames()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var report = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: false),
            cancellationToken);

        Assert.Equal(
            ["declared.txt", "declared.win"],
            report.Changed.Select(change => change.Path).Order(StringComparer.Ordinal));

        Assert.Equal("one\ntwo\n", Read(temp, "declared.txt"));
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "declared.win"));

        // Declared binary, and undeclared: neither is the policy's business, so neither moved.
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "kept.blob"));
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "undeclared.dat"));
    }

    [Fact]
    public async Task Check_RefusesAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var report = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: true),
            cancellationToken);

        var outcome = LineEndingReports.Render(report, json: false);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(2, report.Changed.Count);

        // The refusal is the whole of what --check does: the bytes on disk are untouched.
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "declared.txt"));
        Assert.Equal("one\ntwo\n", Read(temp, "declared.win"));
    }

    [Fact]
    public async Task ASecondRun_ChangesNothing_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var service = Service(harness);
        var request = new LineEndingRequest(LineEndingScope.All, CheckOnly: false);

        await service.ApplyAsync(temp.Path, request, cancellationToken);
        var again = await service.ApplyAsync(temp.Path, request, cancellationToken);

        Assert.Empty(again.Changed);
        Assert.Equal(HarnessExit.Success, LineEndingReports.Render(again, json: false).ExitCode);
    }

    [Fact]
    public async Task AnExcludedPath_IsLeftAlone()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, new LineEndingSettings { Exclude = ["generated"] });

        var report = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: false),
            cancellationToken);

        Assert.DoesNotContain("generated/made.txt", report.Changed.Select(change => change.Path));
        Assert.Equal("one\r\ntwo\r\n", Read(temp, Path.Combine("generated", "made.txt")));
        Assert.True(report.Excluded > 0);
    }

    [Fact]
    public async Task Changed_CoversTheWorkingSetAndNothingElse()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        // Everything is committed, so nothing is staged or unstaged and the working set is empty.
        await harness.CommitAllAsync(temp.Path, "fixtures", cancellationToken);

        var settled = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.Changed, CheckOnly: false),
            cancellationToken);

        Assert.Empty(settled.Changed);

        File.WriteAllText(temp.Combine("declared.txt"), "three\r\nfour\r\n");

        var working = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.Changed, CheckOnly: false),
            cancellationToken);

        Assert.Equal("declared.txt", Assert.Single(working.Changed).Path);
        Assert.Equal("three\nfour\n", Read(temp, "declared.txt"));
    }

    private static LineEndingService Service(HarnessFactory harness)
        => new(harness.ContextLoader, harness.GitClient, harness.FileSystem);

    private static string Read(TempDirectory temp, string relativePath)
        => File.ReadAllText(temp.Combine(relativePath));

    private static async Task<HarnessFactory> PrepareAsync(
        TempDirectory temp,
        LineEndingSettings? lineEndings = null)
    {
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(
            temp.Path,
            cancellationToken,
            new HarnessConfig { LineEndings = lineEndings ?? new LineEndingSettings() });

        Write(temp, ".gitattributes", Attributes + "\n");

        // Each fixture starts on the wrong side of what the attributes file declares for it.
        Write(temp, "declared.txt", "one\r\ntwo\r\n");
        Write(temp, "declared.win", "one\ntwo\n");
        Write(temp, "kept.blob", "one\r\ntwo\r\n");
        Write(temp, "undeclared.dat", "one\r\ntwo\r\n");

        // Only where a test declares an exclusion: otherwise it is an ordinary .txt the policy reaches.
        if (lineEndings is not null)
        {
            Write(temp, Path.Combine("generated", "made.txt"), "one\r\ntwo\r\n");
        }

        // Tracked, because --all covers what git tracks. Adding does not touch the working tree.
        await harness.RunGitAsync(temp.Path, ["add", "-A"], cancellationToken);

        return harness;
    }

    /// <summary>Writes exactly these bytes, so no writer's own newline handling changes the fixture.</summary>
    private static void Write(TempDirectory temp, string relativePath, string contents)
    {
        var full = temp.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(contents));
    }
}
