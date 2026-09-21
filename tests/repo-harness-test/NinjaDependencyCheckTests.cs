using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// Which objects recorded no header dependencies, and which of them legitimately so. Only an object
/// built under <c>deps = msvc</c> can record zero legitimately - its compile including nothing but
/// headers ninja takes for the system's, or ones its build is rebuilt for all the same, as a
/// precompiled header's units are - and the manifest is read the way ninja reads it - CMake keeps its
/// rules in a file <c>build.ninja</c> includes, names each source by an escaped absolute path, and
/// gives each compile its options through variables - or the excuse never applies to a build CMake
/// generated at all.
/// </summary>
public sealed class NinjaDependencyCheckTests
{
    private const string CRule = "C_COMPILER__probe_unscanned_Debug";
    private const string CxxRule = "CXX_COMPILER__probe_unscanned_Debug";
    private const string DefaultFlags = "/DWIN32 /Zi /Od";

    /// <summary>The build directory's name: it holds a space, so a flag naming a file in it is quoted, as CMake quotes it.</summary>
    private const string BuildName = "the build";

    /// <summary>
    /// A build laid out the way CMake lays out an MSVC one: the rule, with <c>deps = msvc</c>, in
    /// <c>CMakeFiles/rules.ninja</c>, and each source named absolutely, escaped. A source that includes
    /// nothing is excused; one whose quoted include resolves beside it to a header ninja keeps, and
    /// recorded none, is not.
    /// </summary>
    [Fact]
    public async Task AnMsvcObjectWhoseSourceIncludesNothing_IsExcused_ThroughTheRulesCMakeIncludes()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(temp, deps: "msvc", ("main.c", "int main(void) { return 0; }\n"), ("other.c", "#include \"probe.h\"\nint other(void) { return 1; }\n"));
        temp.WriteFile(Path.Combine("my src", "probe.h"), "int other(void);\n");

        var report = await Check(build, "main.c", "other.c");

