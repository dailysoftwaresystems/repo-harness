using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// Which compilers CMake configured a build directory with, read from its file API - and why nothing
/// was read, where nothing was.
/// </summary>
public sealed class CMakeToolchainReaderTests
{
    /// <summary>The query every configure answers, at the path the file API reads it from, written once.</summary>
    [Fact]
    public void Ask_WritesTheQuery_WhereCMakeReadsIt()
    {
        using var temp = new TempDirectory();
        var build = temp.Combine("build", "x86_64-gcc-debug");

        Reader().Ask(build);
        Reader().Ask(build);

        Assert.True(File.Exists(Path.Combine(build, ".cmake", "api", "v1", "query", "toolchains-v1")));
    }

    /// <summary>
    /// The reply CMake 4.3 wrote for a C and C++ project, read back one compiler per language; the
    /// resource compiler it lists with no id is no compiler a build can be held to.
    /// </summary>
    [Fact]
    public void Read_ReadsEachLanguagesCompiler_FromTheReplyCMakeWrote()
    {
        using var temp = new TempDirectory();
        var build = WriteReply(temp, "index-2026-09-19T16-16-05-0385.json", "toolchains-v1-e005b0ba7508d5391e6e.json", ("GNU", "13.2.0"));

        var reading = Reader().Read(build);

        Assert.Null(reading.Unread);
        Assert.Equal([new CompilerFact("C", "GNU", "13.2.0"), new CompilerFact("CXX", "GNU", "13.2.0")], reading.Compilers);
    }

    /// <summary>
    /// The newest index answers: named for the moment it was written, it sorts last. An older one
    /// left beside it describes a configure that no longer holds.
    /// </summary>
    [Fact]
    public void Read_TakesTheNewestIndex()
    {
        using var temp = new TempDirectory();
        WriteReply(temp, "index-2026-09-18T09-00-00-0000.json", "toolchains-v1-old.json", ("Clang", "17.0.6"));
        var build = WriteReply(temp, "index-2026-09-19T16-16-05-0385.json", "toolchains-v1-new.json", ("MSVC", "19.51.36231"));

        Assert.All(Reader().Read(build).Compilers, compiler => Assert.Equal("MSVC", compiler.Id));
    }

