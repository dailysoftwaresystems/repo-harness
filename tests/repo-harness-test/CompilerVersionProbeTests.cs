using RepoHarness.Core.Build;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// A compiler asked its version as CMake identifies one: the macros it predefines, put together by
/// CMake's own formula for the id CMake recorded.
/// </summary>
public sealed class CompilerVersionProbeTests
{
    /// <summary>
    /// What CMake 4.3 wrote in its records, beside what each compiler printed preprocessing the probed
    /// line, measured on each: MSVC through Visual Studio 18, MinGW gcc, Linux gcc and clang. Clang's
    /// row carries the __GNUC__ 4.2.1 clang defines too, which its version is never made of; the last
    /// is CMake's own fallback for a GNU compiler defining __GNUG__ alone.
    /// </summary>
    [Theory]
    [InlineData("MSVC", "1951 195136260 0 __GNUC__ __GNUG__ __GNUC_MINOR__ __GNUC_PATCHLEVEL__ __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__", "19.51.36260.0")]
    [InlineData("GNU", "_MSC_VER _MSC_FULL_VER _MSC_BUILD 13 13 2 0 __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__", "13.2.0")]
    [InlineData("GNU", "_MSC_VER _MSC_FULL_VER _MSC_BUILD 13 __GNUG__ 3 0 __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__", "13.3.0")]
    [InlineData("Clang", "_MSC_VER _MSC_FULL_VER _MSC_BUILD 4 4 2 1 18 1 3 __apple_build_version__", "18.1.3")]
    [InlineData("AppleClang", "_MSC_VER _MSC_FULL_VER _MSC_BUILD 4 4 2 1 15 0 0 15000040", "15.0.0.15000040")]
    [InlineData("GNU", "_MSC_VER _MSC_FULL_VER _MSC_BUILD __GNUC__ 13 2 0 __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__", "13.2.0")]
    public void WhatTheCompilerPrinted_IsPutTogetherAsCMakeRecordsIt(string id, string printed, string version)
    {
        var answer = CompilerVersionProbe.Read(id, $"probe.cpp\r\nrepo_harness_compiler_version {printed}\r\n");

        Assert.Equal(version, answer.Version);
        Assert.Null(answer.Replaced);
        Assert.Null(answer.Unanswered);
    }

    /// <summary>
    /// A part CMake would not have is left off, and every part after it: an MSVC without _MSC_BUILD is
    /// three parts, and one without _MSC_FULL_VER two, as CMake writes them.
    /// </summary>
    [Theory]
    [InlineData("1951 195136260 _MSC_BUILD", "19.51.36260")]
    [InlineData("1951 _MSC_FULL_VER 0", "19.51")]
    [InlineData("1310 13103077 _MSC_BUILD", "13.10.3077")]
    public void AnMsvcPartThatIsNotDefined_IsLeftOff_WithEveryPartAfterIt(string msvc, string version)
    {
        var answer = CompilerVersionProbe.Read("MSVC", $"repo_harness_compiler_version {msvc} __GNUC__ __GNUG__ __GNUC_MINOR__ __GNUC_PATCHLEVEL__ __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__\n");

        Assert.Equal(version, answer.Version);
    }

    /// <summary>
    /// A compiler that defines none of the macros its id's version is made of is not the compiler CMake
    /// identified, whatever else it defines: gcc where CMake recorded clang.
    /// </summary>
    [Fact]
    public void ACompilerDefiningNoneOfItsIdsMacros_IsAnotherCompiler()
    {
        var answer = CompilerVersionProbe.Read("Clang", "repo_harness_compiler_version _MSC_VER _MSC_FULL_VER _MSC_BUILD 13 13 2 0 __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__\n");

        Assert.Null(answer.Version);
        Assert.Contains("none of the macros a Clang compiler's version is made of", answer.Replaced, StringComparison.Ordinal);
    }

