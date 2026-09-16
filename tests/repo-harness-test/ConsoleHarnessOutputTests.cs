using RepoHarness.Core.Output;

namespace RepoHarness.Tests;

public sealed class ConsoleHarnessOutputTests
{
    [Fact]
    public void SuccessAndProgress_GoToStandardOutput()
    {
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Info("init", "created config.json");
        output.Ok("init", "initialised");

        Assert.Equal(["init: created config.json", "init: OK - initialised"], Lines(standardOutput));
        Assert.Empty(Lines(standardError));
    }

    /// <summary>
    /// What anything that would interrupt a document asks before it does. A password prompt written
    /// into standard output is read as part of the document a script is parsing; the same prompt is
    /// also the one thing that must not be skipped silently, so the answer has to be exact.
    /// </summary>
    [Fact]
    public void IsDataOnly_SaysWhetherADocumentIsBeingWritten_AndStopsSayingSoAfterwards()
    {
        var (output, _, _) = Create(verbose: false);

        Assert.False(output.IsDataOnly);

        using (output.DataOnly())
        {
            Assert.True(output.IsDataOnly);
        }

        Assert.False(output.IsDataOnly);
    }

    [Fact]
    public void FailuresAndWarnings_GoToStandardError_SoOutputStaysPipeable()
    {
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Warn("push", "nothing to push");
        output.Fail("push", "rejected");

        Assert.Equal(["push: WARN - nothing to push", "push: FAIL - rejected"], Lines(standardError));
        Assert.Empty(Lines(standardOutput));
    }

    [Fact]
    public void Detail_IsShownOnlyWhenVerbose()
    {
        var (quiet, quietOutput, _) = Create(verbose: false);
        var (verbose, verboseOutput, _) = Create(verbose: true);

        quiet.Detail("build", "configuring");
        verbose.Detail("build", "configuring");

        Assert.False(quiet.IsVerbose);
        Assert.True(verbose.IsVerbose);
        Assert.Empty(Lines(quietOutput));
        Assert.Equal(["build: configuring"], Lines(verboseOutput));
    }

    [Fact]
    public void ChildProcessOutput_IsPassedThroughUnprefixed_OnItsOwnStream()
    {
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Raw("compiling main.c");
        output.RawError("warning: unused variable");

        Assert.Equal(["compiling main.c"], Lines(standardOutput));
        Assert.Equal(["warning: unused variable"], Lines(standardError));
    }

    [Fact]
    public void ACommandsResult_IsWrittenUnprefixed_ToStandardOutput()
    {
        // A listing or a JSON document must be readable by another program as it stands.
        var (output, standardOutput, standardError) = Create(verbose: false);

        output.Data("[\n  \"D-AREA-TOPIC-DETAIL\"\n]");

        Assert.Equal("[\n  \"D-AREA-TOPIC-DETAIL\"\n]" + standardOutput.NewLine, standardOutput.ToString());
        Assert.Empty(Lines(standardError));
    }

    [Fact]
    public void ConcurrentReports_NeverInterleaveWithinALine()
    {
        // Parallel legs report at the same moment, and half of one line inside another is
        // unreadable exactly when it matters.
        var (output, standardOutput, _) = Create(verbose: false);

        Parallel.For(0, 400, index => output.Info("leg", new string((char)('a' + (index % 26)), 64)));

        var lines = Lines(standardOutput);
        Assert.Equal(400, lines.Count);
        Assert.All(lines, line => Assert.Matches(@"^leg: ([a-z])\1{63}$", line));
    }

    private static (ConsoleHarnessOutput Output, StringWriter StandardOutput, StringWriter StandardError) Create(bool verbose)
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        return (new ConsoleHarnessOutput(standardOutput, standardError, verbose), standardOutput, standardError);
    }

    private static List<string> Lines(StringWriter writer)
        => [.. writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries)];
}