        Assert.Equal(2, report.ObjectsRead);
        Assert.Equal([Object("other.c")], report.WithoutHeaders);
        Assert.Equal(temp.Combine("my src", "main.c"), Assert.Single(report.Excused, pair => pair.Key == Object("main.c")).Value);
    }

    /// <summary>
    /// Ninja drops every header it takes for the system's under <c>deps = msvc</c>, so a source that
    /// includes only those records none, and legitimately: a hello world including only the standard
    /// library failed every msvc leg. So does one whose quoted include resolves nowhere beside it, or
    /// to a header ninja reaches from the build directory through a directory it takes for the system's.
    /// </summary>
    [Fact]
    public async Task AnMsvcObjectIncludingOnlyWhatNinjaDrops_IsExcused()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(
            temp,
            deps: "msvc",
            ("hello.cpp", "#include <iostream>\n#include <windows.h>\nint main() { std::cout << 1; }\n"),
            ("config.cpp", "#include \"generated/config.h\"\nint config() { return 0; }\n"),
            (Path.Combine("Program Files", "sdk.cpp"), "#include \"sdk.h\"\nint sdk() { return 0; }\n"));
        temp.WriteFile(Path.Combine("my src", "Program Files", "sdk.h"), "int sdk();\n");

        var report = await Check(build, "hello.cpp", "config.cpp", Path.Combine("Program Files", "sdk.cpp"));

        Assert.Empty(report.WithoutHeaders);
        Assert.Equal(3, report.Excused.Count);
    }

    /// <summary>
    /// Ninja takes a header for the system's by the path it holds it by, relative to the build
    /// directory: a tree kept under <c>Program Files</c> reaches its own headers without naming that,
    /// and ninja keeps them - so a unit there that includes one, and recorded none, is not excused.
    /// </summary>
    [Fact]
    public async Task ATreeUnderProgramFiles_KeepsItsOwnHeaders_AsNinjaDoes()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuildUnder(temp, "Program Files", "msvc", ("main.c", "#include \"util.h\"\nint main(void) { return 0; }\n"));
        temp.WriteFile(Path.Combine("Program Files", "my src", "util.h"), "int util(void);\n");

        var report = await Check(build, "main.c");

        Assert.Equal([Object("main.c")], report.WithoutHeaders);
    }

    /// <summary>
    /// Another object's record says nothing of this one's: it may have been written by an older build,
    /// under a Visual Studio in another language, or replayed by a compiler cache. So a zero whose
    /// source includes a header beside it is flagged even beside an object that recorded that very
    /// header, and one that includes nothing ninja keeps is excused by its own source alone. A
    /// dependency listed beneath no object's line is no object's.
    /// </summary>
    [Fact]
    public async Task AnMsvcZero_IsNeverExcused_ByAnotherObjectsRecord()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(
            temp,
            deps: "msvc",
            ("main.c", "#include \"util.h\"\nint main(void) { return util(); }\n"),
            ("other.c", "#include \"util.h\"\nint other(void) { return util(); }\n"),
            ("hello.c", "#include <stdio.h>\nint hello(void) { return printf(\"\"); }\n"));
        temp.WriteFile(Path.Combine("my src", "util.h"), "int util(void);\n");

        var answer = $"{Object("other.c")}: #deps 0, deps mtime 2 (VALID)\n\nnot a record\n    ../my src/util.h\n\n"
            + $"{Object("main.c")}: #deps 1, deps mtime 1 (VALID)\n    ../my src/util.h\n\n{Object("hello.c")}: #deps 0, deps mtime 2 (VALID)\n\n";
        var report = await CheckAnswered(build, answer);

        Assert.Equal(3, report.ObjectsRead);
        Assert.Equal([Object("other.c")], report.WithoutHeaders);
        Assert.Equal([Object("hello.c")], report.Excused.Keys);
    }

    /// <summary>
    /// A precompiled header's build as CMake 4 writes it for MSVC, and as ninja recorded it: the object
    /// compiling it and every unit built from it force-include it, and that object records every
    /// header it holds; each unit names the <c>.pch</c>, a phony whose only input is that object. A
    /// unit that includes nothing, or a header the precompiled header holds and guards - which cl never
    /// opens again, here named in another case, as Windows finds it - is rebuilt through that object,
    /// and excused; one that includes a header it does not hold is not. For C++ the header holds its
    /// includes under <c>#ifdef __cplusplus</c>, which a C++ compile surely compiles. Once that object
    /// records nothing - a compiler cache replaying it without cl's includes - nothing rebuilds
    /// anything for a held header, and neither it nor any unit built from it is excused.
    /// </summary>
    [Theory]
    [InlineData("c")]
    [InlineData("cpp")]
    public async Task APrecompiledHeadersUnits_AreExcused_OnlyWhileItsObjectRecordsWhatItHolds(string extension)
    {
        using var temp = new TempDirectory();
        var (build, compiling, header) = PrecompiledBuild(
            temp,
            extension,
            ($"stub.{extension}", "typedef char int_is_wide_enough[sizeof(int) >= 2 ? 1 : -1];\n"),
            ($"once.{extension}", "#include \"Once.h\"\nint once(void) { return ONCE; }\n"),
            ($"extra.{extension}", "#include \"extra.h\"\nint extra(void) { return EXTRA; }\n"));
        var units = string.Concat(new[] { "stub", "once", "extra" }.Select(unit => $"{PchObject($"{unit}.{extension}")}: #deps 0, deps mtime 2 (VALID)\n\n"));

        var recorded = await CheckAnswered(
            build,
            $"{compiling}: #deps 3, deps mtime 1 (VALID)\n    ../my src/probe.h\n    ../my src/once.h\n    {header}\n\n" + units);

        Assert.Equal([PchObject($"extra.{extension}")], recorded.WithoutHeaders);
        Assert.Equal([PchObject($"stub.{extension}"), PchObject($"once.{extension}")], recorded.Excused.Keys);

        var replayed = await CheckAnswered(build, $"{compiling}: #deps 0, deps mtime 1 (VALID)\n\n" + units);

        Assert.Equal([compiling, PchObject($"stub.{extension}"), PchObject($"once.{extension}"), PchObject($"extra.{extension}")], replayed.WithoutHeaders);
        Assert.Empty(replayed.Excused);
    }

    /// <summary>
    /// What rebuilds an object is followed however far back its inputs are built: here a unit's input
    /// is a phony naming a phony naming the object that recorded the header the unit includes, and
    /// the unit is rebuilt for that header all the same.
    /// </summary>
    [Fact]
    public async Task WhatRebuildsAnObject_IsFollowedHoweverFarBackItsInputsAreBuilt()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("my src", "held.h"), "#pragma once\nint held(void);\n");
        var compiling = temp.WriteFile(Path.Combine("my src", "compiling.c"), "#include \"held.h\"\nint compiling(void) { return 0; }\n");
        var unit = temp.WriteFile(Path.Combine("my src", "unit.c"), "#include \"held.h\"\nint unit(void) { return held(); }\n");

        var build = Manifest(
            temp,
            "msvc",
            [
                .. Compiled(ObjectPath("compiling.c"), compiling),
                $"build real.pch: phony {Escape(ObjectPath("compiling.c"))}",
                "build alias.pch: phony real.pch",
                .. Compiled(ObjectPath("unit.c"), unit, DefaultFlags, "alias.pch"),
            ]);
        var report = await CheckAnswered(
            build,
            $"{Object("compiling.c")}: #deps 1, deps mtime 1 (VALID)\n    ../my src/held.h\n\n{Object("unit.c")}: #deps 0, deps mtime 2 (VALID)\n\n");

        Assert.Empty(report.WithoutHeaders);
        Assert.Equal([Object("unit.c")], report.Excused.Keys);
    }

    /// <summary>
    /// A header is force-included however cl is told to: <c>/FI</c> or <c>-FI</c>, its path joined to
    /// it or the next argument, quoted or not - and a unit that recorded nothing is then judged by it.
    /// <c>/Fi</c> names a file cl writes, and includes nothing.
    /// </summary>
    [Fact]
    public async Task AForcedInclude_IsReadInEveryFormClTakes()
    {
        using var temp = new TempDirectory();
        var util = Slashed(temp.WriteFile(Path.Combine("my src", "util.h"), "int util(void);\n"));
        var lines = new List<string>();

        foreach (var (name, flags) in new[] { ("joined.c", $"\"/FI{util}\""), ("apart.c", $"/FI \"{util}\""), ("dashed.c", $"\"-FI{util}\""), ("written.c", $"\"/Fi{util}\"") })
        {
            lines.AddRange(Compiled(ObjectPath(name), temp.WriteFile(Path.Combine("my src", name), "int unit(void) { return 0; }\n"), flags));
        }

        var report = await Check(Manifest(temp, "msvc", lines), "joined.c", "apart.c", "dashed.c", "written.c");

        Assert.Equal([Object("joined.c"), Object("apart.c"), Object("dashed.c")], report.WithoutHeaders);
        Assert.Equal([Object("written.c")], report.Excused.Keys);
    }

    /// <summary>
    /// Only what the command force-includes is read as included: what a build line depends on besides -
    /// CMake's OBJECT_DEPENDS, here a directory and a file that includes a header - is rebuilt for, and
    /// never read. A unit that includes only a system header beside them is excused, as its build is
    /// healthy; so is one including a header its build line names among its inputs, which ninja
    /// rebuilds it for whatever its build recorded.
    /// </summary>
    [Fact]
    public async Task AnInputABuildLineOnlyDependsOn_IsNeverReadAsIncluded()
    {
        using var temp = new TempDirectory();
        var assets = Directory.CreateDirectory(temp.Combine("my src", "assets")).FullName;
        var listing = temp.WriteFile(Path.Combine("my src", "listing.txt"), "#include \"util.h\"\n");
        var util = temp.WriteFile(Path.Combine("my src", "util.h"), "int util(void);\n");
        var depends = temp.WriteFile(Path.Combine("my src", "depends.cpp"), "#include <vector>\nint depends() { return 0; }\n");
        var named = temp.WriteFile(Path.Combine("my src", "named.c"), "#include \"util.h\"\nint named(void) { return util(); }\n");

        var report = await Check(
            Manifest(temp, "msvc", [.. Compiled(ObjectPath("depends.cpp"), depends, DefaultFlags, assets, listing), .. Compiled(ObjectPath("named.c"), named, DefaultFlags, util)]),
            "depends.cpp",
            "named.c");

        Assert.Empty(report.WithoutHeaders);
        Assert.Equal([Object("depends.cpp"), Object("named.c")], report.Excused.Keys);
    }

    /// <summary>
    /// A conditional block is surely compiled only where the unit's language answers its condition: a
    /// number, or whether <c>__cplusplus</c> is defined - by C++, under <c>/TP</c> or for a C++ source a
    /// rule compiles without it, and not by C - and a later branch only where no earlier one may have
    /// been. An include in a block that may not be compiled proves nothing; one in a block that surely
    /// is proves a header should have been recorded, and its object is not excused.
    /// </summary>
    [Fact]
    public async Task AConditionalBlock_IsSurelyCompiled_OnlyWhereTheUnitsLanguageAnswersIt()
    {
        using var temp = new TempDirectory();
        const string cplusplus = "#ifdef __cplusplus\n#include \"util.h\"\n#endif\n";
        var lines = new List<string>();

        foreach (var (name, text, flags) in new[]
        {
            ("c_only.c", cplusplus, DefaultFlags),
            ("cxx_only.cpp", cplusplus, DefaultFlags),
            ("told.c", cplusplus, DefaultFlags + " /TP"),
            ("not_cxx.c", "#if !defined(__cplusplus)\n#include \"util.h\"\n#endif\n", DefaultFlags),
            ("zero.c", "#if 0\n#include \"util.h\"\n#endif\n", DefaultFlags),
            ("otherwise.c", "#if 0\n#else\n#include \"util.h\"\n#endif\n", DefaultFlags),
            ("maybe.c", "#ifdef FEATURE\n#else\n#include \"util.h\"\n#endif\n", DefaultFlags),
        })
        {
            lines.AddRange(Compiled(ObjectPath(name), temp.WriteFile(Path.Combine("my src", name), text + "int unit(void) { return 0; }\n"), flags));
        }

        temp.WriteFile(Path.Combine("my src", "util.h"), "int util(void);\n");

        // Built by the C rule, which adds no /TP: C++ by its extension alone.
        var bare = temp.WriteFile(Path.Combine("my src", "bare.cpp"), cplusplus + "int unit() { return 0; }\n");
        lines.AddRange([$"build {Escape(ObjectPath("bare.cpp"))}: {CRule} {Escape(bare)}", $"  FLAGS = {DefaultFlags}"]);

        var report = await Check(Manifest(temp, "msvc", lines), "c_only.c", "cxx_only.cpp", "told.c", "not_cxx.c", "zero.c", "otherwise.c", "maybe.c", "bare.cpp");

        Assert.Equal([Object("cxx_only.cpp"), Object("told.c"), Object("not_cxx.c"), Object("otherwise.c"), Object("bare.cpp")], report.WithoutHeaders);
        Assert.Equal([Object("c_only.c"), Object("zero.c"), Object("maybe.c")], report.Excused.Keys);
    }

    /// <summary>
    /// A source is read as the preprocessor reads its directives: every line a backslash ends joined to
    /// the next first, so a directive continued is one directive and a comment's <c>*</c> and <c>/</c>
    /// split across lines still close it; then an include inside a comment is never read, and proves
    /// nothing - nor does a comment opener inside a literal, or behind <c>//</c>, hide the include after
    /// it, and one after the comment closed is read. A number's digit separators open no literal, and
    /// a raw string's lines are none of the source's.
    /// </summary>
    [Fact]
    public async Task ACommentedOutInclude_ProvesNothing_AndASplicedOneIncludes()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(
            temp,
            deps: "msvc",
            ("commented.c", "/*\n#include \"util.h\"\n*/\n// #include \"util.h\"\nint commented(void) { return 0; }\n"),
            ("literal.c", "const char *opener = \"/*\";\n#include \"util.h\"\nint literal(void) { return 0; }\n"),
            ("behind.c", "// see /* here\n#include \"util.h\"\nint behind(void) { return 0; }\n"),
            ("closed.c", "/* a comment */\n#include \"util.h\"\nint closed(void) { return 0; }\n"),
            ("spliced.c", "#include \\\r\n  \"util.h\"\nint spliced(void) { return 0; }\n"),
            ("star.c", "/* a comment *\\\n/\n#include \"util.h\"\nint star(void) { return 0; }\n"),
            ("number.cpp", "int big = 0x1'0000; /*\n#include \"util.h\"\n*/\nint number() { return big; }\n"),
            ("raw.cpp", "const char *raw = R\"x(\n#include \"util.h\"\n)x\";\nint raw_string() { return 0; }\n"));
        temp.WriteFile(Path.Combine("my src", "util.h"), "int util(void);\n");

        var report = await Check(build, "commented.c", "literal.c", "behind.c", "closed.c", "spliced.c", "star.c", "number.cpp", "raw.cpp");

        Assert.Equal([Object("literal.c"), Object("behind.c"), Object("closed.c"), Object("spliced.c"), Object("star.c")], report.WithoutHeaders);
        Assert.Equal([Object("commented.c"), Object("number.cpp"), Object("raw.cpp")], report.Excused.Keys);
    }

    /// <summary>
    /// A local include compiled out on this platform proves nothing: cl never read it, so the zero
    /// beside it is excused. An include compiled whatever is defined - here after a block that also
    /// holds one - still proves a header should have been recorded, and its object is not excused.
    /// </summary>
    [Fact]
    public async Task AnMsvcZero_WhoseOnlyLocalIncludeIsCompiledOut_IsExcused()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(
            temp,
            deps: "msvc",
            ("posix.c", "#ifndef _WIN32\n#  include \"posix.h\"\n#endif\nint posix(void) { return 0; }\n"),
            ("both.c", "#if defined(FEATURE)\n#include \"feature.h\"\n#else\n#include \"posix.h\"\n#endif\n#include \"util.h\"\nint both(void) { return 0; }\n"));
        temp.WriteFile(Path.Combine("my src", "posix.h"), "int posix(void);\n");
        temp.WriteFile(Path.Combine("my src", "feature.h"), "int feature(void);\n");
        temp.WriteFile(Path.Combine("my src", "util.h"), "int util(void);\n");

        var report = await Check(build, "posix.c", "both.c");

        Assert.Equal([Object("both.c")], report.WithoutHeaders);
        Assert.Equal([Object("posix.c")], report.Excused.Keys);
    }

    /// <summary>
    /// Under <c>deps = gcc</c> the source itself is always recorded, so an object recording nothing is
    /// never excused, whatever its source includes.
    /// </summary>
    [Fact]
    public async Task AGccObject_IsNeverExcused_ThoughItsSourceIncludesNothing()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(temp, deps: "gcc", ("main.c", "int main(void) { return 0; }\n"));

        var report = await Check(build, "main.c");

        Assert.Equal([Object("main.c")], report.WithoutHeaders);
        Assert.Empty(report.Excused);
    }

    /// <summary>A build line's own <c>deps</c> outranks its rule's, as ninja reads it.</summary>
    [Fact]
    public async Task ABuildLinesOwnDeps_OutranksItsRules()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(temp, deps: "msvc", ("main.c", "int main(void) { return 0; }\n"));

        File.AppendAllText(Path.Combine(build, NinjaDependencyCheck.ManifestFileName), "  deps = gcc\n");

        var report = await Check(build, "main.c");

        Assert.Equal([Object("main.c")], report.WithoutHeaders);
    }

    /// <summary>
    /// What cannot be tied to a source that includes nothing is never excused: an object no build line
    /// produces, and a source that is not there.
    /// </summary>
    [Fact]
    public async Task AnObjectWithNoBuildLine_OrNoSource_IsNeverExcused()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(temp, deps: "msvc", ("main.c", "int main(void) { return 0; }\n"));

        File.Delete(temp.Combine("my src", "main.c"));

        var report = await Check(build, "main.c", "stray.c");

        Assert.Equal([Object("main.c"), Object("stray.c")], report.WithoutHeaders);
        Assert.Empty(report.Excused);
    }

    /// <summary>An object whose path holds a space is still counted, and still judged.</summary>
    [Fact]
    public async Task AnObjectWhosePathHoldsASpace_IsCounted()
    {
        using var temp = new TempDirectory();
        var build = Directory.CreateDirectory(temp.Combine("build")).FullName;

        File.WriteAllText(Path.Combine(build, NinjaDependencyCheck.ManifestFileName), "rule cc\n  deps = gcc\nbuild my$ file.o: cc my$ file.c\n");

        var report = await CheckAnswered(build, "my file.o: #deps 0, deps mtime 1 (VALID)\n\n");

        Assert.Equal(1, report.ObjectsRead);
        Assert.Equal(["my file.o"], report.WithoutHeaders);
    }

    /// <summary>
    /// A build line is read with ninja's escapes undone and its variables evaluated: what it produces,
    /// implicit outputs too; what rebuilds it, implicit inputs too, and never what it is only ordered
    /// after or validated by; and its first explicit input, which one with none has not. A <c>|</c>
    /// ends a path unspaced, as ninja reads it. A line ending in an unescaped <c>$</c> goes on on the
    /// next, and one ending in an escaped one does not.
    /// </summary>
    [Fact]
    public void ABuildLine_IsReadWithNinjasEscapesUndone()
    {
        var line = NinjaManifest.BuildLine(@"out$ dir/a$$b.obj | a.pdb: cc C$:\my$ src\a.c | a.h || order $$HOME");

        Assert.Equal(["out dir/a$b.obj", "a.pdb"], line.Outputs);
        Assert.Equal("cc", line.Rule);
        Assert.Equal([@"C:\my src\a.c", "a.h"], line.Inputs);
        Assert.Equal(@"C:\my src\a.c", line.Source);

        var unspaced = NinjaManifest.BuildLine("a.obj|a.pdb: cc a.c|a.h||order|@check");

        Assert.Equal(["a.obj", "a.pdb"], unspaced.Outputs);
        Assert.Equal(["a.c", "a.h"], unspaced.Inputs);
        Assert.Null(NinjaManifest.BuildLine("all.stamp: phony || order").Source);
        Assert.Null(NinjaManifest.BuildLine("x.stamp: touch | only.h").Source);

        var named = NinjaManifest.BuildLine("$dir/a.obj: cc ${dir}/a.c", name => name == "dir" ? "out" : string.Empty);

        Assert.Equal(["out/a.obj"], named.Outputs);
        Assert.Equal(["out/a.c"], named.Inputs);

        Assert.Equal(
            ["build a.obj: cc a.c b.c", "  deps = msvc", "x = cost $$"],
            NinjaManifest.LogicalLines("build a.obj: cc a.c $\n    b.c\n  deps = msvc\r\nx = cost $$\n").Where(line => line.Length > 0));
    }

    /// <summary>
    /// A build line's command is evaluated as ninja evaluates it: its rule's bindings for that line, with
    /// <c>$in</c> and <c>$out</c> its own explicit paths, quoted where a space would split them; its own
    /// bindings, evaluated as they are read, over its rule's, over its file's, where a rule's are read
    /// with every file's final values; a subninja's own over the file it is read from; and what its
    /// response file holds after the command.
    /// </summary>
    [Fact]
    public void ACommand_IsEvaluatedAsNinjaEvaluatesIt()
    {
        using var temp = new TempDirectory();
        var build = Directory.CreateDirectory(temp.Combine("build")).FullName;

        File.WriteAllText(
            Path.Combine(build, NinjaDependencyCheck.ManifestFileName),
            "tool = cl\n"
            + "rule cc\n  command = ${tool} /nologo $extra $FLAGS -c $in /Fo$out\n  extra = /TP\n  deps = $mode\n  rspfile = $out.rsp\n  rspfile_content = /FI$forced\n"
            + "build a.obj | a.pdb: cc my$ file.cpp | a.h\n  FLAGS = /Od $late\n"
            + "late = /Zi\nmode = msvc\nforced = forced.h\n"
            + "subninja sub.ninja\n");
        File.WriteAllText(Path.Combine(build, "sub.ninja"), "tool = clang-cl\nbuild b.obj: cc b.cpp\n");

        var manifest = NinjaManifest.Read(FileSystem(), build);

        Assert.Equal("cl /nologo /TP /Od  -c \"my file.cpp\" /Foa.obj /FIforced.h", manifest.EdgeFor("a.obj")?.Command);
        Assert.Equal("msvc", manifest.EdgeFor("a.pdb")?.Deps);
        Assert.Equal("clang-cl /nologo /TP  -c b.cpp /Fob.obj /FIforced.h", manifest.EdgeFor("b.obj")?.Command);
    }

    /// <summary>
    /// A file a subninja names sees the rules declared before it, and what it declares stays its own;
    /// a file included twice, or including itself, is read once. An output is found whichever
    /// separator it is asked for with.
    /// </summary>
    [Fact]
    public void ASubninjasRules_StayItsOwn_AndNoFileIsReadTwice()
    {
        using var temp = new TempDirectory();
        var build = Directory.CreateDirectory(temp.Combine("build")).FullName;

        File.WriteAllText(
            Path.Combine(build, NinjaDependencyCheck.ManifestFileName),
            "include my$ rules.ninja\ninclude my$ rules.ninja\nsubninja sub.ninja\nbuild out/top.obj: cc top.c\n");
        File.WriteAllText(Path.Combine(build, "my rules.ninja"), "rule cc\n  deps = msvc\ninclude my$ rules.ninja\n");
        File.WriteAllText(Path.Combine(build, "sub.ninja"), "build inherited.obj: cc a.c\nrule cc\n  deps = gcc\nbuild own.obj: cc b.c\n");

        var manifest = NinjaManifest.Read(FileSystem(), build);

        Assert.Equal("msvc", manifest.EdgeFor("inherited.obj")?.Deps);
        Assert.Equal("gcc", manifest.EdgeFor("own.obj")?.Deps);
        Assert.Equal("msvc", manifest.EdgeFor("out/top.obj")?.Deps);
        Assert.Equal("msvc", manifest.EdgeFor(@"out\top.obj")?.Deps);
        Assert.Null(manifest.EdgeFor("absent.obj"));
    }

    /// <summary>
    /// A path is read as ninja canonicalizes it, so the <c>.pch</c> CMake declares with <c>.\\</c> is the
    /// one its units name without, and a record's path is the manifest's; a network path keeps the pair
    /// of separators it opens with, as ninja on Windows keeps it.
    /// </summary>
    [Theory]
    [InlineData(@"CMakeFiles\t.dir\.\\cmake_pch.c.pch", "CMakeFiles/t.dir/cmake_pch.c.pch")]
    [InlineData("../src/./a.h", "../src/a.h")]
    [InlineData("sub/../../a.h", "../a.h")]
    [InlineData("/abs//a.h", "/abs/a.h")]
    [InlineData(@"\\server\share\.\a.obj", "//server/share/a.obj")]
    public void APath_IsReadAsNinjaCanonicalizesIt(string spelled, string canonical)
        => Assert.Equal(canonical, NinjaManifest.Normalize(spelled));

    /// <summary>The object's name in the spelling <c>ninja -t deps</c> prints it in.</summary>
    private static string Object(string source) => $"CMakeFiles/probe.dir/{source.Replace('\\', '/')}.obj";

    /// <summary>Where CMake puts the object of <paramref name="source"/>, relative to the build directory.</summary>
    private static string ObjectPath(string source) => Path.Combine("CMakeFiles", "probe.dir", source + ".obj");

    /// <summary>The name, as <c>ninja -t deps</c> prints it, of an object <see cref="PrecompiledBuild"/> builds.</summary>
    private static string PchObject(string source) => $"CMakeFiles/pch.dir/{source}.obj";

    /// <summary>
    /// A build directory as CMake writes one: <c>build.ninja</c> including <c>CMakeFiles/rules.ninja</c>,
    /// which declares the compile rules, and one build line per source, each named absolutely under a
    /// directory whose name holds a space.
    /// </summary>
    private static string CMakeBuild(TempDirectory temp, string deps, params (string Name, string Text)[] sources)
        => CMakeBuildUnder(temp, string.Empty, deps, sources);

    /// <summary><see cref="CMakeBuild"/>, with the sources and the build directory under <paramref name="root"/>.</summary>
    private static string CMakeBuildUnder(TempDirectory temp, string root, string deps, params (string Name, string Text)[] sources)
    {
        var lines = new List<string>();

        foreach (var (name, text) in sources)
        {
            lines.AddRange(Compiled(ObjectPath(name), temp.WriteFile(Path.Combine(root, "my src", name), text)));
        }

        return Manifest(temp, deps, lines, root);
    }

    /// <summary>
    /// A precompiled header's build as CMake 4 writes it for MSVC, measured: <c>cmake_pch.h</c> - or for
    /// C++ <c>cmake_pch.hxx</c>, whose includes it holds under <c>#ifdef __cplusplus</c> - includes each
    /// header it holds, <c>probe.h</c> and <c>once.h</c> but not <c>extra.h</c>, by its absolute path,
    /// with CRLF line ends. The object compiling it and each unit built from it force-include it, by a
    /// flag quoted where its path holds a space, and name it among their inputs; each unit names the
    /// <c>.pch</c> too, a phony whose only input is that object, spelled with <c>.\\</c>.
    /// </summary>
    /// <returns>The build directory, and the names <c>ninja -t deps</c> prints that object and the header by.</returns>
    private static (string Build, string Compiling, string Header) PrecompiledBuild(TempDirectory temp, string extension, params (string Name, string Text)[] units)
    {
        var cplusplus = extension != "c";
        var probe = temp.WriteFile(Path.Combine("my src", "probe.h"), "#define PROBE 0\n");
        var once = temp.WriteFile(Path.Combine("my src", "once.h"), "#pragma once\n#define ONCE 1\n");
        temp.WriteFile(Path.Combine("my src", "extra.h"), "#pragma once\n#define EXTRA 2\n");

        var directory = Path.Combine("CMakeFiles", "pch.dir");
        var name = cplusplus ? "cmake_pch.hxx" : "cmake_pch.h";
        var creator = cplusplus ? "cmake_pch.cxx" : "cmake_pch.c";
        var header = Path.Combine(directory, name);
        var compiling = Path.Combine(directory, creator + ".obj");
        var separator = Path.DirectorySeparatorChar;
        var includes = $"#include \"{Slashed(probe)}\"\n#include \"{Slashed(once)}\"\n";
        var held = temp.WriteFile(
            Path.Combine(BuildName, header),
            ("/* generated by CMake */\n\n#pragma system_header\n" + (cplusplus ? $"#ifdef __cplusplus\n{includes}#endif // __cplusplus\n" : includes))
                .Replace("\n", "\r\n", StringComparison.Ordinal));
        var flags = $"{DefaultFlags} {Flag("/Fp", temp.Combine(BuildName, directory, creator + ".pch"))} {Flag("/FI", held)}";

        var lines = new List<string>(Compiled(compiling, temp.WriteFile(Path.Combine(BuildName, directory, creator), "/* generated by CMake */\r\n"), $"{Flag("/Yc", held)} {flags}", header))
        {
            $"build {directory}{separator}.{separator}{separator}{creator}.pch: phony {compiling}",
        };

        foreach (var (unit, text) in units)
        {
            lines.AddRange(Compiled(Path.Combine(directory, unit + ".obj"), temp.WriteFile(Path.Combine("my src", unit), text), $"{Flag("/Yu", held)} {flags}", header, Path.Combine(directory, creator + ".pch")));
        }

        return (Manifest(temp, "msvc", lines), PchObject(creator), $"CMakeFiles/pch.dir/{name}");
    }

    /// <summary>
    /// The lines CMake writes to build <paramref name="output"/> from <paramref name="source"/> - by the C
    /// rule for a <c>.c</c> source, and the C++ one otherwise - with <paramref name="flags"/>, naming
    /// <paramref name="implicitInputs"/> after it.
    /// </summary>
    private static IEnumerable<string> Compiled(string output, string source, string flags = DefaultFlags, params string[] implicitInputs)
    {
        var rule = source.EndsWith(".c", StringComparison.Ordinal) ? CRule : CxxRule;
        var implicitly = implicitInputs.Length > 0 ? " | " + string.Join(' ', implicitInputs.Select(Escape)) : string.Empty;

        return
        [
            $"build {Escape(output)}: {rule} {Escape(source)}{implicitly} || cmake_object_order_depends_target_probe",
            $"  DEP_FILE = {output}.d",
            $"  FLAGS = {flags}",
        ];
    }

    /// <summary>
    /// Writes <c>build.ninja</c> as CMake does - including <c>CMakeFiles/rules.ninja</c>, which declares
    /// the compile rules with <paramref name="deps"/>, C++'s adding <c>/TP</c> - followed by <paramref name="statements"/>.
    /// </summary>
    private static string Manifest(TempDirectory temp, string deps, IEnumerable<string> statements, string root = "")
    {
        var build = Directory.CreateDirectory(temp.Combine(root, BuildName, "CMakeFiles")).Parent!.FullName;

        File.WriteAllText(
            Path.Combine(build, "CMakeFiles", "rules.ninja"),
            "msvc_deps_prefix = Note: including file: \n" + CompileRule(CRule, deps, "C", string.Empty) + CompileRule(CxxRule, deps, "CXX", "/TP "));
        File.WriteAllText(
            Path.Combine(build, NinjaDependencyCheck.ManifestFileName),
            string.Join('\n', ["# CMAKE generated file: DO NOT EDIT!", "ninja_required_version = 1.5", $"include {Path.Combine("CMakeFiles", "rules.ninja")}", .. statements]) + "\n");

        return build;
    }

    /// <summary>A compile rule as CMake writes it for MSVC.</summary>
    private static string CompileRule(string name, string deps, string language, string told)
        => $"rule {name}\n  depfile = $DEP_FILE\n  deps = {deps}\n"
            + $"  command = ${{LAUNCHER}}${{CODE_CHECK}}cl /nologo {told}$DEFINES $FLAGS /showIncludes /Fo$out -c $in\n"
            + $"  description = Building {language} object $out\n";

    /// <summary>An option naming a file, as CMake writes one into FLAGS: forward slashes, the whole of it quoted where it holds a space.</summary>
    private static string Flag(string option, string path)
    {
        var argument = option + Slashed(path);

        return argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
    }

    private static string Slashed(string path) => path.Replace('\\', '/');

    /// <summary>A path as a ninja build line spells it.</summary>
    private static string Escape(string path)
        => path.Replace("$", "$$", StringComparison.Ordinal).Replace(" ", "$ ", StringComparison.Ordinal).Replace(":", "$:", StringComparison.Ordinal);

    /// <summary>Checks <paramref name="build"/>, whose ninja says each of <paramref name="sources"/>' objects recorded nothing.</summary>
    private static Task<NinjaDependencyReport> Check(string build, params string[] sources)
        => CheckAnswered(build, string.Concat(sources.Select(source => $"{Object(source)}: #deps 0, deps mtime 1 (VALID)\n\n")));

    /// <summary>Checks <paramref name="build"/>, whose ninja answers <c>-t deps</c> with <paramref name="answer"/>.</summary>
    private static Task<NinjaDependencyReport> CheckAnswered(string build, string answer)
        => new NinjaDependencyCheck(new DepsAnswer(answer), FileSystem(), new HostPlatform())
            .CheckAsync(build, [], cancellationToken: TestContext.Current.CancellationToken);

    private static PhysicalFileSystem FileSystem() => new(FilePermissionsFactory.Create());

    /// <summary>A ninja whose <c>-t deps</c> answers <paramref name="output"/>.</summary>
    private sealed class DepsAnswer(string output) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(0, output, string.Empty, TimeSpan.Zero, TimedOut: false));

        public string? FindExecutable(string command) => command;
    }
}
