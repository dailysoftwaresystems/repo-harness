using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
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
    /// The reply CMake 4.3 wrote for a C++ project whose C only a subproject enables - as googletest's
    /// own project() enables it - measured with MSVC: C with the compiler's path and no id, which CMake
    /// identified in that subdirectory alone. C is identified from CMake's own record of it, which names
    /// the same compiler; the resource compiler, which CMake never identifies, is named unidentified,
    /// with why, and never read as a compiler.
    /// </summary>
    [Fact]
    public void Read_IdentifiesALanguageOnlyASubprojectEnabled_FromCMakesOwnRecordOfIt()
    {
        using var temp = new TempDirectory();
        var build = WriteSubprojectReply(temp);

        WriteRecord(build, "C", $"set(CMAKE_C_COMPILER \"{Cl}\")\nset(CMAKE_C_COMPILER_ARG1 \"\")\nset(CMAKE_C_COMPILER_ID \"MSVC\")\nset(CMAKE_C_COMPILER_VERSION \"19.51.36257.0\")\r\n");
        WriteRecord(build, "RC", $"set(CMAKE_RC_COMPILER \"{Rc}\")\nset(CMAKE_RC_COMPILER_ARG1 \"\")\n");

        var reading = Reader().Read(build);

        Assert.Null(reading.Unread);
        Assert.Equal([new CompilerFact("C", "MSVC", "19.51.36257.0"), new CompilerFact("CXX", "MSVC", "19.51.36257.0")], reading.Compilers);
        Assert.Equal("RC", Assert.Single(reading.Unidentified).Key);
        Assert.EndsWith("names no id either", reading.Unidentified["RC"], StringComparison.Ordinal);
    }

    /// <summary>
    /// Such a language is identified from a record of the compiler the answer names, however the answer
    /// names it: under a Visual Studio generator it names no compiler for the language at all, measured
    /// with Visual Studio 18 2026, and a toolchain file's value is kept in the answer as written, where
    /// the record holds what CMake made of it - a path tidied of .. and doubled separators, a list's first
    /// item, and a name alone found as a program, dotted or not.
    /// </summary>
    [Theory]
    [InlineData(null, Cl, "MSVC", "19.51.36257.0")]
    [InlineData("gcc", "/usr/bin/gcc", "GNU", "13.2.0")]
    [InlineData("x86_64-w64-mingw32-gcc-13.2.0", "/usr/bin/x86_64-w64-mingw32-gcc-13.2.0", "GNU", "13.2.0")]
    [InlineData("/opt/gcc/bin/../bin/gcc", "/opt/gcc/bin/gcc", "GNU", "13.2.0")]
    [InlineData("/opt/gcc/bin//gcc", "/opt/gcc/bin/gcc", "GNU", "13.2.0")]
    [InlineData("/opt/gcc/bin/gcc;-m64", "/opt/gcc/bin/gcc", "GNU", "13.2.0")]
    public void Read_IdentifiesSuchALanguage_FromARecordOfTheCompilerTheAnswerNames(string? answered, string recorded, string id, string version)
    {
        using var temp = new TempDirectory();
        var build = WriteSubprojectReply(temp, c: answered);

        WriteRecord(build, "C", $"set(CMAKE_C_COMPILER \"{recorded}\")\nset(CMAKE_C_COMPILER_ID \"{id}\")\nset(CMAKE_C_COMPILER_VERSION \"{version}\")\n");

        Assert.Equal(new CompilerFact("C", id, version), Reader().Read(build).Compilers[0]);
    }

    /// <summary>
    /// What CMake makes of a Windows toolchain file's compiler, measured with CMake 4.3 and MinGW gcc: a
    /// path with the backslashes Windows takes for separators, and one with a doubled separator, each
    /// spelled with forward slashes in the record, its drive in the case written; and a name alone - dotted
    /// or not - found as the program with .exe after it. The record identifies the language each time.
    /// </summary>
    [Theory]
    [InlineData(@"C:\\Strawberry\\c\\bin\\gcc.exe", "C:/Strawberry/c/bin/gcc.exe")]
    [InlineData("c:/Strawberry/c/bin//gcc.exe", "c:/Strawberry/c/bin/gcc.exe")]
    [InlineData("gcc", "C:/Strawberry/c/bin/gcc.exe")]
    [InlineData("x86_64-w64-mingw32-gcc-13.2.0", "C:/Strawberry/c/bin/x86_64-w64-mingw32-gcc-13.2.0.exe")]
    public void Read_IdentifiesSuchALanguage_AsCMakeMakesACompilerOfAWindowsToolchainFile(string answeredInJson, string recorded)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows takes a backslash for a separator, and adds .exe to a program's name.");

        using var temp = new TempDirectory();

        // The answer's JSON, which escapes each backslash.
        var build = WriteSubprojectReply(temp, c: answeredInJson);

        WriteRecord(build, "C", $"set(CMAKE_C_COMPILER \"{recorded}\")\nset(CMAKE_C_COMPILER_ID \"GNU\")\nset(CMAKE_C_COMPILER_VERSION \"13.2.0\")\n");

        Assert.Equal(new CompilerFact("C", "GNU", "13.2.0"), Reader().Read(build).Compilers[0]);
    }

    /// <summary>
    /// Off Windows, a name alone is found as a program of exactly that name, as CMake's find_program finds
    /// it there: a record naming the name with .exe after it names another program.
    /// </summary>
    [Fact]
    public void Read_TakesARecordOfTheNameWithAnExtension_ForAnotherProgram_OffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows adds .com or .exe to a program's name.");

        using var temp = new TempDirectory();
        var build = WriteSubprojectReply(temp, c: "gcc");

        WriteRecord(build, "C", "set(CMAKE_C_COMPILER \"/usr/bin/gcc.exe\")\nset(CMAKE_C_COMPILER_ID \"GNU\")\n");

        Assert.EndsWith("names '/usr/bin/gcc.exe', another compiler", Reader().Read(build).Unidentified["C"], StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer whose path holds a NUL names no file, and ties no record to itself: the language is
    /// unidentified, and the rest of the answer is still read.
    /// </summary>
    [Fact]
    public void Read_TiesNoRecordToAPathHoldingANul()
    {
        using var temp = new TempDirectory();

        // The answer's JSON, which escapes the NUL.
        var build = WriteSubprojectReply(temp, c: "/opt/gcc\\u0000/bin/gcc");

        WriteRecord(build, "C", "set(CMAKE_C_COMPILER \"/opt/gcc/bin/gcc\")\nset(CMAKE_C_COMPILER_ID \"GNU\")\n");

        var reading = Reader().Read(build);

        Assert.Equal(["CXX"], reading.Compilers.Select(compiler => compiler.Language));
        Assert.EndsWith("names '/opt/gcc/bin/gcc', another compiler", reading.Unidentified["C"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A record nothing ties to the answer identifies nothing, as a configure after the answer's can leave
    /// one - one that identified the compiler again and then failed writes no answer: a record of another
    /// compiler than the answer names, by path or by a toolchain file's program name, one naming none, and
    /// one written after the answer, whether or not the answer names a compiler.
    /// </summary>
    [Theory]
    [InlineData("C:/Strawberry/c/bin/gcc.exe", Cl, false, $"names '{Cl}', another compiler")]
    [InlineData("gcc", "C:/Program Files/LLVM/bin/clang.exe", false, "names 'C:/Program Files/LLVM/bin/clang.exe', another compiler")]
    [InlineData(Cl, null, false, "names no compiler")]
    [InlineData(Cl, Cl, true, "was written after that answer")]
    [InlineData(null, Cl, true, "was written after that answer")]
    public void Read_IdentifiesNothingFromARecordNothingTiesToTheAnswer(string? answered, string? recorded, bool later, string expected)
    {
        using var temp = new TempDirectory();
        var build = WriteSubprojectReply(temp, c: answered);
        var names = recorded is null ? string.Empty : $"set(CMAKE_C_COMPILER \"{recorded}\")\n";

        WriteRecord(build, "C", $"{names}set(CMAKE_C_COMPILER_ID \"MSVC\")\nset(CMAKE_C_COMPILER_VERSION \"19.51.36257.0\")\n", later ? Answered.AddSeconds(1) : null);

        var reading = Reader().Read(build);

        Assert.Equal(["CXX"], reading.Compilers.Select(compiler => compiler.Language));
        Assert.StartsWith($"its answer names {(answered is null ? "no compiler" : $"'{answered}'")} for C and no id, and its record of identifying C's compiler, ", reading.Unidentified["C"], StringComparison.Ordinal);
        Assert.EndsWith(expected, reading.Unidentified["C"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A language the answer names no id for, where CMake's record does not identify it, is named
    /// unidentified with why - never read as any compiler: no record, one with no id, and an index
    /// naming no version to find it under - saying what the answer named, or that it named nothing.
    /// </summary>
    [Theory]
    [InlineData("missing", Cl, "is not there")]
    [InlineData("missing", null, "is not there")]
    [InlineData("no-id", Cl, "names no id either")]
    [InlineData("no-version", Cl, "its index names no CMake version, which its record of identifying C's compiler is kept under")]
    public void Read_NamesALanguageItCannotIdentify_WithWhy(string state, string? answered, string expected)
    {
        using var temp = new TempDirectory();
        var build = WriteSubprojectReply(temp, version: state == "no-version" ? null : "4.3.2", c: answered);

        if (state is "no-id" or "no-version")
        {
            WriteRecord(build, "C", $"set(CMAKE_C_COMPILER \"{Cl}\")\nset(CMAKE_C_COMPILER_ID \"\")\n");
        }

        var reading = Reader().Read(build);

        Assert.Equal(["CXX"], reading.Compilers.Select(compiler => compiler.Language));
        Assert.StartsWith($"its answer names {(answered is null ? "no compiler" : $"'{answered}'")} for C and no id, and ", reading.Unidentified["C"], StringComparison.Ordinal);
        Assert.EndsWith(expected, reading.Unidentified["C"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A declared language CMake's answer lists without identifying its compiler is unwitnessed, saying
    /// what the answer named for it and why that is no id - never that CMake named none - beside one the
    /// answer does not list at all; a language it identified as declared answers for itself.
    /// </summary>
    [Fact]
    public void HeldTo_SaysWhyALanguageCMakeListedIsUnidentified_ApartFromOneItNamedNothingFor()
    {
        var config = new HarnessConfig { Toolchains = { ["msvc"] = new ToolchainConfig { CompilerId = { ["C"] = "MSVC", ["CXX"] = "MSVC" } } } };
        var why = $"its answer names '{Cl}' for C and no id, and its record of identifying that compiler, 'CMakeCCompiler.cmake', is not there";
        var reading = new CompilerReading([new CompilerFact("CXX", "MSVC", "19.51.36257.0")], null)
        {
            Unidentified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["C"] = why },
        };

        var alone = CompilerFacts.HeldTo(config, "msvc", reading);

        Assert.Equal(LegVerdict.Unwitnessed, alone!.Verdict);
        Assert.Equal(
            $"toolchain 'msvc' declares the compiler for C, and CMake identified none for it: {why}; nothing established which compiler this build used",
            alone.Detail);

        config.Toolchains["msvc"].CompilerId["CUDA"] = "NVIDIA";

        var both = CompilerFacts.HeldTo(config, "msvc", reading);

        Assert.Equal(
            $"toolchain 'msvc' declares the compiler for C, CUDA, and CMake named none for CUDA; CMake identified none for C: {why}; "
            + "nothing established which compiler this build used",
            both!.Detail);
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

    /// <summary>The C and C++ compiler CMake 4.3 named in the measured reply, as it spelled it.</summary>
    private const string Cl = "C:/Program Files/Microsoft Visual Studio/18/Enterprise/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/cl.exe";

    /// <summary>The resource compiler it named beside them.</summary>
    private const string Rc = "C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/rc.exe";

    /// <summary>When the measured reply's index was written, as its name says.</summary>
    private static readonly DateTime Answered = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private static CMakeToolchainReader Reader() => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));

    /// <summary>
    /// Writes the index and toolchains answer CMake 4.3 wrote, measured, for a C++ project whose C a
    /// subproject enables: C with no id - the path <paramref name="c"/>, or none - C++ identified, and
    /// the resource compiler with its path alone. The index names the CMake <paramref name="version"/>
    /// that answered, or none, and was written at <see cref="Answered"/>.
    /// </summary>
    private static string WriteSubprojectReply(TempDirectory temp, string? version = "4.3.2", string? c = Cl)
    {
        var build = temp.Combine("build");
        var replies = Path.Combine(build, ".cmake", "api", "v1", "reply");
        var cmake = version is null ? string.Empty : $$""" "cmake": { "version": { "major": 4, "minor": 3, "patch": 2, "string": "{{version}}", "suffix": "" } }, """;
        var index = Path.Combine(replies, "index-2026-09-22T12-00-00-0000.json");

        temp.WriteFile(index, $$"""
            { {{cmake}} "reply": { "toolchains-v1": { "jsonFile": "toolchains-v1-a.json", "kind": "toolchains", "version": { "major": 1, "minor": 1 } } } }
            """);
        File.SetLastWriteTimeUtc(index, Answered);
        temp.WriteFile(Path.Combine(replies, "toolchains-v1-a.json"), $$"""
            {
              "kind": "toolchains",
              "version": { "major": 1, "minor": 1 },
              "toolchains": [
                { "language": "C", "compiler": { {{(c is null ? string.Empty : $"\"path\": \"{c}\", ")}}"implicit": {} } },
                { "language": "CXX", "compiler": { "id": "MSVC", "version": "19.51.36257.0", "path": "{{Cl}}", "implicit": {} } },
                { "language": "RC", "compiler": { "path": "{{Rc}}", "implicit": {} } }
              ]
            }
            """);

        return build;
    }

    /// <summary>
    /// Writes CMake's record of identifying <paramref name="language"/>'s compiler, where CMake 4.3.2 keeps
    /// it, as written at <paramref name="written"/> - by default before <see cref="Answered"/>, as the
    /// configure that answered wrote it.
    /// </summary>
    private static void WriteRecord(string build, string language, string text, DateTime? written = null)
    {
        var record = Path.Combine(build, "CMakeFiles", "4.3.2", $"CMake{language}Compiler.cmake");

        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(record, text);
        File.SetLastWriteTimeUtc(record, written ?? Answered.AddSeconds(-5));
    }

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
