using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

public sealed class LineTextTests
{
    /// <summary>
    /// Each line reads as the process runner hands it on: a carriage return before a line feed, or at the very
    /// end, is the line's ending and goes; one inside a line is the line's own and stays.
    /// </summary>
    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb\n")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\nb\r", "a\nb")]
    [InlineData("50%\r100%\n", "50%\r100%\n")]
    [InlineData("", "")]
    public void EachLine_ReadsWithoutTheCarriageReturnThatEndedIt(string written, string read)
        => Assert.Equal(read, LineText.Of(written));
}