    /// <summary>
    /// Every way CMake said nothing is said as that, with why - never as a compiler, and never as an
    /// empty list nobody can tell from "none were configured".
    /// </summary>
    [Theory]
    [InlineData("none", "CMake wrote no file API answer there, which it does from version 3.20")]
    [InlineData("no-index", "CMake wrote no file API answer there, which it does from version 3.20")]
    [InlineData("no-answer", "CMake's file API index holds no answer about the compilers")]
    [InlineData("error", "CMake answered that it could not say: unknown request kind")]
    [InlineData("missing-file", "CMake's answer about the compilers names 'toolchains-v1-gone.json', which is not there")]
    [InlineData("malformed", "CMake's file API answer could not be read")]
    public void Read_SaysWhyItReadNothing(string state, string expected)
    {
        using var temp = new TempDirectory();
        var build = temp.Combine("build");
        var replies = Path.Combine(build, ".cmake", "api", "v1", "reply");

        switch (state)
        {
            case "none":
                Directory.CreateDirectory(build);
                break;
            case "no-index":
                Directory.CreateDirectory(replies);
                break;
            case "no-answer":
                temp.WriteFile(Path.Combine(replies, "index-1.json"), """{ "reply": {} }""");
                break;
            case "error":
                temp.WriteFile(Path.Combine(replies, "index-1.json"), """{ "reply": { "toolchains-v1": { "error": "unknown request kind" } } }""");
                break;
            case "missing-file":
                temp.WriteFile(Path.Combine(replies, "index-1.json"), """{ "reply": { "toolchains-v1": { "jsonFile": "toolchains-v1-gone.json" } } }""");
                break;
            default:
                temp.WriteFile(Path.Combine(replies, "index-1.json"), "{ not json");
                break;
        }

        var reading = Reader().Read(build);

        Assert.Empty(reading.Compilers);
        Assert.StartsWith(expected, reading.Unread, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a leg testing a build it does not make was configured with: read for a CMake project,
    /// and nothing for one CMake never configures.
    /// </summary>
    [Fact]
    public void Configured_ReadsACMakeProjectsDirectory_AndNothingForAnotherKind()
    {
        using var temp = new TempDirectory();
        var build = WriteReply(temp, "index-1.json", "toolchains-v1-a.json", ("GNU", "13.2.0"));

        Assert.NotEmpty(Reader().Configured(new ProjectConfig { Name = "app", Type = "cmake", Path = "." }, build)!.Compilers);
        Assert.Null(Reader().Configured(new ProjectConfig { Name = "app", Type = "dotnet", Path = "." }, build));
        Assert.Null(Reader().Configured(null, build));
    }

    /// <summary>
    /// Read for a configure it asked, only what that configure answered: an answer the directory held
    /// before is an earlier configure's, whatever the times in the names say. A configure that failed
    /// says why, in CMake's words where it wrote its error index; one that wrote nothing says that.
    /// </summary>
    [Fact]
    public void Read_ForAConfigureItAsked_TakesOnlyWhatThatConfigureAnswered()
    {
        using var temp = new TempDirectory();
        var build = WriteReply(temp, "index-2026-09-19T16-16-05-0385.json", "toolchains-v1-a.json", ("GNU", "13.2.0"));
        var replies = Path.Combine(build, ".cmake", "api", "v1", "reply");
        var reader = Reader();

        var silent = reader.Read(build, reader.Ask(build));

        Assert.Empty(silent.Compilers);
        Assert.Equal("this configure wrote no file API answer, which CMake does from version 3.20", silent.Unread);

        var asked = reader.Ask(build);
        temp.WriteFile(Path.Combine(replies, "error-2026-09-19T16-17-00-0001.json"), """{ "reply": { "toolchains-v1": { "error": "no buildsystem generated" } } }""");

        var failed = reader.Read(build, asked);

        Assert.Empty(failed.Compilers);
        Assert.Equal("CMake answered that it could not say: no buildsystem generated", failed.Unread);
        Assert.Equal("GNU", Assert.Single(reader.Read(build).Compilers, compiler => compiler.Language == "C").Id);

        // A newer answer is read even where the clock that named it had stepped back.
        asked = reader.Ask(build);
        WriteReply(temp, "index-2026-01-01T00-00-00-0000.json", "toolchains-v1-b.json", ("Clang", "17.0.6"));

        Assert.All(reader.Read(build, asked).Compilers, compiler => Assert.Equal("Clang", compiler.Id));
    }

    /// <summary>
    /// A leg's line names each compiler once, with the languages it serves - C and C++ from one MSVC
    /// are one compiler - and a version CMake did not report is left out rather than guessed.
    /// </summary>
    [Fact]
    public void Describe_NamesEachCompilerOnce_WithTheLanguagesItServes()
    {
        Assert.Equal(
            "compiler: MSVC 19.51.36231 (C, CXX)",
            CompilerFacts.Describe([new("C", "MSVC", "19.51.36231"), new("CXX", "MSVC", "19.51.36231")]));
        Assert.Equal(
            "compiler: GNU 13.2.0 (C), Clang 17.0.6 (CXX), GNU (Fortran)",
            CompilerFacts.Describe([new("C", "GNU", "13.2.0"), new("CXX", "Clang", "17.0.6"), new("Fortran", "GNU", string.Empty)]));
        Assert.Null(CompilerFacts.Describe([]));
    }

    private static CMakeToolchainReader Reader() => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));

    /// <summary>
    /// Writes an index and the toolchains answer it names, in the shape CMake 4.3 wrote them: C and
    /// C++ from <paramref name="compiler"/>, and a resource compiler with no id.
    /// </summary>
    private static string WriteReply(TempDirectory temp, string index, string answer, (string Id, string Version) compiler)
    {
        var build = temp.Combine("build");
        var replies = Path.Combine(build, ".cmake", "api", "v1", "reply");

        temp.WriteFile(Path.Combine(replies, index), $$"""
            {
              "objects": [ { "jsonFile": "{{answer}}", "kind": "toolchains", "version": { "major": 1, "minor": 1 } } ],
              "reply": { "toolchains-v1": { "jsonFile": "{{answer}}", "kind": "toolchains", "version": { "major": 1, "minor": 1 } } }
            }
            """);

        temp.WriteFile(Path.Combine(replies, answer), $$"""
            {
              "kind": "toolchains",
              "version": { "major": 1, "minor": 1 },
              "toolchains": [
                { "language": "C", "compiler": { "id": "{{compiler.Id}}", "version": "{{compiler.Version}}", "path": "C:/Strawberry/c/bin/gcc.exe", "implicit": {} } },
                { "language": "CXX", "compiler": { "id": "{{compiler.Id}}", "version": "{{compiler.Version}}", "path": "C:/Strawberry/c/bin/c++.exe", "implicit": {} } },
                { "language": "RC", "compiler": { "path": "C:/Strawberry/c/bin/windres.exe", "implicit": {} } }
              ]
            }
            """);

        return build;
    }
}