    /// <summary>
    /// Output that does not read as the probed line says so, and gives no version: no such line, a word
    /// short, or a word that is not a number where a macro was.
    /// </summary>
    [Theory]
    [InlineData("nothing like it\n", "printed no line starting 'repo_harness_compiler_version'")]
    [InlineData("repo_harness_compiler_version 1951 195136260\n", "it printed 2 words")]
    [InlineData("repo_harness_compiler_version (1951) 195136260 0 __GNUC__ __GNUG__ __GNUC_MINOR__ __GNUC_PATCHLEVEL__ __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__\n", "it printed '(1951)' for _MSC_VER")]
    public void OutputThatDoesNotReadAsTheLine_IsUnanswered(string output, string why)
    {
        var answer = CompilerVersionProbe.Read("MSVC", output);

        Assert.Null(answer.Version);
        Assert.Contains(why, answer.Unanswered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compiler is run as CMake runs it - its path, then the words its record names after it - with
    /// the options for preprocessing its kind takes, in the build's environment and on its PATH, over a
    /// line written outside the build directory in the language's own file.
    /// </summary>
    [Theory]
    [InlineData(true, "CXX", "/nologo /EP", ".cpp")]
    [InlineData(false, "C", "-E -P", ".c")]
    public async Task TheCompiler_IsRunAsCMakeRunsIt_PreprocessingTheLine(bool msvc, string language, string options, string extension)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var program = temp.WriteFile(Path.Combine("toolchain", "compiler.exe"), "a compiler");
        var scratch = temp.Combine("run", "leg");
        var runner = new AnsweringRunner("repo_harness_compiler_version 1951 195136260 0 __GNUC__ __GNUG__ __GNUC_MINOR__ __GNUC_PATCHLEVEL__ __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__");
        var environment = new Dictionary<string, string?> { ["INCLUDE"] = "from the developer environment" };

        await new CompilerVersionProbe(runner, factory.FileSystem).AskAsync(
            new IdentifiedCompiler(language, "MSVC", "19.51.36260.0", program, ["--launched"], msvc),
            scratch,
            environment,
            [temp.Combine("tools")],
            TestContext.Current.CancellationToken);

        var request = Assert.Single(runner.Requests);
        var source = Path.Combine(scratch, $"compiler-version-{language}{extension}");

        Assert.Equal(program, request.FileName);
        Assert.Equal(["--launched", .. options.Split(' '), source], request.Arguments);
        Assert.Same(environment, request.Environment);
        Assert.Equal([temp.Combine("tools")], request.AppendToPath);
        Assert.StartsWith("repo_harness_compiler_version _MSC_VER ", File.ReadAllText(source), StringComparison.Ordinal);
    }

    /// <summary>A compiler whose recorded path holds nothing now is gone, and is not run.</summary>
    [Fact]
    public async Task ACompilerThatIsNotThere_IsGone_AndNotRun()
    {
        using var temp = new TempDirectory();
        var runner = new AnsweringRunner("unused");

        var answer = await new CompilerVersionProbe(runner, new HarnessFactory().FileSystem).AskAsync(
            new IdentifiedCompiler("CXX", "GNU", "13.2.0", temp.Combine("gone", "g++"), [], MsvcOptions: false),
            temp.Combine("scratch"),
            new Dictionary<string, string?>(),
            [],
            TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.Equal("it is not there now", answer.Replaced);
        Assert.Empty(runner.Requests);
    }

    /// <summary>
    /// A compiler that cannot be asked says why, and gives no version: it would not start, it exited
    /// otherwise than 0, or it did not finish in time. What it said on exiting is quoted past the name of
    /// the file it read, which cl says first - on its standard error, under /EP - and from its standard
    /// output where its standard error says nothing else.
    /// </summary>
    [Theory]
    [InlineData("start", "could not be started: Text file busy")]
    [InlineData("exit", "preprocessing one line, it exited 2: fatal error: cannot open source file")]
    [InlineData("exit-naming", "preprocessing one line, it exited 2: compiler-version-C.c(1): fatal error C1034: no include path set")]
    [InlineData("exit-naming-only", "preprocessing one line, it exited 2: cl : Command line error D8021 : invalid numeric argument")]
    [InlineData("time", "it had not preprocessed one line after 2 minutes")]
    public async Task ACompilerThatCannotBeAsked_SaysWhy(string how, string why)
    {
        using var temp = new TempDirectory();
        var program = temp.WriteFile("cc", "a compiler");
        var runner = new AnsweringRunner(string.Empty) { Fails = how };

        var answer = await new CompilerVersionProbe(runner, new HarnessFactory().FileSystem).AskAsync(
            new IdentifiedCompiler("C", "GNU", "13.2.0", program, [], MsvcOptions: false),
            temp.Combine("scratch"),
            new Dictionary<string, string?>(),
            [],
            TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.Null(answer.Version);
        Assert.Contains(why, answer.Unanswered, StringComparison.Ordinal);
    }

    /// <summary>
    /// An id whose version this build does not know how to put together is not asked at all: nothing it
    /// could answer would be comparable with what CMake recorded. Nor is a compiler CMake could not
    /// identify, which it records with an empty id and version.
    /// </summary>
    [Theory]
    [InlineData("IntelLLVM", "2025.0.0")]
    [InlineData("", "")]
    public async Task AnIdWhoseFormulaIsNotKnown_IsNotAsked(string id, string version)
    {
        using var temp = new TempDirectory();
        var runner = new AnsweringRunner("unused");

        var answer = await new CompilerVersionProbe(runner, new HarnessFactory().FileSystem).AskAsync(
            new IdentifiedCompiler("CXX", id, version, temp.WriteFile("icx", "a compiler"), [], MsvcOptions: false),
            temp.Combine("scratch"),
            new Dictionary<string, string?>(),
            [],
            TestContext.Current.CancellationToken);

        Assert.Null(answer);
        Assert.Empty(runner.Requests);
    }

    /// <summary>
    /// A record that identified a known kind but left out what the question needs - a version to hold the
    /// answer to, or a program to ask - is said and not asked: a version compared with nothing would
    /// differ on every build, and start the directory from clean every time.
    /// </summary>
    [Theory]
    [InlineData("", "cc", "CMake recorded no version of it")]
    [InlineData("13.2.0", "", "CMake's record of it names no program")]
    public async Task ARecordLeavingOutWhatTheQuestionNeeds_IsNotAsked_AndSaysWhat(string version, string program, string why)
    {
        using var temp = new TempDirectory();
        var runner = new AnsweringRunner("unused");

        var answer = await new CompilerVersionProbe(runner, new HarnessFactory().FileSystem).AskAsync(
            new IdentifiedCompiler("C", "GNU", version, program.Length > 0 ? temp.WriteFile(program, "a compiler") : program, [], MsvcOptions: false),
            temp.Combine("scratch"),
            new Dictionary<string, string?>(),
            [],
            TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.Equal(why, answer.Unanswered);
        Assert.Empty(runner.Requests);
    }

    /// <summary>A runner that answers every request with <paramref name="output"/>, or fails as told.</summary>
    private sealed class AnsweringRunner(string output) : IProcessRunner
    {
        private readonly List<ProcessRequest> _requests = [];

        /// <summary>Every request it was given.</summary>
        public IReadOnlyList<ProcessRequest> Requests => _requests;

        /// <summary>
        /// How it fails: "start", "exit", "exit-naming" as cl does, "exit-naming-only" with the reason on its
        /// standard output, or "time"; null to answer.
        /// </summary>
        public string? Fails { get; init; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);

            return Fails switch
            {
                "start" => throw new ProgramStartException(request.FileName, $"'{request.FileName}' could not be started: Text file busy"),
                "exit" => Task.FromResult(new ProcessResult(2, string.Empty, "\nfatal error: cannot open source file\nmore\n", TimeSpan.Zero, TimedOut: false)),
                "exit-naming" => Task.FromResult(new ProcessResult(
                    2,
                    string.Empty,
                    "compiler-version-C.c\r\ncompiler-version-C.c(1): fatal error C1034: no include path set\r\n",
                    TimeSpan.Zero,
                    TimedOut: false)),
                "exit-naming-only" => Task.FromResult(new ProcessResult(
                    2,
                    "cl : Command line error D8021 : invalid numeric argument '/Wx'\r\n",
                    "compiler-version-C.c\r\n",
                    TimeSpan.Zero,
                    TimedOut: false)),
                "time" => Task.FromResult(new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(2), TimedOut: true)),
                _ => Task.FromResult(new ProcessResult(0, output + "\n", string.Empty, TimeSpan.Zero, TimedOut: false)),
            };
        }

        public string? FindExecutable(string command) => command;
    }
}
