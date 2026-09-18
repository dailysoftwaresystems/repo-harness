using RepoHarness.Core.Build;

namespace RepoHarness.Tests;

/// <summary>How a compiler variable's value is read: as CMake reads it, and never guessed at.</summary>
public sealed class CompilerValueTests
{
    /// <summary>
    /// The whole value when it holds no space; its first word, and the rest as the words after it,
    /// when that word is a name - what CMake caches as the compiler and its ARG1.
    /// </summary>
    [Theory]
    [InlineData("gcc", "gcc", "")]
    [InlineData("/usr/bin/gcc", "/usr/bin/gcc", "")]
    [InlineData("ccache gcc", "ccache", "gcc")]
    [InlineData("gcc -m32", "gcc", "-m32")]
    [InlineData("  ccache  gcc  -m64 ", "ccache", "gcc -m64")]
    public void AValue_IsReadAsItsProgramAndTheWordsAfterIt(string value, string program, string arguments)
    {
        var read = CompilerValue.Read(value);

        Assert.NotNull(read);
        Assert.Equal(program, read.Program);
        Assert.Equal(arguments, string.Join(' ', read.Arguments));
    }

    /// <summary>
    /// A value with a space whose first word is a path cannot be read from the value alone - that
    /// word may be half a file name - so it is left to CMake, as an unset one is.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\LLVM\bin\clang-cl.exe")]
    [InlineData("/opt/my tools/cc")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AValueOnlyCMakeCanRead_IsNotRead(string? value)
        => Assert.Null(CompilerValue.Read(value));
}
