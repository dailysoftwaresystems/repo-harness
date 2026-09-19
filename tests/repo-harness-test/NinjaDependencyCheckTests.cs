using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// Which objects recorded no header dependencies, and which of them legitimately so. Only an object
/// built under <c>deps = msvc</c> whose source includes nothing records zero legitimately, and the
/// manifest is read the way ninja reads it - CMake keeps its rules in a file <c>build.ninja</c>
/// includes, and names each source by an escaped absolute path - or the excuse never applies to a
/// build CMake generated at all.
/// </summary>
public sealed class NinjaDependencyCheckTests
{
    private const string Rule = "C_COMPILER__probe_unscanned_Debug";

    /// <summary>
    /// A build laid out the way CMake lays out an MSVC one: the rule, with <c>deps = msvc</c>, in
    /// <c>CMakeFiles/rules.ninja</c>, and each source named absolutely, escaped. A source that includes
    /// nothing is excused; one that includes a header and recorded none is not.
    /// </summary>
    [Fact]
    public async Task AnMsvcObjectWhoseSourceIncludesNothing_IsExcused_ThroughTheRulesCMakeIncludes()
    {
        using var temp = new TempDirectory();
        var build = CMakeBuild(temp, deps: "msvc", ("main.c", "int main(void) { return 0; }\n"), ("other.c", "#include \"probe.h\"\nint other(void) { return 1; }\n"));

        var report = await Check(build, "main.c", "other.c");

        Assert.Equal(2, report.ObjectsRead);
        Assert.Equal([Object("other.c")], report.WithoutHeaders);
        Assert.Equal(temp.Combine("my src", "main.c"), Assert.Single(report.Excused, pair => pair.Key == Object("main.c")).Value);
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

        var runner = new DepsAnswer("my file.o: #deps 0, deps mtime 1 (VALID)\n\n");
        var report = await new NinjaDependencyCheck(runner, FileSystem()).CheckAsync(build, [], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, report.ObjectsRead);
        Assert.Equal(["my file.o"], report.WithoutHeaders);
    }

    /// <summary>
    /// A build line is read with ninja's escapes undone, its implicit outputs and inputs left out; a
    /// line ending in an unescaped <c>$</c> goes on on the next, and one ending in an escaped one does not.
    /// </summary>
    [Fact]
    public void ABuildLine_IsReadWithNinjasEscapesUndone()
    {
        var (outputs, rule, source) = NinjaManifest.BuildLine(@"out$ dir/a$$b.obj | a.pdb: cc C$:\my$ src\a.c | a.h || order $$HOME");

        Assert.Equal(["out dir/a$b.obj"], outputs);
        Assert.Equal("cc", rule);
        Assert.Equal(@"C:\my src\a.c", source);
        Assert.Null(NinjaManifest.BuildLine("all.stamp: phony || order").Source);

        Assert.Equal(
            ["build a.obj: cc a.c b.c", "  deps = msvc", "x = cost $$"],
            NinjaManifest.LogicalLines("build a.obj: cc a.c $\n    b.c\n  deps = msvc\r\nx = cost $$\n").Where(line => line.Length > 0));
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

    /// <summary>The object's name in the spelling <c>ninja -t deps</c> prints it in.</summary>
    private static string Object(string source) => $"CMakeFiles/probe.dir/{source}.obj";

    /// <summary>
    /// A build directory as CMake writes one: <c>build.ninja</c> including <c>CMakeFiles/rules.ninja</c>,
    /// which declares the compile rule, and one build line per source, each named absolutely under a
    /// directory whose name holds a space.
    /// </summary>
    private static string CMakeBuild(TempDirectory temp, string deps, params (string Name, string Text)[] sources)
    {
        var build = Directory.CreateDirectory(temp.Combine("build")).FullName;
        Directory.CreateDirectory(Path.Combine(build, "CMakeFiles"));

        File.WriteAllText(
            Path.Combine(build, "CMakeFiles", "rules.ninja"),
            $"msvc_deps_prefix = Note: including file: \nrule {Rule}\n  depfile = $DEP_FILE\n  deps = {deps}\n  command = cl /nologo $FLAGS /showIncludes /Fo$out -c $in\n  description = Building C object $out\n");

        var lines = new List<string>
        {
            "# CMAKE generated file: DO NOT EDIT!",
            "ninja_required_version = 1.5",
            $"include {Path.Combine("CMakeFiles", "rules.ninja")}",
        };

        foreach (var (name, text) in sources)
        {
            var path = temp.WriteFile(Path.Combine("my src", name), text);
            var output = Path.Combine("CMakeFiles", "probe.dir", name + ".obj");

            lines.Add($"build {output}: {Rule} {Escape(path)} || cmake_object_order_depends_target_probe");
            lines.Add($"  DEP_FILE = {output}.d");
            lines.Add("  FLAGS = /DWIN32 /Zi /Od");
        }

        File.WriteAllText(Path.Combine(build, NinjaDependencyCheck.ManifestFileName), string.Join('\n', lines) + "\n");

        return build;
    }

    /// <summary>A path as a ninja build line spells it.</summary>
    private static string Escape(string path)
        => path.Replace("$", "$$", StringComparison.Ordinal).Replace(" ", "$ ", StringComparison.Ordinal).Replace(":", "$:", StringComparison.Ordinal);

    /// <summary>Checks <paramref name="build"/>, whose ninja says each of <paramref name="sources"/>' objects recorded nothing.</summary>
    private static Task<NinjaDependencyReport> Check(string build, params string[] sources)
        => new NinjaDependencyCheck(new DepsAnswer(string.Concat(sources.Select(source => $"{Object(source)}: #deps 0, deps mtime 1 (VALID)\n\n"))), FileSystem())
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
