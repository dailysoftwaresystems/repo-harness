using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// Every log is scoped to a run id, so two ids that collide put two legs' output in one file and
/// one leg's result can be read as another's. These pin the shape and the uniqueness.
/// </summary>
public sealed class RunIdTests
{
    [Fact]
    public void New_IsTheDeclaredShape_AndReadsBack()
    {
        var runId = RunId.New();

        Assert.Matches("^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$", runId.Value);
        Assert.Equal(runId, RunId.Parse(runId.Value));
        Assert.Equal(runId.Value, runId.ToString());
    }

    [Fact]
    public void Value_IsSafeAsADirectoryName()
    {
        var value = RunId.New().Value;

        Assert.Equal(-1, value.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.Equal(value, Path.GetFileName(value));
    }

    [Fact]
    public void TwoIds_NeverCollide_AndNeverGoBackwards()
    {
        // Generated from every thread at once, as several legs starting together would.
        var ids = new RunId[256];
        Parallel.For(0, ids.Length, index => ids[index] = RunId.New());

        Assert.Equal(ids.Length, ids.Select(id => id.Value).Distinct(StringComparer.Ordinal).Count());

        var first = RunId.New();
        var second = RunId.New();
        Assert.True(second.StartedUtc >= first.StartedUtc, "a later id carried an earlier timestamp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("20250101-120000")]
    [InlineData("20250101-120000-DEADBEEF")]
    [InlineData("20250101-120000-deadbee")]
    [InlineData("20250101-120000-deadbeeg")]
    [InlineData("2025011-1200000-deadbeef")]
    [InlineData("20250101_120000-deadbeef")]
    [InlineData("../../etc-120000-deadbeef")]
    public void Parse_RefusesAnythingElse(string text)
    {
        // An id decides where logs are written. Anything accepted here would write them somewhere
        // nobody named; uppercase hex is refused because it is one directory on Windows and two
        // on Linux.
        Assert.False(RunId.TryParse(text, out _));
        Assert.Throws<FormatException>(() => RunId.Parse(text));
    }

    [Fact]
    public void Parse_KeepsTheTimestampItWasWrittenWith()
    {
        var runId = RunId.Parse("20250915-104501-0a1b2c3d");

        Assert.Equal(new DateTimeOffset(2025, 9, 15, 10, 45, 1, TimeSpan.Zero), runId.StartedUtc);
    }
}
