using RepoHarness.Core.Build;

namespace RepoHarness.Tests;

/// <summary>
/// Which files a clean-rebuild decision compares over. Every tracked file was the first answer and
/// it is the safe one, but it made a documentation edit discard a warm build directory on every
/// leg: measured on a consumer's tree, one markdown file put two legs through a full rebuild, one
/// of them eleven minutes. These pin what narrowing it may and may not do.
/// </summary>
public sealed class BuildInputKindsTests
{
    private static BuildInputKinds Kinds(string type) => BuildAdapters.For(type).InputKinds;

    /// <summary>
    /// The case that opened this: documentation cannot change what a C or C++ build produces, and
    /// sources and the build system's own files can.
    /// </summary>
    [Theory]
    [InlineData("src/app.cpp", true)]
    [InlineData("src/app.h", true)]
    [InlineData("src/boot.S", true)]
    [InlineData("CMakeLists.txt", true)]
    [InlineData("cmake/Toolchain.cmake", true)]
    [InlineData("config.h.in", true)]
    [InlineData("docs/guide.md", false)]
    [InlineData(".claude/skills/x/SKILL.md", false)]
    [InlineData("README.rst", false)]
    public void ACMakeBuild_ComparesOverWhatItCompiles(string path, bool covered)
        => Assert.Equal(covered, Kinds("cmake").Covers(path, StringComparison.Ordinal));

    /// <summary>
    /// The same question has a different answer per language, which is the whole reason the set
    /// comes from the project's type: a .json is a project-system file to one build system and a
    /// test fixture to another.
    /// </summary>
    [Theory]
    [InlineData("cmake", "src/data.json", false)]
    [InlineData("dotnet", "src/app.csproj", true)]
    [InlineData("dotnet", "Directory.Build.props", true)]
    [InlineData("dotnet", "src/app.cs", true)]
    [InlineData("dotnet", "docs/guide.md", false)]
    [InlineData("dart", "lib/main.dart", true)]
    [InlineData("dart", "pubspec.yaml", true)]
    [InlineData("dart", "docs/guide.md", false)]
    public void EachLanguageComparesOverItsOwnKinds(string type, string path, bool covered)
        => Assert.Equal(covered, Kinds(type).Covers(path, StringComparison.Ordinal));

    /// <summary>
    /// A file with no extension is covered whatever the entries say. Measured on a consumer's tree:
    /// VERSION, extensionless, is read with file(READ) at configure time and feeds both the project
    /// version and a compiler predefine. Listing that name, and the next repository's, would be
    /// guessing at a vocabulary; treating "no extension" as unknown costs a clean rebuild when
    /// somebody edits LICENSE, which is rare and is the direction to be wrong in.
    /// </summary>
    [Theory]
    [InlineData("VERSION")]
    [InlineData("LICENSE")]
    [InlineData("src/generated/manifest")]
    public void AFileWithNoExtension_IsAlwaysABuildInput(string path)
    {
        Assert.True(Kinds("cmake").Covers(path, StringComparison.Ordinal));

        // Including against a list somebody wrote themselves, which is the case that matters: an
        // override narrow enough to exclude it would rest the guarantee on their build system.
        Assert.True(new BuildInputKinds([".cpp"]).Covers(path, StringComparison.Ordinal));
    }

    /// <summary>
    /// An entry is an extension or a whole file name, with or without the leading dot, so one list
    /// carries both questions and nobody has to know which kind of entry they wrote.
    /// </summary>
    [Theory]
    [InlineData(".cpp", "src/app.cpp", true)]
    [InlineData("cpp", "src/app.cpp", true)]
    [InlineData(".CPP", "src/app.cpp", true)]
    [InlineData("CMakeLists.txt", "CMakeLists.txt", true)]
    [InlineData("CMakeLists.txt", "nested/CMakeLists.txt", true)]
    [InlineData("CMakeLists.txt", "src/app.txt", false)]
    [InlineData(".cpp", "src/app.h", false)]
    public void AnEntryMatchesAnExtensionOrAWholeName(string entry, string path, bool covered)
        => Assert.Equal(covered, new BuildInputKinds([entry]).Covers(path, StringComparison.Ordinal));

    /// <summary>
    /// Saying nothing is not saying "nothing matters". A set that narrowed to nothing would compare
    /// equal on every build, which is exactly the answer that keeps a stale binary.
    /// </summary>
    [Fact]
    public void AnEmptySet_ComparesOverEverything()
    {
        Assert.False(BuildInputKinds.Everything.Narrows);
        Assert.True(BuildInputKinds.Everything.Covers("docs/guide.md", StringComparison.Ordinal));
        Assert.True(BuildInputKinds.Everything.Covers("anything at all.xyz", StringComparison.Ordinal));
    }
}
