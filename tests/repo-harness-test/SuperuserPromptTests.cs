using RepoHarness.Core.Secrets;

namespace RepoHarness.Tests;

/// <summary>
/// The two parts of asking somebody for a superuser password that do not need a console: whether
/// anybody may be asked at all, and what the keys they press spell.
/// </summary>
public sealed class SuperuserPromptTests
{
    [Theory]
    // Nobody is there to ask. The flag makes no difference, and saying it did would send the reader
    // to drop it and be refused again in the same words.
    [InlineData(true, false, true, PromptAvailability.Unavailable)]
    [InlineData(false, false, true, PromptAvailability.Unavailable)]
    [InlineData(true, true, false, PromptAvailability.Unavailable)]
    [InlineData(false, true, false, PromptAvailability.Unavailable)]

    // Somebody is there, and only then does the flag decide anything.
    [InlineData(true, false, false, PromptAvailability.Available)]
    [InlineData(false, false, false, PromptAvailability.Suppressed)]
    public void Decide_SettlesWhetherAnybodyIsThere_BeforeItReadsTheFlag(
        bool prompting,
        bool writingDocument,
        bool inputRedirected,
        PromptAvailability expected)
        => Assert.Equal(expected, ISuperuserPrompt.Decide(prompting, writingDocument, inputRedirected));

    [Fact]
    public void Read_ReturnsWhatWasTyped_AndShowsAMaskInsteadOfIt()
    {
        var echo = new StringWriter();

        var typed = MaskedPassword.Read(Keys(Ch('p'), Ch('w'), Ch('d'), Enter), echo);

        Assert.Equal("pwd", typed, StringComparer.Ordinal);

        // One mark per character and nothing else: the answer never reaches the screen.
        Assert.Equal("***" + Environment.NewLine, echo.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public void Read_TakesBackOneCharacterForEachBackspace()
    {
        var typed = MaskedPassword.Read(Keys(Ch('a'), Ch('b'), Back, Ch('c'), Enter), new StringWriter());

        Assert.Equal("ac", typed, StringComparer.Ordinal);
    }

    /// <summary>
    /// Backspace with nothing typed must not walk back over the prompt itself, which is on the same
    /// line. A password one character short of what was typed is indistinguishable from a wrong one,
    /// and the wrong one is then remembered for the whole host.
    /// </summary>
    [Fact]
    public void Read_WithNothingTypedYet_TakesNothingBack()
    {
        var echo = new StringWriter();

        var typed = MaskedPassword.Read(Keys(Back, Back, Ch('a'), Enter), echo);

        Assert.Equal("a", typed, StringComparer.Ordinal);
        Assert.Equal("*" + Environment.NewLine, echo.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public void Read_IgnoresAKeyThatTypesNothing()
    {
        // An arrow key arrives carrying a control character; taken as typed it would put something in
        // the password that nobody pressed.
        var typed = MaskedPassword.Read(Keys(Ch('a'), Arrow, Ch('b'), Enter), new StringWriter());

        Assert.Equal("ab", typed, StringComparer.Ordinal);
    }

    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    private static readonly ConsoleKeyInfo Back = new('\b', ConsoleKey.Backspace, false, false, false);

    private static readonly ConsoleKeyInfo Arrow = new('\0', ConsoleKey.LeftArrow, false, false, false);

    private static ConsoleKeyInfo Ch(char character) => new(character, ConsoleKey.A, false, false, false);

    private static Func<ConsoleKeyInfo> Keys(params ConsoleKeyInfo[] keys)
    {
        var pressed = new Queue<ConsoleKeyInfo>(keys);

        return pressed.Dequeue;
    }
}
