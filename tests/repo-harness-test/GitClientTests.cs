using System.Text;
using NSubstitute;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>The git client against real git and real repositories.</summary>
public sealed class GitClientTests
{
    [Fact]
    public void IsInstalled_FindsGit()
    {
        Assert.True(new HarnessFactory().GitClient.IsInstalled());
    }

    [Fact]
    public async Task RepositoryQueries_AnswerNotARepository_OutsideOne()
    {
        using var temp = new TempDirectory();
        var git = new HarnessFactory().GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.False(await git.IsRepositoryAsync(temp.Path, cancellationToken));
        Assert.Null(await git.GetRepositoryRootAsync(temp.Path, cancellationToken));
        Assert.Null(await git.GetMainWorktreeAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetRepositoryRootAsync_FindsTheRoot_FromASubdirectory()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        var nested = Directory.CreateDirectory(temp.Combine("a", "b")).FullName;

        var root = await harness.GitClient.GetRepositoryRootAsync(nested, cancellationToken);

        Assert.NotNull(root);
        PathAssert.Same(temp.Path, root);
        Assert.True(await harness.GitClient.IsRepositoryAsync(nested, cancellationToken));
    }

    [Fact]
    public async Task GetMainWorktreeAsync_FindsTheMainCheckout_FromInsideALinkedWorktree()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);

        var linked = elsewhere.Combine("linked");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", linked], cancellationToken);

        var main = await harness.GitClient.GetMainWorktreeAsync(linked, cancellationToken);
        var linkedRoot = await harness.GitClient.GetRepositoryRootAsync(linked, cancellationToken);

        Assert.NotNull(main);
        Assert.True(main.IsMain);
        Assert.False(main.IsBare);
        PathAssert.Same(repository.Path, main.Path);

