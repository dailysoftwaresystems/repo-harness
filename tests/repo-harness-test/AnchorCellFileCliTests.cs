using System.Text;
using System.Text.Json;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The <c>--&lt;cell&gt;-file</c> options, through the built CLI. They exist because a command line is
/// bounded and a row is not: Windows caps one at 32,767 characters, and the longest row measured in
/// the repository these commands serve is 78 KB, so without them the tool cannot write the rows it is
/// meant to maintain.
/// </summary>
public sealed class AnchorCellFileCliTests
{
    private const string One = "D-AREA-TOPIC-ONE";

    /// <summary>The Windows command-line bound, which a cell given inline cannot exceed.</summary>
    private const int CommandLineBound = 32767;

    [Fact]
    public async Task ACellFarLargerThanACommandLine_RoundTripsThroughAFile()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var trigger = LongCell();
        Assert.True(trigger.Length > CommandLineBound * 2, $"the cell is only {trigger.Length} characters");

        // Written with a trailing newline, as every editor writes one: it is not part of the cell.
        var file = temp.Combine("trigger.txt");
        await File.WriteAllTextAsync(file, trigger + "\n", new UTF8Encoding(false), cancellationToken);

        var written = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger-file", file, "-C", temp.Path],
            cancellationToken);

        Assert.Equal(HarnessExit.Success, written.ExitCode);

        var read = await CliRunner.RunAsync(["read-anchor", One, "--json", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, read.ExitCode);

        using var json = JsonDocument.Parse(read.StandardOutput);
        var anchor = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(trigger, anchor.GetProperty("trigger").GetString());
    }

    /// <summary>
    /// A cell read from a file keeps every character of every line - a run, a tab, a no-break space - and
    /// only its line breaks go: each with the whitespace either side of it, the file's last among them.
    /// </summary>
    [Fact]
    public async Task ACellFile_IsStoredAsWritten_ButForItsLineBreaks()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var file = temp.Combine("trigger.txt");
        await File.WriteAllTextAsync(file, "inputs  : held still\t4  +  38\u00a0\u00a0kept\n  and the next line\n", new UTF8Encoding(false), cancellationToken);

        var written = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger-file", file, "-C", temp.Path],
            cancellationToken);

        Assert.Equal(HarnessExit.Success, written.ExitCode);

        var read = await CliRunner.RunAsync(["read-anchor", One, "--json", "-C", temp.Path], cancellationToken);

        using var json = JsonDocument.Parse(read.StandardOutput);
        var anchor = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("inputs  : held still\t4  +  38\u00a0\u00a0kept and the next line", anchor.GetProperty("trigger").GetString());
    }

    /// <summary>
    /// A cell file that is there and cannot be read - another program holds it, or this user may not read it - is
    /// refused by name, as one that is not there is: it is the file the option named, and nothing wrong with the tool.
    /// </summary>
    [Fact]
    public void ACellFileThatCannotBeRead_IsRefusedByName()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("trigger.txt");
        File.WriteAllText(file, "held");

        var refusal = Assert.Throws<HarnessException>(() => AnchorCellInputs.Resolve(
            new HeldByAnother(new HarnessFactory().FileSystem),
            new AnchorCellInput("Trigger", "--trigger", "--trigger-file", Inline: null, File: file)));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.StartsWith($"--trigger-file names '{file}', which could not be read: ", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cell file that is not UTF-8 is refused, naming the file and the byte, rather than read leniently:
    /// a Latin-1 é was stored as U+FFFD, and the character its author wrote was gone with nothing said. A
    /// byte-order mark is refused rather than dropped: kept, it would open the cell with an invisible character.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x20, 0x74 }, "is not UTF-8: the byte at offset 3 (0xE9) does not form a UTF-8 character")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x63, 0x61, 0x66, 0xC3, 0xA9 }, "opens with a byte-order mark")]
    public async Task ACellFileThatIsNotPlainUtf8_IsRefused_NamingWhatIsWrong(byte[] bytes, string expected)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var file = temp.Combine("trigger.txt");
        await File.WriteAllBytesAsync(file, bytes, cancellationToken);

        var result = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger-file", file, "-C", temp.Path],
            cancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains($"--trigger-file names '{file}', which", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(expected, result.StandardError, StringComparison.Ordinal);

        var read = await CliRunner.RunAsync(["read-anchor", One, "-C", temp.Path], cancellationToken);
        Assert.NotEqual(HarnessExit.Success, read.ExitCode);
    }

    [Fact]
    public async Task ACellGivenBothWays_IsRefused_AndTheRefusalNamesTheCell()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var file = temp.WriteFile("trigger.txt", "from the file");

        var result = await CliRunner.RunAsync(
            [
                "write-anchor", One,
                "--priority", "P1",
                "--trigger", "inline",
                "--trigger-file", file,
                "-C", temp.Path,
            ],
            cancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--trigger and --trigger-file", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Trigger cell", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCellWithAnInlineOption_HasAFileTwin()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var priority = temp.WriteFile("priority.txt", "P2\n");
        var status = temp.WriteFile("status.txt", "gated\n");
        var trigger = temp.WriteFile("trigger.txt", "why it matters\n");
        var closing = temp.WriteFile("closing.txt", "what remains\n");
        var crossRefs = temp.WriteFile("cross-refs.txt", "where it is cited\n");

        var written = await CliRunner.RunAsync(
            [
                "write-anchor", One,
                "--priority-file", priority,
                "--status-file", status,
                "--trigger-file", trigger,
                "--closing-file", closing,
                "--cross-refs-file", crossRefs,
                "-C", temp.Path,
            ],
            cancellationToken);

        Assert.Equal(HarnessExit.Success, written.ExitCode);

        var read = await CliRunner.RunAsync(["read-anchor", One, "--json", "-C", temp.Path], cancellationToken);
        using var json = JsonDocument.Parse(read.StandardOutput);
        var anchor = Assert.Single(json.RootElement.EnumerateArray());

        // Each file's trailing newline was dropped, so every cell reads exactly as its file spelled it.
        Assert.Equal("P2", anchor.GetProperty("priority").GetString());
        Assert.Equal("⏳ GATED", anchor.GetProperty("status").GetString());
        Assert.Equal("why it matters", anchor.GetProperty("trigger").GetString());
        Assert.Equal("what remains", anchor.GetProperty("closing").GetString());
        Assert.Equal("where it is cited", anchor.GetProperty("cross_refs").GetString());
    }

    [Fact]
    public async Task SetAnchor_TakesTheSameFileOptions()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var written = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger", "first", "-C", temp.Path],
            cancellationToken);
        Assert.Equal(HarnessExit.Success, written.ExitCode);

        var file = temp.WriteFile("closing.txt", LongCell() + "\n");

        var changed = await CliRunner.RunAsync(
            ["set-anchor", One, "--closing-file", file, "-C", temp.Path],
            cancellationToken);

        Assert.Equal(HarnessExit.Success, changed.ExitCode);

        var read = await CliRunner.RunAsync(["read-anchor", One, "--json", "-C", temp.Path], cancellationToken);
        using var json = JsonDocument.Parse(read.StandardOutput);
        Assert.Equal(LongCell(), Assert.Single(json.RootElement.EnumerateArray()).GetProperty("closing").GetString());
    }

    [Fact]
    public async Task AFileOptionNamingNothing_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger-file", temp.Combine("absent.txt"), "-C", temp.Path],
            cancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("absent.txt", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cell with no pipe, no escape and single spaces, so what comes back is what went in: the
    /// table collapses runs of whitespace, and comparing against a value it would have changed would
    /// test the comparison rather than the round trip.
    /// </summary>
    private static string LongCell()
        => string.Join(' ', Enumerable.Range(0, 2500).Select(index => $"segment-{index:D5}-of-a-very-long-cell"));

    private static async Task PrepareAsync(TempDirectory temp)
        => await new HarnessFactory().InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken);

    /// <summary>A disk on which every file is held open by another program.</summary>
    private sealed class HeldByAnother(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
            => throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
    }
}
