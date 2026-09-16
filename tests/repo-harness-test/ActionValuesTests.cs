using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>The values an action reads, and what keeps a secret out of everything that prints.</summary>
public sealed class ActionValuesTests
{
    [Fact]
    public async Task ReadAsync_MergesEveryFileInADirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "CORPUS_ROOT=corpus\n# a comment\n\nJOBS=8\n");
        temp.WriteFile("env/b.env", "TARGET=all\n");

        var values = await ReadAsync(temp);

        Assert.Equal("corpus", values.Values["CORPUS_ROOT"]);
        Assert.Equal("8", values.Values["JOBS"]);
        Assert.Equal("all", values.Values["TARGET"]);
    }

    [Fact]
    public async Task ReadAsync_StripsAMatchingPairOfQuotes_AndKeepsAHashInsideAValue()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "PADDED=\"  spaced  \"\nSINGLE='quoted'\nTOKEN=ab#cd\n");

        var values = await ReadAsync(temp);

        Assert.Equal("  spaced  ", values.Values["PADDED"]);
        Assert.Equal("quoted", values.Values["SINGLE"]);

        // A generated credential contains '#' often enough that stripping it would produce a value
        // that is almost right, which fails later and somewhere else.
        Assert.Equal("ab#cd", values.Values["TOKEN"]);
    }

    [Fact]
    public async Task ReadAsync_Refuses_ANameDefinedTwiceInOneDirectory()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "JOBS=8\n");
        temp.WriteFile("env/b.env", "JOBS=4\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => ReadAsync(temp));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("'JOBS' is defined in", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nothing can say which value a run used", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_Refuses_ANameDefinedInBothDirectories()
    {
        // The dangerous one: redaction masks the secret's value while the run used the plain one.
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "TOKEN=plain\n");
        temp.WriteFile("secrets/a.env", "TOKEN=s3cret\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => ReadAsync(temp));

        Assert.Contains("'TOKEN' is defined in", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_Refuses_ALineThatDefinesNothing()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "JOBS 8\n=nothing\nexport TOKEN=x\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => ReadAsync(temp));

        Assert.Contains("line 1: no '='", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 2: the name before '=' is empty", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 3: 'export TOKEN' is not a name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ReadsNothing_WhenTheDirectoriesAreAbsent()
    {
        using var temp = new TempDirectory();

        var values = await ReadAsync(temp);

        Assert.Empty(values.Values);
        Assert.Empty(values.SecretNames);
    }

    [Fact]
    public async Task ASecretNeverReachesARenderedCommand_AnErrorMessage_OrThePlainValues()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("env/a.env", "USER=dev\n");
        temp.WriteFile("secrets/a.env", "TOKEN=s3cret-value\nPREFIX=s3cret\n");

        var values = await ReadAsync(temp);

        Assert.Equal(["PREFIX", "TOKEN"], values.SecretNames.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("s3cret", string.Join('|', values.Values.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", values.ToString(), StringComparison.Ordinal);

        var rendered = values.RedactAll(["curl", "--header", "Authorization: s3cret-value", "--user", "dev"]);

        Assert.Equal(["curl", "--header", "Authorization: ***", "--user", "dev"], rendered);

        // Longest first, so masking the shorter secret does not leave the longer one's tail behind.
        Assert.Equal(
            "curl failed: *** was rejected",
            values.Redact("curl failed: s3cret-value was rejected"));
    }

    [Fact]
    public void RevealSecrets_IsTheOnlyWayToTheRawValues()
    {
        var values = new ActionValues(
            new Dictionary<string, string> { ["USER"] = "dev" },
            new Dictionary<string, string> { ["TOKEN"] = "s3cret" });

        Assert.True(values.IsSecret("token"));
        Assert.False(values.Values.ContainsKey("TOKEN"));
        Assert.Equal("s3cret", values.RevealSecrets()["TOKEN"]);

        // The copy handed out cannot be used to reach back into the values.
        Assert.NotSame(values.RevealSecrets(), values.RevealSecrets());
    }

    [Fact]
    public void Redact_LeavesTextAloneWhenThereAreNoSecrets()
    {
        Assert.Equal("cmake --build build", ActionValues.Empty.Redact("cmake --build build"));
        Assert.Equal(string.Empty, ActionValues.Empty.Redact(null));
    }

    private static Task<ActionValues> ReadAsync(TempDirectory temp)
        => new ActionValuesReader(
                new PhysicalFileSystem(FilePermissionsFactory.Create()),
                new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false))
            .ReadAsync(temp.Combine("env"), temp.Combine("secrets"), TestContext.Current.CancellationToken);
}
