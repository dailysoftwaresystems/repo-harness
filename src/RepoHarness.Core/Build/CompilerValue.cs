using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Build;

/// <summary>
/// A compiler variable's value - <c>CC</c>, <c>CXX</c> - read as CMake reads it: the program it
/// starts, and the arguments CMake records beside that program.
/// </summary>
/// <param name="Program">The program the value starts.</param>
/// <param name="Arguments">The words after it, which CMake records as <c>CMAKE_&lt;LANG&gt;_COMPILER_ARG1</c>.</param>
/// <remarks>
/// One reading for everything that asks which compiler a build uses - the survey, and the guard on
/// a configured build directory. Read two ways, <c>ccache clang</c> was a program the survey
/// required and a value the guard compared whole with the <c>/usr/bin/ccache</c> CMake cached, and
/// every rebuild of the directory refused the run.
/// </remarks>
public sealed record CompilerValue(string Program, IReadOnlyList<string> Arguments)
{
    /// <summary>The variable a build names its C compiler in.</summary>
    public const string C = "CC";

    /// <summary>The variable a build names its C++ compiler in.</summary>
    public const string Cxx = "CXX";

    /// <summary>Both, in the order a build reads them.</summary>
    public static IReadOnlyList<string> Variables { get; } = [C, Cxx];

    /// <summary>
    /// What <paramref name="value"/> starts, where the value alone can say: the whole value when it
    /// holds no space, and its first word, with the rest as its arguments, when that word is a name.
    /// </summary>
    /// <param name="value">The variable's value, or <see langword="null"/> when it is not set.</param>
    /// <returns>The reading, or <see langword="null"/> where the value is not set or cannot be read.</returns>
    /// <remarks>
    /// Measured on CMake 4.3: <c>CC="ccache gcc"</c> caches <c>/usr/bin/ccache</c> as the compiler
    /// and <c>" gcc"</c> as its argument, and <c>CC="gcc -m64"</c> caches <c>/usr/bin/gcc</c> and
    /// <c>" -m64"</c>. A value with a space whose first word is a path is the one that cannot be
    /// read: in <c>C:\Program Files\LLVM\bin\clang-cl.exe</c> that word is half a file name, and
    /// only the host knows whether the whole value is one. Left to CMake rather than guessed at.
    /// </remarks>
    public static CompilerValue? Read(string? value)
    {
        var words = value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];

        return words switch
        {
            [var whole] => new CompilerValue(whole, []),
            [var first, .. var rest] when !ProcessRunner.IsPath(first) => new CompilerValue(first, rest),
            _ => null,
        };
    }

    /// <summary>The value as a build would be told it, for a message to quote.</summary>
    public override string ToString() => string.Join(' ', [Program, .. Arguments]);
}