        Assert.NotNull(linkedRoot);
        PathAssert.Same(linked, linkedRoot);
    }

    /// <summary>
    /// A submodule's main checkout is its own - never the git directory its superproject keeps it in under
    /// .git/modules, which git names as the main worktree - from the submodule and from a linked worktree
    /// of it alike, as that directory's configuration records it.
    /// </summary>
    [Fact]
    public async Task GetMainWorktreeAsync_FindsASubmodulesOwnCheckout_NotTheGitDirectoryGitNames()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var submodule = await AddSubmoduleAsync(harness, temp, cancellationToken);
        var linked = temp.Combine("linked");

        await harness.RunGitAsync(submodule, ["worktree", "add", "--detach", linked], cancellationToken);

        foreach (var from in new[] { submodule, linked })
        {
            var main = await harness.GitClient.GetMainWorktreeAsync(from, cancellationToken);

            Assert.NotNull(main);
            Assert.True(main.IsMain);
            PathAssert.Same(submodule, main.Path);
        }
    }

    /// <summary>
    /// A checkout whose git directory was made apart from it, with --separate-git-dir, is its own main
    /// checkout, though git names that directory as the main worktree and records no checkout for it. From
    /// a linked worktree of it, nothing says which checkout is the main one, and none is named.
    /// </summary>
    [Fact]
    public async Task GetMainWorktreeAsync_FindsACheckoutWhoseGitDirectoryIsApart_AsItsOwnMain()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var checkout = Directory.CreateDirectory(temp.Combine("checkout")).FullName;
        var linked = temp.Combine("linked");

        await harness.RunGitAsync(checkout, ["init", "--quiet", $"--separate-git-dir={temp.Combine("git")}", "."], cancellationToken);
        await harness.RunGitAsync(
            checkout,
            ["-c", "user.name=Harness Test", "-c", "user.email=harness@test.invalid", "commit", "--quiet", "--allow-empty", "-m", "initial"],
            cancellationToken);

        var main = await harness.GitClient.GetMainWorktreeAsync(checkout, cancellationToken);

        Assert.NotNull(main);
        PathAssert.Same(checkout, main.Path);

        await harness.RunGitAsync(checkout, ["worktree", "add", "--detach", linked], cancellationToken);

        Assert.Null(await harness.GitClient.GetMainWorktreeAsync(linked, cancellationToken));
    }

    /// <summary>
    /// Adds a repository as the submodule <c>sub</c> of another, both under <paramref name="temp"/>, and
    /// returns the submodule's checkout.
    /// </summary>
    internal static async Task<string> AddSubmoduleAsync(HarnessFactory harness, TempDirectory temp, CancellationToken cancellationToken)
    {
        var inner = Directory.CreateDirectory(temp.Combine("inner")).FullName;
        var super = Directory.CreateDirectory(temp.Combine("super")).FullName;

        await harness.InitializeGitRepositoryAsync(inner, cancellationToken);
        await harness.InitializeGitRepositoryAsync(super, cancellationToken);

        // git refuses a submodule from a local path unless told to allow the file protocol.
        await harness.RunGitAsync(super, ["-c", "protocol.file.allow=always", "submodule", "add", "--quiet", inner, "sub"], cancellationToken);

        return Path.Combine(super, "sub");
    }

    [Fact]
    public async Task Roots_AreReportedInOneForm_WhenTheRepositoryIsReachedThroughASymbolicLink()
    {
        // The tree root and the main checkout root are compared with each other. Were one
        // spelled through the link and the other resolved, the main checkout would look
        // like a worktree of itself; macOS's temporary directory, under /var, is such a link.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var real = Directory.CreateDirectory(temp.Combine("real")).FullName;
        await harness.InitializeGitRepositoryAsync(real, cancellationToken);

        var link = temp.Combine("link");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        var root = await harness.GitClient.GetRepositoryRootAsync(link, cancellationToken);
        var main = await harness.GitClient.GetMainWorktreeAsync(link, cancellationToken);

        Assert.NotNull(root);
        Assert.NotNull(main);
        PathAssert.Same(root, main.Path);
    }

    [Fact]
    public async Task Roots_AreReportedExactly_ForAPathOutsideAscii()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = Directory.CreateDirectory(temp.Combine("ação-日本")).FullName;
        await harness.InitializeGitRepositoryAsync(repository, cancellationToken);

        var root = await harness.GitClient.GetRepositoryRootAsync(repository, cancellationToken);
        var main = await harness.GitClient.GetMainWorktreeAsync(repository, cancellationToken);

        Assert.NotNull(root);
        Assert.NotNull(main);
        PathAssert.Same(repository, root);
        PathAssert.Same(repository, main.Path);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsNothing_ForACleanTree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        Assert.Empty(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));
        Assert.False(await harness.GitClient.IsDirtyAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_ReportsARename_AsOneChange()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["mv", "README.md", "RENAMED.md"], cancellationToken);

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.StartsWith("R", entry, StringComparison.Ordinal);
        Assert.EndsWith("RENAMED.md", entry, StringComparison.Ordinal);
        Assert.True(await harness.GitClient.IsDirtyAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_KeepsAnUnusualPathIntact()
    {
        // Without -z git quotes such a path and escapes every byte outside ASCII.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("ação 'quoted' file.txt", "x");

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.Equal("?? ação 'quoted' file.txt", entry);
    }

    [Fact]
    public async Task GetStatusAsync_ListsAnUntrackedFile_WhenConfigurationHidesThem()
    {
        // Under status.showUntrackedFiles=no a plain status lists nothing for a new file, and a
        // caller about to discard the tree would read it as clean.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["config", "status.showUntrackedFiles", "no"], cancellationToken);
        temp.WriteFile("notes.txt", "never committed");

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.Equal("?? notes.txt", entry);
        Assert.True(await harness.GitClient.IsDirtyAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_ReportsARenameInTheWorkTree_AsOneChange()
    {
        // git marks a rename it finds in the work tree, here of an intent-to-add file, in the
        // second status column rather than the first, and still follows it with the old path.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        File.Move(temp.Combine("README.md"), temp.Combine("RENAMED.md"));
        await harness.RunGitAsync(temp.Path, ["add", "--intent-to-add", "RENAMED.md"], cancellationToken);

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.Equal(" R RENAMED.md", entry);
    }

    [Fact]
    public async Task GetStatusAsync_Throws_WhenGitCannotAnswer()
    {
        // An empty answer here would read as a clean tree, and a caller would proceed
        // over work it never saw.
        using var temp = new TempDirectory();

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.GetStatusAsync(temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
    }

    [Fact]
    public async Task ListWorktreesAsync_ListsTheMainWorktreeFirst_AndNamesEachBranch()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);

        var detached = elsewhere.Combine("detached");
        var onBranch = elsewhere.Combine("on-branch");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", detached], cancellationToken);
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "-b", "feature", onBranch], cancellationToken);

        var worktrees = await harness.GitClient.ListWorktreesAsync(repository.Path, cancellationToken);

        Assert.Equal(3, worktrees.Count);
        Assert.Equal([true, false, false], worktrees.Select(worktree => worktree.IsMain));
        Assert.All(worktrees, worktree => Assert.False(worktree.IsBare));
        Assert.All(worktrees, worktree => Assert.Matches("^[0-9a-f]{40,64}$", worktree.Commit ?? string.Empty));
        PathAssert.Same(repository.Path, worktrees[0].Path);

        var detachedEntry = Assert.Single(worktrees, worktree => PathAssert.AreSame(detached, worktree.Path));
        var branchEntry = Assert.Single(worktrees, worktree => PathAssert.AreSame(onBranch, worktree.Path));
        Assert.Null(detachedEntry.Branch);
        Assert.Equal("feature", branchEntry.Branch);
    }

    [Fact]
    public async Task ListWorktreesAsync_Throws_OutsideARepository()
    {
        // An empty list would read as "no worktrees", a different answer from "unknown".
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsIgnoredAsync_AnswersBothWays()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(".gitignore", "*.log\n");

        Assert.True(await harness.GitClient.IsIgnoredAsync(temp.Path, "build.log", cancellationToken));
        Assert.False(await harness.GitClient.IsIgnoredAsync(temp.Path, "main.c", cancellationToken));
    }

    [Fact]
    public async Task ResolveCommitAsync_ResolvesACommit_AndAnswersNullForANameThatIsNone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        Assert.Matches("^[0-9a-f]{40,64}$", await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken) ?? string.Empty);
        Assert.Null(await harness.GitClient.ResolveCommitAsync(temp.Path, "no-such-branch", cancellationToken));

        // A name that starts with a dash is a name to look up, never an option to obey.
        Assert.Null(await harness.GitClient.ResolveCommitAsync(temp.Path, "--output=elsewhere", cancellationToken));
    }

    [Fact]
    public async Task ReadFileAtCommitAsync_ReadsTheCommittedContent_AndNullForAFileThatWasNotThere()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "committed ✅\r\nline two\n");
        await harness.CommitAllAsync(temp.Path, "notes", cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "changed since");

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);

        Assert.Equal("committed ✅\r\nline two\n", await harness.GitClient.ReadFileAtCommitAsync(temp.Path, head!, "docs/notes.md", cancellationToken));
        Assert.Null(await harness.GitClient.ReadFileAtCommitAsync(temp.Path, head!, "docs/absent.md", cancellationToken));
    }

    /// <summary>
    /// Every file of a commit is read by one git process, as it was committed: its line breaks, its
    /// characters beyond ASCII, a byte order mark dropped as reading the file on its own drops it, and
    /// bytes that are not text as the replacement they decode to.
    /// </summary>
    /// <summary>
    /// An index made to hold exactly a set of files holds them - each staged as it stands on disk, a name
    /// no command line would carry among them - and no other: an entry the set does not name is removed,
    /// and a named file that is not there is not held.
    /// </summary>
    [Fact]
    public async Task IndexExactlyAsync_LeavesTheIndexHoldingExactlyTheFilesNamed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var created = await harness.GitClient.RunAsync(temp.Path, ["init", "--quiet", "."], cancellationToken: cancellationToken);
        Assert.True(created.Succeeded, created.FailureMessage);

        temp.WriteFile(Path.Combine("src", "a.c"), "a\n");
        temp.WriteFile(Path.Combine("src", "-b.c"), "b\n");
        temp.WriteFile("stale.txt", "old\n");

        async Task<IReadOnlyList<string>> IndexedAsync()
            => [.. (await harness.GitClient.ListIndexAsync(temp.Path, cancellationToken)).Select(entry => entry.Path).Order(StringComparer.Ordinal)];

        await harness.GitClient.IndexExactlyAsync(temp.Path, ["src/a.c", "src/-b.c", "stale.txt"], cancellationToken);
        Assert.Equal(["src/-b.c", "src/a.c", "stale.txt"], await IndexedAsync());

        // A second set replaces the first: what it drops leaves, and a file named but gone is not held.
        await harness.GitClient.IndexExactlyAsync(temp.Path, ["src/a.c", "src/-b.c", "missing.c"], cancellationToken);
        Assert.Equal(["src/-b.c", "src/a.c"], await IndexedAsync());

        // Staged as it stands: a file changed on disk is staged again, so the index is not stale about it.
        temp.WriteFile(Path.Combine("src", "a.c"), "changed\n");
        await harness.GitClient.IndexExactlyAsync(temp.Path, ["src/a.c", "src/-b.c"], cancellationToken);

        var status = await harness.GitClient.RunAsync(temp.Path, ["status", "--porcelain", "--untracked-files=no"], cancellationToken: cancellationToken);
        Assert.DoesNotContain(" M src/a.c", status.StandardOutput, StringComparison.Ordinal);

        // And an empty set empties it.
        await harness.GitClient.IndexExactlyAsync(temp.Path, [], cancellationToken);
        Assert.Empty(await IndexedAsync());
    }

    [Fact]
    public async Task ReadFilesAtCommitAsync_ReadsEveryFile_ThroughOneProcess()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "committed ✅\r\nline two\n");
        temp.WriteFile(Path.Combine("src", "naïve name.txt"), "日本語\n");
        await File.WriteAllBytesAsync(temp.Combine("bom.txt"), [0xEF, 0xBB, 0xBF, (byte)'b', (byte)'o', (byte)'m'], cancellationToken);
        await File.WriteAllBytesAsync(temp.Combine("data.bin"), [0x00, 0xFF, 0x0A, 0x41], cancellationToken);
        await File.WriteAllBytesAsync(temp.Combine("wide.txt"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("wide ✅\n")], cancellationToken);
        await harness.CommitAllAsync(temp.Path, "files", cancellationToken);

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);
        var processes = new CountingProcesses(harness.ProcessRunner);

        var read = await new GitClient(processes, harness.Output).ReadFilesAtCommitAsync(
            temp.Path,
            head!,
            ["docs/notes.md", "src/naïve name.txt", "bom.txt", "data.bin", "wide.txt"],
            cancellationToken);

        Assert.Equal("committed ✅\r\nline two\n", read["docs/notes.md"]);
        Assert.Equal("日本語\n", read["src/naïve name.txt"]);
        Assert.Equal("bom", read["bom.txt"]);
        Assert.Equal("\0\uFFFD\nA", read["data.bin"]);
        Assert.Equal("wide ✅\n", read["wide.txt"]);
        Assert.Equal(["cat-file"], processes.Started.Select(request => request.Arguments[0]));
    }

    /// <summary>
    /// A path that names no file at the commit is answered null: one never there, a directory, a
    /// submodule's entry. Only the commit's listing tells such a path from a file git cannot read, so
    /// it costs that one listing more - and a read of files that are all there costs nothing more.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_AnswersNull_ForAPathThatNamesNoFileThere()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "notes\n");
        await harness.CommitAllAsync(temp.Path, "notes", cancellationToken);

        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"160000,{first},sub"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "submodule"], cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var processes = new CountingProcesses(harness.ProcessRunner);

        var read = await new GitClient(processes, harness.Output).ReadFilesAtCommitAsync(
            temp.Path,
            head,
            ["docs/notes.md", "docs/absent.md", "docs", "sub"],
            cancellationToken);

        Assert.Equal("notes\n", read["docs/notes.md"]);
        Assert.Null(read["docs/absent.md"]);
        Assert.Null(read["docs"]);
        Assert.Null(read["sub"]);
        Assert.Equal(["cat-file", "ls-tree"], processes.Started.Select(request => request.Arguments[0]));
    }

    /// <summary>
    /// A file the commit lists that git cannot read throws, naming it, read among others or alone. git
    /// answers "missing" for it as for a path that names nothing, and passed off as absent, a registry
    /// nobody could read was reported missing at the base.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_Throws_ForAFileTheCommitListsButGitCannotRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "lost.md"), "lost\n");
        await harness.CommitAllAsync(temp.Path, "lost", cancellationToken);
        await harness.LoseObjectAsync(temp.Path, "HEAD:docs/lost.md", cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;

        var among = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFilesAtCommitAsync(
            temp.Path, head, ["README.md", "docs/lost.md"], cancellationToken));
        var alone = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFileAtCommitAsync(
            temp.Path, head, "docs/lost.md", cancellationToken));

        foreach (var refusal in new[] { among, alone })
        {
            Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
            Assert.Contains("'docs/lost.md'", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("git fsck", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A path holding a line break - which git allows, and no line of the batch can carry - is read by
    /// the object the commit's listing gives it, and is null where the commit lists no such file.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_ReadsAPathHoldingALineBreak()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "git for Windows refuses a name holding a line break.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.StageAsync(temp.Path, "\"docs/line\\nbreak.md\"", "wrapped\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "line break"], cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var read = await harness.GitClient.ReadFilesAtCommitAsync(
            temp.Path,
            head,
            ["docs/line\nbreak.md", "docs/other\nbreak.md", "README.md"],
            cancellationToken);

        Assert.Equal("wrapped\n", read["docs/line\nbreak.md"]);
        Assert.Null(read["docs/other\nbreak.md"]);
        Assert.Equal("test repository", read["README.md"]);
    }

    /// <summary>
    /// A commit's files are listed from the repository's root whichever directory git is asked from,
    /// a submodule's entry left out. A name that is not UTF-8 is said to be one, and quoted as git
    /// quotes it.
    /// </summary>
    [Fact]
    public async Task ListFilesAtCommitAsync_ListsTheCommitsFiles_EachNameAsGitHoldsIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("src", "naïve name.txt"), "text\n");
        await harness.CommitAllAsync(temp.Path, "files", cancellationToken);

        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.StageAsync(temp.Path, "\"src/caf\\351.md\"", "bytes\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"160000,{first},sub"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "names"], cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var listed = await harness.GitClient.ListFilesAtCommitAsync(temp.Combine("src"), head, cancellationToken);

        Assert.Equal(
            [
                new GitName("README.md", true),
                new GitName("src/caf�.md", false) { Quoted = @"src/caf\351.md" },
                new GitName("src/naïve name.txt", true),
            ],
            listed.OrderBy(name => name.Text, StringComparer.Ordinal));
    }

    /// <summary>
    /// A listing's names are read as git holds them: one beyond ASCII as its text, and one that is not
    /// UTF-8 said to be one, and quoted as git quotes it. Read as UTF-8 text alone, that name became
    /// another, which named no file, and nothing said so.
    /// </summary>
    [Fact]
    public async Task ListNamesAsync_ReadsEachNameAsGitHoldsIt_AndThrowsWhenGitFails()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("naïve.md", "text\n");
        await harness.StageAsync(temp.Path, "\"caf\\351.md\"", "bytes\n", cancellationToken);

        var listed = await harness.GitClient.ListNamesAsync(
            temp.Path,
            ["ls-files", "-z", "--cached", "--others"],
            cancellationToken);

        Assert.Equal(
            [
                new GitName("README.md", true),
                new GitName("caf�.md", false) { Quoted = @"caf\351.md" },
                new GitName("naïve.md", true),
            ],
            listed.OrderBy(name => name.Text, StringComparer.Ordinal));

        await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ListNamesAsync(
            temp.Path,
            ["ls-files", "-z", "--no-such-option"],
            cancellationToken));
    }

    /// <summary>
    /// One path this machine spelled has this machine's separator turned into git's: on Windows,
    /// 'docs\notes.md' is the file git calls 'docs/notes.md'.
    /// </summary>
    [Fact]
    public async Task ReadFileAtCommitAsync_ReadsAPathThisMachineSpelled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows separates a path with a backslash.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "notes\n");
        await harness.CommitAllAsync(temp.Path, "notes", cancellationToken);

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);

        Assert.Equal("notes\n", await harness.GitClient.ReadFileAtCommitAsync(temp.Path, head!, @"docs\notes.md", cancellationToken));
    }

    /// <summary>
    /// A path is read as git spells it. A backslash is an ordinary character in a name on Linux and
    /// macOS; rewritten as a separator, the name read another file, and its answer was filed under a
    /// key nobody had asked for.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_ReadsAPathAsGitSpellsIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows cannot hold a backslash in a file name.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, @"odd\name.txt"), "odd\n", cancellationToken);
        await harness.CommitAllAsync(temp.Path, "odd", cancellationToken);

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);
        var read = await harness.GitClient.ReadFilesAtCommitAsync(temp.Path, head!, [@"odd\name.txt"], cancellationToken);

        Assert.Equal("odd\n", read[@"odd\name.txt"]);
    }

    /// <summary>
    /// A commit git cannot read is refused when many files are read from it as when one is: git
    /// answers 'missing' for it as it does for an absent file, and read that way it passed off a
    /// commit nobody could read as files that were not there.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_Throws_ForACommitGitCannotRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFilesAtCommitAsync(
            temp.Path, new string('0', 40), ["README.md", "docs/notes.md"], cancellationToken));
    }

    [Fact]
    public async Task ReadFileAtCommitAsync_Throws_ForACommitGitCannotRead()
    {
        // Answering null here would pass off an unreadable commit as a file that did not exist yet.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFileAtCommitAsync(
            temp.Path, new string('0', 40), "README.md", cancellationToken));
    }

    [Fact]
    public async Task IsIgnoredAsync_Throws_WhenGitCannotAnswer()
    {
        // check-ignore exits 128 outside a repository. Read as "not ignored", a secret
        // would be reported as safe to copy because the question itself failed.
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.IsIgnoredAsync(temp.Path, ".secret", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// git names the rule deciding each path, in the order asked, whether or not the path exists: one
    /// that ignores, a re-include, a directory above the path - which no re-include below it reaches -
    /// and none at all.
    /// </summary>
    [Fact]
    public async Task ExplainIgnoredAsync_NamesTheRuleDecidingEachPath_InTheOrderAsked()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "*.log\n!keep.log\nout/\n!out/kept.txt\n");

        var decisions = await harness.GitClient.ExplainIgnoredAsync(
            temp.Path,
            ["a.log", "keep.log", "out/kept.txt", "src/main.c"],
            token);

        Assert.Equal(
            [
                new IgnoreDecision("a.log", ".gitignore", 1, "*.log"),
                new IgnoreDecision("keep.log", ".gitignore", 2, "!keep.log"),
                new IgnoreDecision("out/kept.txt", ".gitignore", 3, "out/"),
                new IgnoreDecision("src/main.c", null, 0, null),
            ],
            decisions);
        Assert.Equal([true, false, true, false], decisions.Select(decision => decision.Ignored));
    }

    /// <summary>
    /// A question git could not answer is refused, never read as "no rule": every managed path would
    /// then read as ruled by nothing.
    /// </summary>
    [Fact]
    public async Task ExplainIgnoredAsync_Throws_WhenGitCannotAnswer()
    {
        using var temp = new TempDirectory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.ExplainIgnoredAsync(temp.Path, [".secret"], TestContext.Current.CancellationToken));

        // Refused for git's own failure, naming it - never for an answer read in a form it was not.
        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.StartsWith("Could not ask git which rules decide 1 path(s)", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path beyond a symbolic link makes git refuse the whole question. Asked again one at a time,
    /// every other path is answered, and that one says why it was not, in git's words.
    /// </summary>
    [Fact]
    public async Task ExplainIgnoredAsync_AnswersEveryOtherPath_WhereOneIsBeyondALink()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "*.log\n");

        try
        {
            Directory.CreateSymbolicLink(temp.Combine("linked"), elsewhere.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        var decisions = await harness.GitClient.ExplainIgnoredAsync(temp.Path, ["linked/a.log", "a.log", "src/main.c"], token);

        Assert.Null(decisions[0].Source);
        Assert.Contains("symbolic link", decisions[0].Unanswered, StringComparison.Ordinal);
        Assert.Equal(new IgnoreDecision("a.log", ".gitignore", 1, "*.log"), decisions[1]);
        Assert.Equal(new IgnoreDecision("src/main.c", null, 0, null), decisions[2]);
    }

    /// <summary>Where git answers about none of the paths, one at a time either, the question is refused as before.</summary>
    [Fact]
    public async Task ExplainIgnoredAsync_Throws_WhenGitAnswersAboutNoneOfThePaths()
    {
        using var temp = new TempDirectory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.ExplainIgnoredAsync(temp.Path, [".secret", ".key"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.StartsWith("Could not ask git which rules decide 2 path(s)", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer whose line is not a number is refused as one this build cannot read, naming the path,
    /// rather than escaping as a defect in this tool.
    /// </summary>
    [Fact]
    public void ReadDecisions_RefusesALineThatIsNotANumber()
    {
        var refusal = Assert.Throws<HarnessException>(() => GitClient.ReadDecisions(".gitignore\0one\0*.log\0a.log\0", ["a.log"]));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Equal("git answered which rule decides 'a.log' in a form this build cannot read: 'one' is not a line number.", refusal.Message);
    }
}

/// <summary>What the git client sends to git, and how it reads answers, against scripted results.</summary>
public sealed class GitClientProtocolTests
{
    private const string NotARepository = "fatal: not a git repository (or any of the parent directories): .git";

    [Fact]
    public async Task EveryCommand_TellsGitNobodyCanAnswerAPrompt()
    {
        var (git, requests) = Scripted(Exited(0));

        await git.RunAsync("/repo", ["fetch"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("0", Assert.Single(requests).Environment["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public async Task EveryCommand_ClearsTheVariablesThatWouldPointGitAtAnotherTree()
    {
        // GIT_DIR, GIT_WORK_TREE and GIT_INDEX_FILE each outrank `-C <directory>`. A git hook runs
        // with all three set, so a harness command invoked from a hook, or from a shell left in
        // another checkout, would read and write a repository nobody named.
        var (git, requests) = Scripted(Exited(0));

        await git.RunAsync("/repo", ["status"], cancellationToken: TestContext.Current.CancellationToken);

        var environment = Assert.Single(requests).Environment;

        foreach (var name in (string[])["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"])
        {
            Assert.True(environment.ContainsKey(name), $"{name} was not cleared.");
            Assert.Null(environment[name]);
        }
    }

    [Fact]
    public async Task RepositoryQueries_RunUntranslated_WhileOtherCommandsKeepTheUsersLocale()
    {
        // The queries are matched against git's English messages. Everything else, hooks
        // included, must keep the user's locale.
        var (git, requests) = Scripted(Exited(0, "/repo\n"));
        var cancellationToken = TestContext.Current.CancellationToken;

        await git.GetRepositoryRootAsync("/repo", cancellationToken);
        await git.RunAsync("/repo", ["commit", "-m", "x"], cancellationToken: cancellationToken);

        Assert.Equal("C", requests[0].Environment["LC_ALL"]);
        Assert.False(requests[1].Environment.ContainsKey("LC_ALL"));
    }

    [Fact]
    public async Task NotARepository_IsAnAnswer()
    {
        var (git, _) = Scripted(Exited(128, stderr: NotARepository));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.False(await git.IsRepositoryAsync("/somewhere", cancellationToken));
        Assert.Null(await git.GetRepositoryRootAsync("/somewhere", cancellationToken));
        Assert.Null(await git.GetMainWorktreeAsync("/somewhere", cancellationToken));
    }

    [Fact]
    public async Task ARepositoryGitRefuses_IsAFailure_NotAMissingRepository()
    {
        // Reported as "not a repository", this sends the user to `git init` inside a
        // repository that exists, when git's own message names the fix.
        var (git, _) = Scripted(Exited(
            128,
            stderr: "fatal: detected dubious ownership in repository at '/repo'"));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            git.IsRepositoryAsync("/repo", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
        Assert.Contains("dubious ownership", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeout_IsNeverReadAsNotARepository()
    {
        var (git, _) = Scripted(Exited(-1, stderr: NotARepository, timedOut: true));

        await Assert.ThrowsAsync<HarnessException>(() =>
            git.GetRepositoryRootAsync("/repo", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetMainWorktreeAsync_RecognisesABareRepository()
    {
        var (git, _) = Scripted(Exited(
            0,
            "worktree /srv/repo.git\nbare\n\nworktree /srv/wt\nHEAD 0123456789abcdef0123456789abcdef01234567\ndetached\n\n"));

        var main = await git.GetMainWorktreeAsync("/srv/wt", TestContext.Current.CancellationToken);

        Assert.NotNull(main);
        Assert.True(main.IsMain);
        Assert.True(main.IsBare);
        Assert.EndsWith("repo.git", main.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatusAsync_CountsARenameOrCopyOnce_WhicheverColumnMarksIt()
    {
        // A rename or copy is followed by its original path, whether the first status column marks
        // it, as for a staged one, or the second, as for one git finds in the work tree. No other
        // entry is followed by one, an unmerged one included.
        var (git, _) = Scripted(Exited(
            0,
            "R  new.txt\0old.txt\0 R moved.txt\0was.txt\0C  copy.txt\0big.txt\0UU f.txt\0?? untracked.txt\0"));

        var entries = await git.GetStatusAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(["R  new.txt", " R moved.txt", "C  copy.txt", "UU f.txt", "?? untracked.txt"], entries);
    }

    [Fact]
    public async Task ListWorktreesAsync_ReadsALock_WithOrWithoutAReason()
    {
        const string Head = "HEAD 0123456789abcdef0123456789abcdef01234567\n";
        var (git, _) = Scripted(Exited(
            0,
            $"worktree /repo\n{Head}branch refs/heads/main\n\n"
            + $"worktree /repo/a\n{Head}detached\nlocked on a USB disk\n\n"
            + $"worktree /repo/b\n{Head}detached\nlocked\n\n"));

        var worktrees = await git.ListWorktreesAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "on a USB disk", string.Empty }, worktrees.Select(worktree => worktree.LockReason));
    }

    [Fact]
    public async Task ListWorktreesAsync_ReadsAnUnbornHead_AsNoCommit()
    {
        // git lists the HEAD of an orphan branch, which names no commit yet, as the null object id.
        // Handed back to git as a commit, it fails every command it reaches.
        var (git, _) = Scripted(Exited(
            0,
            "worktree /repo\nHEAD 0123456789abcdef0123456789abcdef01234567\nbranch refs/heads/main\n\n"
            + "worktree /repo/a\nHEAD 0000000000000000000000000000000000000000\nbranch refs/heads/unborn\n\n"));

        var worktrees = await git.ListWorktreesAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(
            new string?[] { "0123456789abcdef0123456789abcdef01234567", null },
            worktrees.Select(worktree => worktree.Commit));
    }

    [Fact]
    public async Task ListIndexAsync_ReadsTheFlagsThatHideAnEdit_AndSubmodules()
    {
        var (git, _) = Scripted(Exited(
            0,
            "H 100644 aaaa 0\tplain.txt\0h 100644 bbbb 0\tassumed.txt\0S 100755 cccc 0\tsparse dir/tool\0H 160000 dddd 0\tlib\0"));

        var entries = await git.ListIndexAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(["plain.txt", "assumed.txt", "sparse dir/tool", "lib"], entries.Select(entry => entry.Path));
        Assert.Equal([false, true, false, false], entries.Select(entry => entry.IsAssumedUnchanged));
        Assert.Equal([false, false, true, false], entries.Select(entry => entry.IsSkipWorktree));
        Assert.Equal([false, false, false, true], entries.Select(entry => entry.IsSubmodule));
    }

    [Fact]
    public async Task IsIgnoredAsync_TreatsATimeout_AsAFailure()
    {
        var (git, _) = Scripted(Exited(1, timedOut: true));

        await Assert.ThrowsAsync<HarnessException>(() =>
            git.IsIgnoredAsync("/repo", ".secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EchoedCommands_StreamBothOutputs()
    {
        var (git, requests) = Scripted(Exited(0));

        await git.RunAsync("/repo", ["pull"], echoOutput: true, TestContext.Current.CancellationToken);

        var request = Assert.Single(requests);
        Assert.NotNull(request.OnOutputLine);
        Assert.NotNull(request.OnErrorLine);
    }

    private static (GitClient Client, List<ProcessRequest> Requests) Scripted(ProcessResult result)
    {
        var requests = new List<ProcessRequest>();
        var runner = Substitute.For<IProcessRunner>();

        runner.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Arg.Any<CancellationToken>()).Returns(result);
        runner.FindExecutable("git").Returns("git");

        return (new GitClient(runner, Substitute.For<IHarnessOutput>()), requests);
    }

    private static ProcessResult Exited(int exitCode, string standardOutput = "", string stderr = "", bool timedOut = false)
        => new(exitCode, standardOutput, stderr, TimeSpan.Zero, timedOut);
}

public sealed class GitCommandResultTests
{
    [Fact]
    public void ATimedOutCommand_NeverSucceeds_WhateverItsExitCode()
    {
        var result = new GitCommandResult(0, string.Empty, string.Empty, TimedOut: true);

        Assert.False(result.Succeeded);
        Assert.Contains("time budget", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureMessage_PrefersStandardError_ThenStandardOutput()
    {
        Assert.Equal("from stderr", new GitCommandResult(1, "from stdout", " from stderr \n").FailureMessage);
        Assert.Equal("from stdout", new GitCommandResult(1, "from stdout\n", "  ").FailureMessage);
    }

    [Fact]
    public void FailureMessage_IsNeverEmpty()
    {
        // Callers interpolate it after a colon; nothing after the colon explains nothing.
        Assert.Equal("git exited with code 128.", new GitCommandResult(128, string.Empty, string.Empty).FailureMessage);
    }

    [Fact]
    public void OutputLines_DropsBlankLinesAndCarriageReturns()
    {
        var result = new GitCommandResult(0, "one\r\n\r\ntwo\n\n", string.Empty);

        Assert.Equal(["one", "two"], result.OutputLines);
    }
}

/// <summary>Runs every program through <paramref name="inner"/>, and keeps what each was asked to start.</summary>
internal sealed class CountingProcesses(IProcessRunner inner) : IProcessRunner
{
    private readonly List<ProcessRequest> _started = [];

    /// <summary>Every program started, in order.</summary>
    public IReadOnlyList<ProcessRequest> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        lock (_started)
        {
            _started.Add(request);
        }

        return inner.RunAsync(request, cancellationToken);
    }

    public string? FindExecutable(string command) => inner.FindExecutable(command);
}
