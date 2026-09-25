# DssHarness architecture

## Why this exists

A repository's build, test and cross-host work is usually a pile of paired
`.sh`/`.ps1` scripts that drift apart, encode one repository's facts, and fail
differently on each platform. `DssHarness` replaces that with one
cross-platform .NET tool whose behaviour is driven entirely by `config.json`.

**Nothing about any specific repository, language or toolchain is compiled in.**
If a behaviour cannot be expressed in `config.json`, that is a defect.

## Status

Implemented today: `init`, `verify-git`, `create-worktree`, `delete-worktree`,
`list-worktree`, `check-root-litter`, the anchor commands (`write-anchor`, `set-anchor`,
`read-anchor`, `read-anchors`, `check-anchor-balance`, `check-anchor-citations`),
`fix-line-endings`, `check-ci-legs`, `legs`, `host-exec`, `install-missing-tools`,
`sync`, `build`, `test`, `run` and `help`.

Every section of this document now describes code that exists. Where a rule is stated in
the present tense it is enforced, and a gap between the two is a defect in the tool rather
than a section still waiting to be written.

## Layering

```
RepoHarness.Cli        Program.cs: argument parsing and dependency wiring only
RepoHarness.Core       domain, services, abstractions
repo-harness-test      tests

(RepoHarness.Adapters was planned for the build adapters; they arrived in
RepoHarness.Core instead, beside the build service that is their only caller, and the
project was not created.)
```

`Core` is a library, so "no logic in Program.cs" is enforced by the assembly
boundary rather than by discipline.

### The platform layer

`HostPlatform`, `FilePermissionsFactory` and `ProcessTableFactory` are the **only**
types that observe the operating system. Everything else depends on `IHostPlatform`,
`IFilePermissions`, `IProcessTable`, `IProcessRunner` and `IFileSystem`.

A platform-specific implementation is created only where behaviour genuinely
differs. Today that is two families of behaviour.

**File permissions**: making a file readable only by its owner, deciding whether a file
is a program at all, and whether other users can read or change a file. Unix answers
these with mode bits; Windows answers them with access lists and file extensions. Hence
a POSIX implementation and a Windows implementation, and no third.

**The process table**: what else is running on this machine, with each process's parent,
its start time and its command line, which is what a leg's contention check reads.
Windows publishes it through WMI, Linux through `/proc`, macOS through `ps`. Behind the
seam so that nothing above it branches on the operating system to find out, and so a test
can say what the machine was running without the machine having to be running it.

### Process execution

`IProcessRunner` takes arguments as a **list**, never a command line string.
Shell quoting is therefore not part of the system. This is not a style
preference: a neighbouring repository measured a shell-string invocation whose
empty variable expanded into `rsync -a --delete / /`, ran for 70 minutes and
reported exit 0.

Child output is decoded, and child input encoded, as UTF-8 on every platform. Left to
its default, Windows decodes redirected output with the console's legacy code page, so
a path such as `C:\Users\João` printed by git would reach the harness garbled on
Windows alone.

A missing working directory is reported as exactly that. Linux and macOS report it
with the same error number as a missing executable, which would otherwise surface as
"git is not installed".

A program named without a path is looked up in the `PATH` directories and nowhere else -
the `PATH` the child is given, which ends with the directories the survey found a leg's
programs in. Left to the runtime, it would be looked for beside the running executable and in
the current directory first, and the current directory is usually the repository, so a file
committed there under a tool's name would run in place of the tool. On Windows a name
without an extension starts only `<name>.exe`, never a batch file, whose arguments cmd.exe
would parse a second time, and a path without one starts that path with `.exe` added. A
program a leg's configuration names by a relative path is read from the directory its phase
starts in - a step's working directory, the test runner's - never from wherever the harness was
started: for a leg on a worktree those are different copies of the same file.

### Paths come from git

Every repository path the harness holds — the tree a command acts on, the main
checkout, each worktree — comes from git, in the single form git resolves it to, with
symbolic links followed. A directory the user typed is only ever used to tell git where
to start. If one root were spelled as typed and another as git resolved it, the same
checkout would compare unequal to itself wherever a link is involved: macOS keeps its
temporary directory under the `/var` link, and any repository cloned beneath a linked
directory behaves the same way.

`--directory` is resolved to an absolute path, and confirmed to exist, once, before any
command runs.

git's messages are matched in two places only: telling "not a repository" apart from
"git could not look", and "not a gitdir" apart from the same when a directory among a
worktree's submodule repositories is examined. Those read-only queries run with
`LC_ALL=C`, so a translated git still answers in the words being matched. Every other git
command, including those that run the user's hooks, keeps the user's locale.

### Reading config.json

The file is edited by hand, so it is checked as it is read, and every problem is reported
at once, with the line it concerns where the parser knows it:

- An unknown key is an error. A misspelled setting that is silently dropped reverts to
  its default while the file plainly appears to set it.
- `null` is refused wherever the model does not allow it, including inside lists and
  maps, which the serializer does not check on its own. Loaded, it would fail much later
  as a crash in whatever first read it.
- References are resolved: a leg naming an undeclared host or emulator, an emulator that
  runs programs for another processor than the leg's, a success pattern that does not
  compile, a commit template placeholder no variable declares.
- **A toolchain CMake builds with names its compiler**: `CC` or `CXX` under `env`,
  `CMAKE_C_COMPILER`, `CMAKE_CXX_COMPILER` or `CMAKE_TOOLCHAIN_FILE` under `cacheVars`, or a
  `compilerId` CMake must configure it with. One naming none is refused, because CMake then takes
  whatever compiler it finds first - how a leg named `msvc` built with MinGW's gcc on every run until
  `CC` was declared - and the build directory guard has nothing to hold a later build to. A
  toolchain only .NET or Dart projects build with names none: those resolve their own compilers,
  and its `cacheVars` are that build's properties. `init`'s `msvc` toolchain names `cl`.
- **Every CMake configure is asked which compilers it resolved**, through the file API: the build
  writes the `toolchains-v1` query into its build directory before configuring, notes the answers
  already there, and reads the one that configure wrote. Each leg's line names what CMake answered -
  `compiler: MSVC 19.51.36231 (C, CXX)`, one entry per compiler with the languages it serves -
  whatever the verdict, and `--json` carries it as `compilers`. A configure that fails answers
  nothing about the compilers - CMake 4 writes an error index in its place, and leaves the last
  successful configure's answer where it was - so it names none, never the ones an earlier
  configure resolved; told apart by what was there before, never by the times in the names, which
  a clock that stepped back would reorder. It travels beside the detail rather than inside it, so a
  leg a host ran is named once on the machine that reports it. A toolchain's `compilerId` -
  `{"C": "MSVC", "CXX": "MSVC"}`, in CMake's own ids - holds the build to it: a compiler CMake
  configured that contradicts it fails the leg before anything is built, and a declared language
  CMake identified no compiler for leaves it `unwitnessed`, naming why - an older CMake writes no
  answer, and a misspelled language is never answered. The answer holds what the top-level
  directory holds, so a language only a subdirectory enables - C, where a C++ project fetches
  googletest, whose own `project()` declares C and C++ - comes with no id: with its compiler's
  path where CMake caches one, and under a Visual Studio generator, which caches none, with no
  path either. A leg declaring the C it built with was left `unwitnessed` while its configure log
  named that compiler. CMake keeps each language's identification once for the whole build
  directory, in `CMakeFiles/<version>/CMake<language>Compiler.cmake`, which enabling the language
  in any directory loads, and the language is identified from that record. The record is held to
  the answer, because it can be a later configure's: one that identifies the compiler again -
  given another, or run with `--fresh` - rewrites it, and one that then fails writes no answer,
  which leaves the last one's beside a record of a compiler that built nothing there. Read that
  way, `test --no-build` named a compiler its binaries were not built with. So the record must name
  the compiler the answer names, where the answer names one, and must have been written no later
  than the answer, which a configure writes after every record it writes; a record nothing ties to
  the answer identifies nothing, and the language is `unwitnessed`, with why. The answer keeps a
  toolchain file's value as written, where the record holds what CMake's
  `Modules/CMakeDetermineCompiler.cmake` made of it, so the two are compared as that: a list's
  first item - `gcc.exe;-m64` names `gcc.exe` - a path tidied of `.`, `..` and doubled separators,
  with forward slashes, and a name alone found as a program, with `.com` or `.exe` after it on
  Windows. Each tie leaves a case to the other: the time alone tells a later record apart where the
  answer names no compiler, as under Visual Studio, where it names one by its name alone, which a
  program of that name elsewhere answers to, and where the same file was replaced in place; the
  compiler alone does where a clock stepped back since the answer was written. Measured with CMake
  3.29 and 4.3: MSVC through Ninja and through Visual Studio 18 2026, gcc on Windows and on Linux,
  toolchain files naming the compiler each of those ways, and a configure that failed after
  identifying the compiler again. A language
  CMake identified nowhere, such as the resource compiler it lists on Windows, is left out of the
  compilers, and a toolchain declaring one is told what CMake's answer named for it and why that
  is no id, never that CMake named none. `test --no-build` names what its build
  directory was last configured with, and holds it to the same `compilerId`: binaries a compiler
  nobody chose produced are failed rather than tested. A runner that does not build names none.
- **A compiler updated in place starts its build directory from clean.** CMake identifies a cached
  compiler once, when a build directory is first configured, and loads that record on every
  configure after; a build system has no edge on the compiler itself. A Visual Studio update
  rewrote `cl.exe`, `c1xx.dll` and `c2.dll` in the same toolset directory, taking `cl` from
  19.51.36257 to 19.51.36260, and a consumer's trees configured before it still recorded the old
  version: the first build of each failed every precompiled header with C1853, "from a different
  version of the compiler". So before each build of a CMake project, the C and C++ compilers the
  directory's records name - under the version of CMake that last answered there - are asked their
  versions as CMake identified them: a line naming the macros CMake's identification reads is
  preprocessed, in the leg's own environment, and the values put together by CMake's formula for the
  id it recorded - `_MSC_VER`, `_MSC_FULL_VER` and `_MSC_BUILD` for MSVC, `__GNUC__` with its minor and
  patch level for GNU, `__clang_major__` and its fellows for Clang, with `__apple_build_version__` for
  AppleClang. Never by which are defined: clang defines `__GNUC__` too, as 4.2.1. A version that
  differs, a compiler that is not there, or one that defines none of its id's macros starts the
  directory from clean, naming both versions. Preprocessing takes a fraction of a second, where a
  scratch configure took 19 with MSVC; measured against CMake 4.3's records, MSVC 19.51.36260.0
  through Visual Studio 18, MinGW gcc 13.2.0, Linux gcc 13.3.0 and clang 18.1.3 each came out as CMake
  wrote it. A compiler that cannot be asked is said and passed over - the question names the cause of
  a failure the build would show anyway - and one of another id is not asked.
- **A toolchain may name a developer environment**, declared once under `developerEnvironments`,
  and one naming none that is declared is refused, as is a `visualStudio` one on a toolchain whose
  `platforms` is not `["windows"]`: a leg elsewhere would be turned away on every run for want of it.
  `init`'s `msvc` toolchain names `visualStudio`. The survey asks every host a leg might land on
  whether it can set it up - Visual Studio's installer, `vswhere`, naming the newest instance with
  `requiresComponent` - and a host without one turns the leg away as `skipped-tool-missing`, naming
  why, one that could not look as `skipped-unavailable`, and one never asked as `poisoned`, a
  defect in this tool; a copy starts nothing and asks nothing. The host that runs the leg sets it up from the
  instance its own survey found, never a second look: that instance's `vcvarsall.bat` for the leg's
  processor (`amd64`, or `amd64_arm64` to cross-compile), run once per environment, instance,
  architecture and host environment by `cmd.exe` from a batch file the harness writes, reading
  `set` before and after it in UTF-16 and keeping only what changed; the batch file names its
  variables `%%NAME%%`, so only `call`'s own pass expands them, and a path holding `%`, `^` or `&`
  is used as it is. Its exit code, an `[ERROR` line in either encoding it prints, a
  `VSCMD_ARG_TGT_ARCH` naming another processor, an instance removed since the survey and a capture
  that cannot be written each fail the leg before anything of it starts: the survey found the
  instance, and an environment that will not set up once a leg began is that leg failing, as a
  program that will not start then is. A capture directory that cannot be removed afterwards is a
  warning and fails nothing. What it set sits over the host's `env` and beneath the variant's, the
  test invocation's and the runner's own, for every process the leg starts, so `cl` builds from a
  plain shell. The programs the leg starts - which no survey can require, since that `PATH` exists
  only once set up - are looked for on it then, before anything of the leg starts: one missing
  there skips the leg as `skipped-tool-missing`, named, never a program failing halfway through a
  build. Each leg's line names it - `developer environment: visualStudio (Visual Studio
  18.0.11205.157, MSVC 14.50.35717, amd64)` - and `--json` carries it as `developerEnvironment`,
  beside the detail rather than inside it, like the compilers.
- **A leg naming a toolchain that does not exist on its own operating system is refused**, by the
  toolchain's `platforms` list. Refused when read rather than skipped when placed, because nothing
  about it needs measuring: a leg's `os` is required, and a leg only ever runs on a host whose
  operating system equals it — emulation varies the processor, never the system. Skipping instead
  would also make the run report a leg that reached no verdict, which is not a success, so a wrong
  list would turn a green run non-zero rather than telling its author which line to fix. A
  project's `defaultToolchain` is held to the same rule, against the platform its key names, or
  against the operating systems its legs declare where the key is `all`.

It is written with LF line endings and no byte order mark on every platform. The file
is tracked, and its bytes must not depend on which machine ran `init`.

## Worktrees

`create-worktree` adds a worktree under the main checkout's `worktrees.root`, which defaults to
`.harness-config/worktrees`, and `delete-worktree` removes one, everything under it, and git's
record of it.

The root is configurable because it is spent before a worktree's own name. The default costs 25
characters of the Windows path budget, and a repository whose build paths are long has no name left
that fits; a shorter root such as `.worktrees` buys those characters back. The budget is still
checked against the real path, so a shorter root never hides an overrun — it only makes one
avoidable. Every build measures the deepest path it left below its build directory against the
reserve, and warns where it went deeper. A Ninja build leaves out of that the outputs ninja says no
target of the current build produces any more - asked as a dry run of `ninja -t cleandead`, which
names them and removes nothing - since a new worktree's build directory, starting from clean, never
holds them; it notes them instead, where one is deeper than the reserve, naming the command that
removes them. A consumer's incremental builds warned every time about the object of a test renamed
away. `ninja -t query` would not have told it apart - ninja's dependency log still knows the path -
and what CMake's configure writes is no output of the manifest at all. A leftover ninja does not
name is still measured, as a target this build had no reason to rebuild, or a file something other
than a target wrote; another generator, or a ninja too old to know the tool, is measured as before. A root spelled with a `.` segment or a doubled separator inside it -
`.harness-config/./worktrees` - is refused with the one spelling to write, and so is a
`sync.exclude` or `sync.neverTransfer` entry spelled that way: each is compared as written, by
sync's lists and by `init`'s ignore rule, so the file system's reading of it would put the
worktrees where nothing withholds or ignores them, and an entry would protect nothing.

**The root is ignored whole, and never holds a placeholder.** `init` writes `/<root>` for it and
creates nothing there; `create-worktree` makes the directory the first time it needs it. It is
ignored by name, with no trailing slash, as `.harness-config/runs` is: a rule ending in `/` matches
only a directory, so a root or a runs directory kept on another disk through a link - which the
worktree commands follow - was listed by `git status`, and committed by `git add -A`, as that link.
By name, it is ignored whatever it is, and however it is asked about. The other
harness directories a person fills by hand — `sshItems`, `wslDistros`, `runner/.env`,
`runner/.secrets` — keep the opposite shape, their *contents* ignored and a `.gitkeep` tracked, so
the directory itself tells that person where the file goes. The root cannot afford that shape.
Measured: excluding only a directory's contents makes the directory's own `git check-ignore` answer
depend on a trailing slash, and it fails toward *not ignored* — `<root>` without the slash reads as
not ignored while worktrees sit inside it. A directory holding any tracked file never reads as
ignored under either spelling, so a committed placeholder turns even `<root>/` wrong. For a slot
holding an address and a key that answer costs nothing; for a root holding whole checkouts it is the
difference between a clean sync and every worktree reaching a remote host. The placeholder also
showed as untracked until committed, which is exactly the state sync's no-longer-ignored guard
refuses.

`init` leaves hand-written `.gitignore` rules alone, so a repository that already ignored one of
these paths by hand keeps its rule beside the managed one. What `init` reports is git's own answer,
never a reading of how the rules are spelled. Each managed rule carries a path it decides - the
file it names, or a name inside the directory it rules on - and `git check-ignore -v --no-index`
says which rule decides each of those paths in the tree. A rule deciding one against the block is
named as the rule git follows: it undoes the block there, whether it is a re-include after the
block, a nested `.gitignore`, or a whole-directory rule such as `.env` - which takes the
`runner/.env` directory, from which git re-includes no placeholder. The same paths are then asked of
the tree's own `.gitignore` with the block blanked out, in a scratch repository holding nothing
else; a hand-written rule that would decide one the other way, where the tree's answer is the
block's, is named as doing nothing there - but only where nothing the harness keeps in git rests
on it. Taken out of the tree's ignore files - its `.gitignore` files, its `.git/info/exclude` and
the excludes file its configuration names - in scratch repositories asked with and without it, such
a rule must turn from kept to ignored no path the block rules on, none of the harness's own files -
its configuration, each placeholder, each file git keeps in an action and a name standing for any
action's, the anchor registries git tracks - and no directory one of those is in; where it would, it
is not named. An action's own files are listed as git keeps them, because a rule can rest on how one
is named: a re-include of the actions directory after the `[Bb]in/` Visual Studio's template
ignores keeps an action's helper in its `bin`, which no name made up for an action's file shows. An allowlist that excludes
`/.harness-config/*`, or everything, re-includes each slot the block keeps a placeholder in - by the
slot's name, or by `!*/` - and git never looks inside an excluded directory for the placeholder:
that re-include is overruled for the slot's contents and needed for the placeholder, and the note
once told a consumer to delete it, which loses the placeholder the block itself re-includes. A rule
keeping an action's files, or the configuration, counts the same way, whatever the block rules on
beside them, and so does one needed only because `.git/info/exclude` excludes what it re-includes.
Only what taking a rule out removes from git counts: a file `*` hid, which taking it out would put
in git, rests on nothing. A re-include of a directory nothing excludes changes nothing, and is
named. No path the check asks about is made a directory in a scratch repository unless git takes
it for one in the tree too - a directory above a probe - so a rule ending in `/` matches there only
what it matches in the tree. A rule agreeing with the block is not named at all: it
changes nothing, and a broad rule covering a managed path is not a copy of the block's rule. The
spelling comparison this replaced named rules that match nothing as overriding the block, and
could not see a rule reaching a managed path through a wildcard.

git's own answer has two blind spots, both measured. It never names a re-include that matched a
directory above the path - the path is then decided by no rule at all - and it matches a rule
ending in `/` against a path only where that path is a directory that exists. So a rule re-including
each host's directory, `!/.harness-config/sshItems/*/`, left a file there to the block while
putting every `.key` in reach of `git add`, and init said nothing; and `!/.harness-config/runs/`
was reported as no rule at all, blamed on another `.gitignore`. Each slot is therefore asked about a
file inside one of its directories too, and where the tree's answer is no rule for a path the block
ignores, the question goes to a scratch repository holding the tree's `.gitignore`, and each
`.gitignore` of its own along the path, with every directory above the path made: there git names
the rule re-including the nearest of them, at the line the tree's file has it on. Those files outrank
`.git/info/exclude` and every excludes file a configuration names, so a re-include undoing the block
is in one of them; were none named even there, init says git does not ignore the path and names no
rule, never where one might be. A path git will not answer about - one beyond a symbolic link, which
git never looks past, ends a whole `check-ignore` with exit 128 - is asked again on its own, named
with git's reason, and every other path is still answered. A scratch repository that cannot be
written is a note that git could not be asked; one that cannot be removed afterwards is a warning,
and the answer stands.

`init` writes the tree it runs in, a worktree's own included: its configuration, its `.gitignore`
and the placeholders that keep each directory in git. A worktree adopting the harness adopts it on
its own branch; written into the main checkout, the worktree's `.gitignore` never changed and main's
did. A worktree with no configuration of its own gets a copy of the main checkout's, which it was running
with and warned about on every command; a default in its place would drop every leg, host and
runner main declares. What git ignores - connection data, runner values and secrets, the lock - is
read from the main checkout whichever tree asks, and `init` in a worktree says so rather than
creating any of it there.

The main checkout is the one git's `worktree list` names first, which it names for the git
directory its worktrees share: that directory's parent, where it is called `.git`, and the directory
itself otherwise - no checkout at all. A submodule's is named so, kept under its superproject's
`.git/modules`, and `init` in a submodule once took it for a worktree of that directory, where its
connection data, runs and worktrees would have been looked for. Where git names a git directory, the
main checkout is the one that directory's configuration records, as a submodule's `core.worktree`
does; one made with `--separate-git-dir` records none, and is then the tree asked from, where git
lists that as no linked worktree. From a linked worktree of such a checkout, nothing says which
checkout is the main one, and the command is refused, saying how to record it.

`create-worktree` records the commit a worktree was made from, under
`refs/harness/worktree-base/<name>`, and `list-worktree` reports it. A worktree's own HEAD moves
with every commit made in it, so after the first one nothing else says what tree the worktree
started from, and it can only be reproduced from the moment it happened to be made. The record is
kept under `refs/harness/` rather than among heads, tags or remotes precisely so it can never be
mistaken for somewhere work is kept: the deletion checks below read branches, tags,
remote-tracking refs, the newest stash and other worktrees' HEADs, and this record is none of them.
It is removed when the worktree is, so it can never answer for a later worktree of the same name.

Every git command the harness runs first clears `GIT_DIR`, `GIT_WORK_TREE` and `GIT_INDEX_FILE`
from the child's environment. Each of the three silently outranks `-C <directory>`, and a git hook
runs with all three set, so a harness command invoked from a hook — or from a shell someone left in
another checkout — would otherwise read and write a repository nobody named. A caller that
deliberately wants a different index still gets one: the inherited value is cleared first and the
requested one set after.

### What deleting one refuses

Without `--force`, every check runs before anything is touched. A refusal deletes nothing,
names everything it found on one line, each with its remedy, and exits 13:

- **Uncommitted changes.** A modified, staged or untracked file git does not ignore, or a
  changed submodule. Status runs with `--untracked-files=normal` and
  `--ignore-submodules=none`, so configuration can hide neither. Status never compares a file
  marked assume-unchanged or skip-worktree, so those present on disk are compared on a copy of
  the index with the marks cleared, which applies git's own line-ending rules and never touches
  the real index. A skip-worktree file absent from disk, as a sparse checkout leaves it, is not
  a change.
- **Commits nothing else names.** A detached worktree's HEAD can be the only name for the
  commits made there. They are counted when no branch, tag, remote-tracking ref, the newest
  stash, or the HEAD of another worktree reaches them, so a worktree on a branch, or whose
  commits were pushed, is not refused. A commit held only by an older stash entry is refused.
- **Submodule work.** A linked worktree keeps its submodules' repositories in its own git
  directory, so their branches, tags and stash are deleted with it, whether the submodule is
  checked out or was deinitialised. Every repository there is checked, nested ones included,
  and so is a checked-out submodule whose `.git` is a directory of its own. One stops the
  deletion when commits on its HEAD or branches are on no remote-tracking ref or tag, or when it
  holds a stash. Tags count as kept, since they usually come from upstream, so commits held
  only by a tag made inside the submodule are deleted with it, without a refusal. A directory
  there that looks like a repository git cannot read, such as one whose `HEAD` a crash emptied,
  stops the deletion with exit 20.
- **A lock**, reported with its reason and `git worktree unlock`.
- **A worktree moved by hand.** git removes, and lists, the directory its record names. When
  that is not this directory, the refusal points to
  `git -C <main checkout> worktree repair <path>`. This worktree's HEAD is told apart from other
  worktrees' by its record rather than its path, so a moved worktree's commits still count.
- **Not a worktree of this repository.** The directory must be the root of a work tree whose
  git directory sits in the `worktrees` directory of the main checkout's git directory. In a
  directory whose `.git` file is gone, git answers for the main checkout around it, whose
  status reports none of the directory's files. The refusal points to
  `git -C <main checkout> worktree repair`, which helps only while git still has the worktree's
  record: when it was moved or its `.git` file was lost. A clone at the path would pass a status
  check and take its whole history with it.

- **Evidence.** The directories `worktrees.evidenceRoots` declares are checked before git is asked
  anything, because they hold files git was never told about. One of them holding anything refuses
  the deletion and names it. A worktree's measurements live in an ignored directory precisely because
  they are not source, and deleting them is silent: git reports nothing missing afterwards.
  `--delete-evidence` proceeds while every other check still runs; `--force` proceeds too, and
  skips everything else as well. A declared root that cannot be read counts as holding something,
  because an unreadable directory is not an empty one. A root resolving outside the worktree is
  ignored rather than refused, since the deletion was never going to touch it.

Without `--force`, when the check cannot be finished, because git cannot answer or a record
cannot be read or its directory found, nothing is deleted and the command exits 20, with the
reason first. Which worktree a directory is, and whether its record is gone, is decided
from paths git reports. A path the harness spelled is compared with one only after every link
along it is followed, so a linked `.harness-config` or worktrees directory changes nothing.

Ignored files outside a declared evidence root are deleted without a check, including ones no
build makes again, such as `.env`, and so are ignored directories with everything in them, the
history of a repository nested inside one included.

### Removal

- Checked, removal is plain `git worktree remove`. git's own check still catches a file changed
  since ours, a file added unless `status.showUntrackedFiles` is `no`, or a lock; a worktree
  holding submodules is removed with `--force`, which git requires for one, and that skips even
  this check. When git fails part way, as on a file another program holds open, its record and
  some files may already be gone: the command deletes nothing more, exits 20 and says what is
  left, and `delete-worktree <name> --force` finishes it, which is safe because every check
  passed before removal began.
- Forced, it is `git worktree remove --force --force`, which overrides a lock. A directory git
  leaves behind is deleted, and git is then asked again to clear its record, which it can once
  the directory is gone. A file that cannot be deleted is reported with exit 20 and what to do
  next.
- The record is confirmed gone by its administrative directory, found before removal, or, where
  git could not name that directory, by git's list. A record whose directory is already gone,
  as an interrupted delete or a directory removed by hand leaves it, is cleared once its lock,
  unreferenced commits and submodule repositories are checked, so the name can be used again.

An interruption stops the command cleanly only before its first destructive step. After that
the deletion goes on, the command says at once that it is under way, and it waits up to two
minutes for the deletion to finish, as does a host agent running it for another machine. Just
before the two minutes are up, git is stopped and the command says what is left. The deletion
can be left partly done on any platform, and on Linux and macOS the interruption reaches git
itself; running `delete-worktree <name> --force` then finishes it.

## Anchor registries

An anchor is a named piece of deferred work, kept as one row of a markdown registry. Two
registries hold every anchor, and each anchor lives in exactly one of them:
`anchors.pendingAnchorsPath` holds the live anchors, and `anchors.doneAnchorsPath` is the
archive of closed ones. `init` creates each registry that is missing from a skeleton
embedded in the tool, and never touches one that exists.

### One table, six cells, one verdict

A registry is an introduction and exactly one anchor table, recognised by its header row,
`| Anchor | Priority | Status | Trigger | Closing work | Cross-refs |`. A file with no
anchor table or a second one, or with an anchor row outside the table or inside some other
table, is malformed: where a new row belongs would be a guess, and a stray row is counted by
nothing. Every command that reads or changes anchors refuses a malformed registry (exit 20)
rather than answer from rows it cannot trust, while `read-anchors --lint` and
`check-anchor-balance` report the problem among their findings (exit 1).

The Status cell is the only verdict that decides: `🟠 OPEN`, `⏳ GATED`, `🔵 DISCLOSED` or `✅ CLOSED`. A
row is closed exactly when its Status cell starts with ✅, and every other glyph, including
one nobody anticipated, reads as open: a row wrongly read as open stays visible as work,
while a row wrongly read as closed disappears from every count. Nothing is inferred from
the prose cells. A registry whose rows open a closed Trigger with the closure itself can say
so with `anchors.triggerCarriesVerdict`: a closed row's Trigger then opens with ✅ and no
other row's does, the writing commands refuse a row whose two cells disagree, and the lint
reports one, so that a row states its verdict once, even where it states it twice.

### Writing a row

Commands take fields, never rows. Line breaks collapse and pipes are escaped, because a raw
`|` adds a column and shifts every later cell, and a wrapped row hides its id from every
search. Only the breaks go - each, with the whitespace either side of it, as one space, at
every boundary a reader of the file might split a line at - and so does whitespace at the
value's very start and end; every other character is kept as given: a run of spaces or a tab
inside a line is often a cell's evidence, quoted tool output or aligned figures. A value that already holds an escaped pipe is refused: escaping it
again would double the backslash, and it usually means someone copied a raw table line. A
cell given in a file is read as UTF-8, and a file that is not, or that opens with a byte-order
mark, is refused by name rather than cleaned. Every composed row is read back through the same
parser before anything is written.

`set-anchor` rebuilds only the cells it was given and writes every other cell back byte for
byte; a row whose cell count is wrong is refused rather than guessed at. A new id must match
the minting rule (`D-` and at least three segments by default), while an id already in a
registry only has to be well formed and is never rewritten.

### Where a row goes

The destination is derived from the status, never named by a caller: a closed anchor goes to
the done registry and every other status to the pending one, so a live anchor can never be
filed where nothing reads it as work. New rows are appended at the end of the table; tables
are never sorted.

A move is two file writes and cannot be atomic. The destination is written first and the
source second, so an interruption leaves the row in both registries, where the next change
refuses the duplicate loudly, and never in neither, which every count would read as a
closure. Each file is replaced whole through a rename, so a reader never sees a torn file.

### Which copy, and who may write

A registry git tracks is read and changed in the tree the command runs in, so the change
travels with that branch. A registry git ignores has no copy in a worktree, and is resolved
in the main checkout, where ignored harness state always lives. `init` resolves registries
the same way, so one it creates from inside a worktree is where every later command looks.

Every change holds a machine-wide named mutex, keyed by the two registry paths, for the whole
of its read, decide and write. .NET supports named mutexes on Windows, Linux and macOS alike,
and named semaphores on Windows only. A mutex must be released by the thread that took it, so
the locked work is synchronous by construction. A change that cannot take the lock within 10
seconds writes nothing and exits 13. Reads take no lock.

### The balance

`check-anchor-balance` compares the working tree with a base commit (`--base`, default
`HEAD`). Each registry is read at the base through `git ls-tree` and `git show`: whether the
file exists there is asked of a command whose exit code does not depend on the answer, so a
commit git cannot read is never mistaken for a file that did not exist yet. A registry absent
at the base counts as empty there, and the receipt says so. A registry git ignores has no
history and is refused.

Anchors are compared by id across both registries, so moving a row counts as nothing. The
check fails when open anchors rose, less the anchors newly disclosed (disclosure records debt
that already existed), and whenever the registries as they stand are unsound: a closed anchor
in pending, a live anchor in done, a missing registry, or a structural problem. Every problem
is reported at once.

### Citations

`check-anchor-citations` requires every anchor id cited in a **scanned root** to resolve to a
row in either registry. The roots are `anchors.citationRoots`, and nothing outside a declared
root is scanned: which code is production code is a judgement a repository makes, not one a
tool can infer. An empty list scans nothing and the command says so, rather than reporting a
pass over a check that looked at no file. `--current-commit` reads HEAD, `--current-tree` reads
the disk, `--current-pr` reads only what this branch changed.

A citation resolves to a row whose id is exactly the id cited, by the rule `read-anchor` finds
a row by, so the two verbs never disagree about whether a row exists. Resolved by containment
instead, a citation of `D-FF3-3` passed through a row `D-FF3-30-…`, and a wrapped fragment
`D-PP-PRESCAN-` passed through the id it was cut from: a truncated or ambiguous citation was
invisible to the gate. A citation that runs into a hyphen at the end of its line is reported as
cut there whatever rows exist - even one named by the part before the cut - because it does not
spell the id it was cut from. An id cut at its first or second hyphen is too short to be a
citation on its own line, so it is one where the next line carries on with the segments that make
it one: `D-PP-` before a line opening `PRESCAN` is reported cut, and `D-` before `day` - a wrapped
D-day - is not. An id cut just before a hyphen is cut too: one that ends its line where the next
opens - past its indentation and a comment's marker - with the hyphen and the segments that carry
it on into a row's id, as `D-LK6-14` before `-INTEGRATION-PAYLOAD` where
`D-LK6-14-INTEGRATION-PAYLOAD` is a row. Read on its own it resolved to the shorter row it happens
to spell, or to none. Where the two lines joined spell no row, the hyphen opens something else - an
option such as `-Wall`, a figure such as `(-40`, a line a diff removed - and read as a cut each
failed the check over an id written whole; a list's `- ` and an option's `--` carry nothing on
either way. What stays out of reach is a break inside a segment, with no hyphen on either side: it cannot be told from a line
that simply ends there, so it reads as the shorter id, reported unresolved unless that shorter id
is a row of its own. The failure says cut citations apart from those no row resolves, since adding
a row answers only the second.

`--current-commit` reads every file of the commit through one git process; asked for one at a
time, each cost two, and 2,385 files took twenty minutes. A file is read as it would be from
disk - its byte order mark, UTF-16 included, says how. git answers "missing" for a file whose
object it cannot read exactly as for a path that names none, so a path it answers that way is
looked for in the commit's listing: one listed there refuses the read, naming it, rather than
passing with nothing read in it. `check-anchor-balance` reads its base through the same rule, so
a registry git cannot read is refused rather than reported missing at the base.

git holds a name as bytes, and nothing makes them UTF-8. A file in a root whose name is not
UTF-8 refuses the check, named as git's quoting writes it (`caf\351.md`): no file opens by such a
name here, and read as UTF-8 it became another name, which the disk silently did not have. Which
root a name lies in is decided on the name as .NET reads it, a stray byte as U+FFFD, and never on
the quoted form, whose backslash reads as a separator and moved `src\351.bak` into `src`. A file
git lists once for each side of a conflict is scanned once; two names that only read alike are
two files.

The scanner is the point of the command. The guard it replaces required a word boundary before
an id, which is right for `FIXED-32-BIT-WORD` — whose tail is anchor-shaped and is correctly
skipped — and wrong for an id written straight after an escape, as in the C++ literal
`<< "\nD-SOME-ID: …"`: the `n` of `\n` is a letter, the boundary fails, and the whole citation
is not reported missing but simply never seen. A wrapped id does not fail, it disappears. So a
preceding word character blocks a match unless it is the tail of a recognised escape sequence,
and a doubled backslash counts as a literal backslash, which is not one.

## Hosts, trees and legs

Three concepts that are frequently conflated, kept separate here:

| Concept | Question it answers | Values |
|---|---|---|
| **Host** | Where does this run, and how is it reached? | this machine, a WSL distribution, an ssh host |
| **Tree** | Which checkout am I acting on? | main checkout, a worktree, a host's copy |
| **Leg** | One unit of work with a verdict | `(os, processor, emulator?, project, toolchain, config, sanitizer?)` |

A worktree is **not** a host. It is a tree, and it composes with any host. Keeping
these apart is what lets one code path serve `build`, `build --worktree wt-a` and
`build --ssh vps` without special cases.

### Where a leg runs

A leg names what it needs, never where it runs: an operating system (`windows`, `linux`,
`macos`), a processor (`x86_64`, `arm64` and the other processors .NET reports), and
optionally the emulator it runs through on a host with another processor, which rules out
running it natively. A misspelt value is refused when the file is read, since it could
never match a host.

Where it runs is measured before anything starts, never declared:

- A leg's candidates are this machine, then the WSL distributions under `hosts.wsl` when the
  leg runs on Linux, since a distribution runs nothing else, then the ssh hosts under
  `hosts.ssh`, each in the order the configuration declares them and named as it declares
  them. A leg that sets `wsl` or `ssh` has that one host as its only candidate.
- This machine is measured first, because it costs nothing to reach. Other hosts are
  measured only for the legs it cannot take, and all at once, so an unreachable host costs
  its connect timeout once.
- A leg runs on the first candidate whose measured operating system matches and whose
  processor matches, or, for an emulated leg, where that emulator's check passed. A program is
  never part of the choice: a host that lacks one the command requires there turns the leg away
  there, rather than the leg moving to a host that has it and measuring a machine nobody chose. Every
  command places a leg the same way, so a run with `--use-staged` finds the tree where a sync
  put it. To run a leg elsewhere, the leg names that host.
- `--legs` takes leg names and leg set names, separated by commas or spaces. A name that is
  neither is a usage error before any host is measured. No `--legs` selects every leg, and
  `--legs` given without a name is a usage error rather than every leg, so an empty
  variable cannot pass a gate. A leg or leg set name holding a comma or a space could never
  be selected, so the file refuses one.
- A leg that cannot run is a warning naming the leg and why - each candidate's reason when none
  could take it, or what the host it went to lacks - and the other legs still go ahead. The
  check fails, and `legs` exits 1, when a leg named with `--legs` cannot run, or when no
  selected leg can: a declared leg on a machine that is switched off is normal, and a leg asked
  for by name is not. It exits 70, named or not, when whether a leg can run was never
  established - a host never asked about a program the leg starts, which is a defect in this
  tool.

A native run and an emulated run are different legs. Neither their timings nor their
failures compare.

### Emulators

An emulator runs programs built for another processor without leaving the host's
operating system: qemu's user mode on Linux, Rosetta on macOS, Prism on Windows. It
declares the hosts it runs on, the processor it runs programs for, the launcher placed in
front of each program (none where the operating system runs such programs itself), what it
requires, the phases it runs (tests by default, since the usual way to build for another
processor is a native cross-build), and a witness.

The witness is a program run through the emulator whose output must match a pattern, such
as `uname -m` from an arm64 userland printing `aarch64`. Without it, an emulator that ran
nothing, or a program the host quietly ran natively, would count as coverage of a processor
that was never exercised.

A whole virtual machine is not an emulator in this sense. It runs an operating system of
its own, and is declared as the ssh host it is. A virtual machine with a different processor
runs DssHarness under full emulation, where .NET is not supported, so DssHarness itself
is not dependable on such a host.

### Reaching a host

- **WSL.** A distribution is available when this machine runs Windows, `wsl.exe` exists,
  and a program starts in the distribution. Programs start with
  `wsl.exe --distribution <name> --cd ~ --exec`, so no shell in the distribution parses
  the arguments, and with `WSL_UTF8=1`, because wsl.exe otherwise writes its own messages in
  UTF-16. Its errors are recognised by the code they carry, such as
  `WSL_E_DISTRO_NOT_FOUND`, never by their sentence, which is translated. `--wsl` with no
  name is WSL's default distribution, as the distribution itself reports it.
- **ssh.** A host's connection data lives in its own directory under
  `.harness-config/sshItems/<name>/`, which git ignores: an `.env` naming the address, the
  user and the port, a `.key`, and a `known_hosts`. `config.json` declares only the directory
  names, under `sshItems`, and `hosts.ssh` is keyed by them. Nothing tracked names an address,
  a user, a key path or a credential, so a `config.json` that arrives through git cannot point
  the harness at a machine nobody set up here, and a public repository carries no connection
  data at all. There is no wildcard and no pattern matching: a directory either has the name or
  it does not, which is what a shared ssh config file made subtle. ssh is invoked with that
  item's key, port and known-hosts file, in batch mode, so an untrusted host key or a password
  prompt fails with ssh's own reason instead of waiting, and `connectTimeoutSeconds` and
  `keepAliveSeconds` bound a dead link. An `.env` other users can change is refused, because
  whoever can change it can send the harness somewhere else; a key other users can read, ssh
  ignores, so it is refused too. Both are checked before connecting, and reported with the
  command that fixes them. Per-host files also confine that risk: one world-writable shared
  configuration file threatened every host at once.
- **Names that resolve.** A host reached by an mDNS `.local` name on a DHCP network fails a
  lookup as a matter of course, and a name each ssh call looks up afresh is one each call can fail
  to find. So ssh is asked first what it would do (`ssh -G`), and the name it would look up - the
  address declared, or a HostName its own configuration gives it - is looked up here, retried,
  with the answer cached briefly, so one failed lookup never fails a leg. Every ssh call is then
  given the address as its `HostName`, while the pin holds, with the host's key looked up under
  the name through `HostKeyAlias` - its configuration's own alias where it sets one, and
  otherwise `[name]:port` off port 22, as known_hosts spells such a host - and `CheckHostIP` off,
  so the address itself is neither checked against known_hosts nor written into it. The
  destination stays the address declared, so a Host block written for it still applies. A host ssh
  reaches through a `ProxyJump` or a `ProxyCommand` is neither looked up here nor pinned, since the
  jump host or the command does its own lookup. One whose `ssh -G`, asked again pinned, shows
  anything but the address changed, as a `Match` block keyed by the host would, is looked up here
  but not pinned. A pinned call that fails before any session - the address takes no connection,
  or shows a key the name is not known by - drops the pin for the rest of the connection and runs
  again, with ssh looking the name up itself.
- **Hosts that sleep.** A personal Mac reached by its mDNS name falls back asleep between commands
  and answers again moments later; three quick lookups miss it, and a consumer saw a run skip it
  seconds after a check had reached it, three times in fifteen minutes. A host given
  `wakeWaitSeconds` is looked up again, and a connection nothing took or that timed out is tried
  again, every few seconds until the window ends, before its legs are skipped. What a host that is
  awake says - a key it shows that the name is not known by, a login refused - is never tried again.
  A host reached after waiting says how long it took, among what measuring it did; one whose window
  ran out is refused naming the window, and, for the half minute a name's answer is kept, refused
  at once to the rest of the command rather than waited for by every leg placed there. Left at 0,
  the default, nothing changes.
- **PATH truth.** A login shell's PATH is not what a command sees: `/opt/homebrew/bin` is absent
  from an ssh command's PATH on macOS, and `~/.dotnet` is in WSL. Programs the harness depends on
  are resolved to an absolute path once per connection, measured rather than assumed, the same
  way the remote shell is. A host whose SDK is installed but off that PATH is reported as exactly
  that, never as "not installed": the remedy differs. A leg's own programs are found by the
  DssHarness on the host that runs it, with the function the leg's run uses: on that PATH, then
  in the searched directories (`toolSearchDirectories`, or a built-in list). The directory each
  was found in is appended to the PATH of every process the leg starts, so the run finds what
  the survey found even where a phase's environment sets a PATH of its own - which is why a
  program started under such an environment is looked for too, though it never turns a leg away.
  A directory the search could not look in leaves a program unknown, never missing.
- **No quoting.** An ssh server hands its command line to a shell, and which shell is not
  known in advance: sh, bash, zsh, fish, cmd or PowerShell. The harness quotes for none of
  them. The command line holds only words every one of them reads literally (letters,
  digits and `._-/=:+`), and anything else travels on standard input. Which shell answers is
  measured once per connection, by whether `echo %COMSPEC%` comes back expanded, because cmd
  needs backslashes in the path of a program.

### DssHarness on every host

Every WSL distribution and ssh host runs DssHarness itself, installed as a global .NET tool
from nuget.org, so it needs the .NET 10 SDK, with `dotnet` on the PATH of a command run
without a login shell. DssHarness itself is started from `~/.dotnet/tools`, where global
tools are installed, since that directory is usually on no such PATH. Every install and
update names nuget.org as its only source, so no feed configured on the host can supply a
different package under the same name. Only stable versions are published there: a beta is
released on GitHub alone, so a machine running one cannot bring a host to its build, and
is told so. The machine that reaches it asks it questions
through a hidden `host-agent` command, with the request as one line of JSON on standard
input, which it holds open until the host has finished: which build
it is, what the host is, and whether each emulator works there; or to run one of its own
commands in the host's copy of the repository, which is what `host-exec` does.

Both ends must be the same build, so before anything runs on a host:

- A host without DssHarness has this machine's version installed.
- A host that is behind is updated to this machine's version. It is never downgraded, and
  not updated while DssHarness is running there: an update replaces a running tool's
  files underneath it on Linux and macOS, and fails part way on Windows.
- A host that is ahead stops everything (exit 13) until this machine is updated, with the
  command that updates it. Moving the host down would undo somebody else's update.
- The version and the SHA-256 of the tool's assembly are both compared. A build from source
  reports the same version as the published package, while the assembly installed from one
  package is the same bytes on every operating system.

`host-exec` returns the exit code of the command it ran on the host, unchanged, and 15 when
nothing could run there: the host is unreachable, has no SDK, could not be brought to this
build, or has no copy of the repository.

- The exit code is the one the host reports in its last line, which carries a value only
  that request knows, never the transport's. ssh exits 255, and wsl.exe with codes of its
  own, when a connection fails, so a line that never arrives is 15 too: the command may not
  have run, or run only in part.
- Interrupting `host-exec` stops ssh or wsl.exe, which ends the host's input, and the host
  cancels the command instead of leaving it running there.
- A host's copy is created by `sync`, which also puts `.harness-config/config.json` there so
  the DssHarness running there can find the repository at all. Sync will not take over a checkout
  made by hand unless `--adopt` names that host, and reports what taking it over would cost
  either way.

### Installing what a host is missing

`install-missing-tools` runs over every declared leg, or those `--legs` names, on the host each
leg names with `wsl` or `ssh` and on this machine otherwise. Its logic lives in the core, so
other commands share it, and `init --install-tools` calls it. Plain `init` installs nothing and
says how: adopting the harness's files changes one tree, and an install changes machines.

- **`--dry-run` installs nothing.** It reaches and asks every host exactly as a run does, and
  reports each tool it would install or update as `would install` or `would update`, with the
  command that would run, `sudo` and all. Nobody is asked for a password, and no host is asked
  whether one is needed: a dry run that stopped for a password would be the install it says it
  is not. It exits `1` while anything is missing, and `--json` carries `dryRun`.

- **The .NET SDK is the default tool on every remote leg.** A WSL distribution or ssh host that
  cannot run DssHarness has it installed, under the home directory, where no login-free PATH
  names it — which is why every command the harness runs there spells the resolved absolute path.
  The machine running the harness is assumed to have it already.
- **Everything under `tools` that carries an `install` is probed and installed or updated
  through it**, by `probe.args`, `probe.regex` and `minVersion`. An entry with no `install` is an
  allowlist entry: probed where it declares a probe, reported when missing, never installed. That
  is how a program shipping with the platform, or with the repository, is allowed to appear in a
  runner's steps.
- **A tool may name the platforms it is needed on**, with `platforms`, in the same words a
  toolchain uses: `windows`, `linux`, `macos`, or `all`. A host whose platform an entry does not
  name is never asked about it, so it is neither probed there nor counted against that host's legs.
  Left out, a tool is needed everywhere, which is what every list written before this meant.
  Without it a repository could not declare both a Windows compiler and a POSIX one: each was
  reported missing on the other's hosts, and no leg was ever fully provisioned.
- **A tool may narrow that to the legs that need it**, and every scope it names must hold:
  `toolchains` (legs whose variant builds with one), `legs` (legs or leg sets, by name),
  `processors` and `emulators`. `processors` is the leg's processor, the one it is built for, which
  under an emulator is not the host's - so it reaches a native arm64 host as surely as an emulated
  leg, and what only the emulating host needs, such as the emulator itself, is scoped with
  `emulators` instead. Two legs on one host share what is installed there, and each is told only
  about the tools it needs: `cl` scoped to Windows alone was reported missing on a MinGW leg,
  because that leg is Windows too. A host is asked about a tool once, whichever of its legs need it,
  and not at all when none of them do. A scope naming nothing declared, or one covering no declared
  leg, is refused when the configuration is read.
- **Each leg is told about a tool as it will find it.** A leg whose toolchain names a developer
  environment starts every process in it, so a tool it needs is looked for on the `PATH` that
  environment sets up for its processor, as the run looks before the leg starts: set up on this
  machine for the look, searched, then each directory searched for programs. Found there it is
  started by its path for its version, and `cl` - which no plain shell has - is never reported
  missing on the leg that builds with it. Every other leg looks on its host's own `PATH`, which is
  looked at once and shared, so two legs on one host can be told different things about one tool:
  CMake that only Visual Studio carries is there for the one and missing for the other, and a
  Visual Studio copy older than `minVersion` that comes first on its `PATH` is outdated for the leg
  that starts it, whatever the host's own `PATH` holds.
  - **An install runs once on a host**, for whichever of its `PATH`s asked first - the host's own
    comes first - and each then looks again, the environment set up afresh, since the install may
    have added to what it sets up. A failed install is the answer every leg there that needed it is
    given.
  - **An environment with no instance leaves the host's own `PATH` all there is**, so a tool
    missing there is missing, and an install that brings Visual Studio is what would help. One that
    could not be looked at or set up leaves a tool the host's own `PATH` lacks unknown, saying why.
  - **Another host's is set up by the DssHarness that runs its legs there**, and this command
    reaches that host through its shell alone: a tool its own `PATH` lacks is unknown for a leg in a
    developer environment, never missing, and nothing is installed for it - a second copy of a tool
    the leg finds is exactly what this command must never install.
- **A privileged install takes its credential from that host's own item, on standard input
  only.** The item declares it as `SUDO_PASSWORD` in its `.env`, beside the address and user:
  `.harness-config/sshItems/<name>/.env`, or `.harness-config/wslDistros/<distro>/.env`. It never
  reaches an argument list, a log or an error message, and redaction happens at one place rather
  than at each call site: a failure excerpt was measured carrying one through. Only the six system
  package managers ask for one at all — `apt`, `apt-get`, `dnf`, `yum`, `apk`, `zypper` and
  `pacman` install into system directories; `brew`, `winget`, `choco`, `scoop`, `npm`, `pip` and
  `dotnet` never run under `sudo`.
- **A host that declares no password is asked for one, once, at the terminal.** Where nothing
  else can supply it, `install-missing-tools` and `init` ask whoever is running them, echoing
  nothing. The answer is checked against that host with `sudo -S -v` before an install that may
  run for half an hour rides on it, and a wrong one is refused on the spot rather than tried
  again: a second guess is a second failed authentication counted against that account.
  - **It is held per host, in memory, for that one command and no longer.** Each host owns its
    own answer, so a password typed for one is never offered to another — which would spend
    somebody's failed-login budget on a machine they never meant to touch. Nothing is written
    anywhere: the next command asks again.
  - **This machine can be asked too**, which is the one case an item could never cover: local
    legs have no item and so can declare no credential at all.
- **Nobody is asked where nobody is there.** A run whose standard input is not a terminal, and a
  run answering with `--json`, both refuse exactly as a run with no credential always has, so
  what a script reads keeps parsing. `--no-prompt` refuses the same way at a real terminal, and
  says so in the refusal. The refusal names every remedy, including running the harness as root,
  which needs no password: CI normally installs its dependencies in an earlier step and never
  reaches this at all, and where it does, running as root is the answer that suits it.
- **Interrupting the prompt stops the run.** Under `install-missing-tools` that is exit `130`,
  as any interruption is. Under `init` it is also `130`, and everything already created is still
  listed: the repository is initialised either way, and only the last step was stopped.
- A second run reports "already current" and changes nothing. An unreachable host is named, and
  the other legs still go ahead.

### What legs and host-exec run

`legs` and `host-exec` run what the configuration declares, without asking first: `legs`
runs the witness of each emulator the selected legs use, on the hosts it measures, and both
run DssHarness itself on WSL distributions and ssh hosts, which they install or update
there. That is the trust building the repository already asks for, since a build runs the
repository's own code.

- An ssh host is reached only when the main checkout holds its directory under
  `.harness-config/sshItems/`, which git ignores, and no ssh configuration file is named with
  `-F`: ssh reads the user's and the system's own, as it does for anybody. A `config.json` that
  arrives through git cannot point the harness at a machine nobody set up here.
- A launcher and a required file are each a program name, found the way a leg's programs are -
  on the host's `PATH`, then in the searched directories - or an absolute path. A relative path would resolve against whichever directory a host
  starts programs in, and would let a file shipped in the repository stand in for the tool
  it is named after. A witness with no launcher is named the same way. Behind a launcher it
  is an absolute path: the launcher finds it, not the `PATH`, and qemu's user mode opens a
  bare name in the directory it starts in.

### Verdict vocabulary (closed)

Every declared leg reaches **exactly one** verdict. A leg that reaches none is a
defect in the harness itself and fails the run, rather than silently vanishing
from the report.

| Verdict | Meaning | Counts as failure |
|---|---|---|
| `passed` | Ran to completion, succeeded, and its success pattern matched | no |
| `failed` | Ran to completion and reported failure | **yes** |
| `unwitnessed` | Exited 0, but its success pattern never matched | **yes** |
| `inputs-moved` | Files the tests read changed while they ran | **yes** |
| `unmeasured` | Whether those files held still could not be established | **yes** |
| `contended` | Another process used the leg's build directory while it ran | **yes** |
| `skipped-not-selected` | Filtered out by `--legs` | no |
| `skipped-unavailable` | No host can take the leg; its host or its tree could not be reached; whether a program it starts is there could not be established; or git could not answer in its tree | warning |
| `skipped-tool-missing` | A required tool is not installed | warning |
| `refused-locked` | Another run holds the lock for this leg, or its host's tree | **yes** |
| `log-held` | Another live run owns this leg's log path | **yes** |
| `poisoned` | The harness could not produce a verdict | **yes** |

`failed` and `poisoned` are deliberately distinct: "your code is broken" and
"the harness broke" call for different responses. `inputs-moved`, `unmeasured` and
`contended` say nothing about the code at all: the first two call for letting the tree
settle and running again, the third for waiting for the other run.

`refused-locked` and `log-held` are deliberately distinct, though both mean another run got
there first. A lock is taken for the duration of the work and is released by the run that
took it; a log path is owned by a run id, and one already owning it means two runs would
write one file and each would read the other's output as its own. The remedies differ -
waiting for the lock, against finding out which run still holds a finished run's logs - and a
reader who cannot tell which fired cannot pick either.

When several apply, the more fundamental one is reported: `poisoned`, then
`unmeasured`, `inputs-moved`, `contended`, `log-held`, `refused-locked`, `failed`, and
`unwitnessed`. A leg whose inputs moved is not reported as failed even if its tests
failed, because what failed was a tree that never existed.

**A leg that reached no verdict is never counted among the legs that passed.** A skip is not a
failure — a switched-off machine is normal — but it is not a pass either, and a run carrying one
exits `21` (`Incomplete`) rather than `0`, naming the legs that did not report. The verdict table
already ranks a skip above a pass so that such a run summarises as the warning; the summary now
reads that ranking instead of reporting the number of rows in the ledger as the number that
passed. A gate comparing two runs reads exactly this line, and "8 leg(s) passed" for eight legs
that never ran is the one number that must never be wrong.

## Parallel execution

A command that selects several legs starts them together and waits for **every**
one to finish before it reports. Legs are isolated from one another (see below), so
running them one at a time is never needed for correctness, and doing so would only
make a gate slower.

**Legs are chunked by the physical machine they run on.** A local leg and every WSL leg are one
machine, because a distribution runs on the machine running the harness; each ssh host is its own.
Two ssh names that happen to reach one machine are counted as two, because nothing here can tell
that they do. `defaults.maxParallelLegs` caps how many run at once **on any one machine**, and
`defaults.maxParallelLegsTotal` caps the whole fleet on top of that, for what a fleet shares even
when its machines do not: a license server, a network share, a sync's bandwidth. A single cap
across every leg had to be set low enough for the busiest machine, which left every other host
idle. A leg that says nothing about where it runs is counted as sharing one machine with every
other such leg — both answers are guesses, and that one only ever runs fewer at a time than the
truth would allow, while the other would remove the cap silently. Left unset, every selected leg
starts immediately.

**A parallel run is reported live, and every line says whose it is.** The run announces what it is
starting and across how many machines; each leg announces the steps it is about to run, then each
step as it starts and finishes; and a child's own output under `--verbose` is tagged
`<leg>/<phase>:`. Output is written a whole line at a time under one lock, so lines from legs
running at once never interleave within a line. The log files keep each line as the child wrote it:
the tag is for the terminal, so nothing that reads a log has to know about it.

Within a leg the order is fixed:

```
sync (when the host needs it)  →  build on buildCores  →  test on testCores
```

- **A tree is synced once, not once per leg.** Legs on the same host share one
  tree; if each synced it, their copies would race over the same files. The legs
  sharing a tree wait for its single sync, then build and test in parallel, which is
  safe because each variant has its own build directory.
- **Cores are configured, never "all of them".** `defaults.buildCores` and
  `defaults.testCores` both default to 6. A host replaces them with its own
  `buildCores` and `testCores`, because a remote host rarely has the same core count
  as the machine that wrote the configuration, and a test invocation can replace the
  test count again with its own `cores`.
- **A host's `env` reaches every process a leg starts there** - each build phase, the
  ninja that reads the build's dependency records, the test runner and each step of a
  runner - as the lowest layer, so everything more specific still says otherwise. From
  lowest to highest: for a build, the host's `env` then the variant's (its toolchain's,
  build config's, sanitizer's and project's); for a test, the host's then the test invocation's; for
  a runner, the host's, then the runner's values and secrets, its own `env`, and the
  step's. Names compare ignoring case on every platform, as they do on Windows: a value reaches
  every spelling of its name the machine already has, and the one written, so `Path` written for
  a Linux host sets its `PATH`, and `http_proxy` reaches both the curl that reads it and the
  tools that read `HTTP_PROXY`.
- **A PATH a host sets is the run's.** It is where that host finds every program a leg
  starts there, and no survey can see it, so none of those programs is required of the
  host before the leg starts - each is looked for, so the directory it is found in still
  reaches that PATH, and a program that is then not found fails the leg, naming it. A leg in a
  developer environment is the exception: the PATH it sets up is built over the host's, and
  what the leg starts is looked for on it before anything of the leg starts, a program missing
  there skipping the leg as a tool missing.
- **A host running a leg another machine dispatched to it is told which host it is.** To
  itself it is `local`, and `hosts.local` in the configuration the two share describes the
  machine that dispatched it: read that way, a leg on a Mac ran with the Windows machine's
  core counts and environment. The dispatch names the host, and its cores, its `env`, and
  the `{host}` a label records are all read under that name.

## Leg integrity

A verdict is only worth reporting if it describes the code rather than the moment the
code happened to run in. Each rule here answers a failure measured in the scripts this
tool replaces, where a green result had quietly stopped meaning anything.

### Evidence that the work ran

- The exit code is read from the process itself, never through a pipe or a wrapper.
- Every test invocation declares a `successPattern`, checked on the platform section
  merged over `all`. It is matched against the runner's own output and never against
  anything the harness wrote: a log header that echoes the command line contains the
  pattern whenever the command does. An empty pattern matches anything and is refused
  when the file is read.
- A build passes only if every file in the project's `buildOutputs` exists afterwards,
  so a build that exited 0 cannot hand its tests a binary left over from an earlier one.
  An entry is a path, or a mapping of platform to path where the platforms disagree about what the
  same target is called — a program CMake names `app` is `app.exe` on Windows, and a static library
  differs by prefix as well as suffix. A bare string applies everywhere, so a list written before
  this means what it always did. An entry that names no path for a platform some leg builds on is
  refused when the file is read, naming the leg: a witness that is quietly not checked is the
  failure `buildOutputs` exists to prevent, and the legs and their operating systems are all known
  then. A suffix added automatically was the alternative and is weaker — it has to guess which
  entries name programs, and cannot express a name differing by more than its suffix.
- A ninja build's dependency records are read after it, with `ninja -t deps`: an object that
  recorded no header dependencies is never rebuilt when a header it includes changes, so it fails
  the leg, and records that cannot be read leave it `unmeasured`. Only an object built under
  `deps = msvc` - its build line's own, or else its rule's - can record none legitimately:
  `/showIncludes` reports headers and never the source, and ninja drops every header whose path, as
  ninja holds it relative to the build directory, names `program files` or `microsoft visual
  studio` as the system's own. So a unit including nothing, or only the standard library and the
  Windows SDK, records none, and so does one built from a precompiled header under `/Yu` that
  includes, besides, only headers the precompiled header holds and guards - with `#pragma once` or
  an include guard - which cl never opens again; one it holds unguarded is read again, and
  recorded. Such a zero is excused only where ninja rebuilds the object, all the same, for every
  header its compile surely includes: each header its command force-includes with `/FI`, and what
  its source and those headers include by a quoted include beside them, of a header ninja keeps -
  never inside a comment, and inside a conditional block only where the block is surely compiled.
  A block is surely compiled where its condition is a number, or asks whether `__cplusplus` is
  defined, which the unit's language answers - C++ under `/TP` or for a C++ source, C under `/TC`
  or for a `.c` one - and never where it asks anything else: one under `#ifndef _WIN32` may be
  compiled out. The command is the one ninja ran, evaluated as ninja evaluates it: CMake's C++ rule
  adds `/TP`, and each build line's `FLAGS` force-includes the header CMake precompiles, which for
  C++ holds its includes under `#ifdef __cplusplus`. An input a build line only depends on - an
  `OBJECT_DEPENDS` directory, say - is rebuilt for and never read. Ninja rebuilds an object for its
  build line's inputs and, for each input that is itself built, for what that build recorded and its
  own inputs - so a unit is rebuilt through its `.pch` for every header the object compiling it
  recorded, and while that object records none, neither it nor any unit built from it is excused
  for a header it holds. Only the object's own build line ever excuses it. A `msvc_deps_prefix` that stops matching what
  cl prints - a Visual Studio in another language - or a compiler cache replaying an object without
  cl's includes breaks only the objects rebuilt since, while older records stand: rebuilt with the
  prefix broken, two objects recorded `#deps 0` beside a precompiled header's object still
  recording its four headers, and read as proof that the build reads them, that record would have
  excused both; and rebuilt with it broken, a C++ precompiled header's object and every unit built
  from it recorded nothing and fail the leg. Under `deps = gcc` the source itself is always
  recorded, so zero is never legitimate there. How each object is built is read the way ninja
  reads the manifest: across the files `build.ninja` includes, since CMake keeps its rules in
  `CMakeFiles/rules.ninja`, with ninja's escapes undone, since CMake names each source absolutely
  and a drive's colon arrives as `$:`, and with its variables evaluated, since a compile's options
  arrive in them. Read from `build.ninja` alone, with the escapes
  left in, no MSVC object was ever excused, and a translation unit including nothing failed every
  MSVC build it was in.
- Every run has its own id, and every log is scoped to it. No two legs ever write to one
  file, so one leg's result can never be read as another's.
- The programs a command will start are resolved on the host before a leg starts - each command
  its own: a build its build's, a test its runner too, a run its steps', a sync none - so a
  missing tool is `skipped-tool-missing` and named, not a failure halfway through. A program
  named by name, or by a path absolute on the leg's platform, is looked for beforehand; one
  named by a relative path or with a placeholder is the run's to find, and so is one started
  under an environment the configuration declares that sets PATH - looked for, so its directory
  reaches that PATH, and never required. One that will not start once the leg is running fails
  the leg, naming the program and the reason the system gave: never a skip, and never `poisoned`.
- A leg-running command asked for `--json` answers with its ledger whatever ended it, once its
  command line was read: a refusal before any leg was placed, a selection no host could take, a
  log another run holds, a refusal while the legs ran - each is the document too, with the code
  the process exits with and the line it ends on. An interruption is said as one: `cancelled`,
  with exit 130, so a script never reads it as red. A machine that dispatched a leg reads the
  host's answer this way, so a host's refusal arrives as that refusal, in the host's words.
- An emulated leg's emulator has passed its witness on that host before the leg starts.

### Inputs that hold still

Test suites read files from the tree while they run: configuration, corpora, fixtures.
Edit one mid-run and some tests see the old file and some the new, and the report
describes a tree that never existed. This was measured: eight failures, all passing
seconds later on the unchanged tree, because a configuration file was rewritten while
the suite ran.

A leg therefore fingerprints `test.inputs` (by default every file git tracks) before
its tests start and again after they end, on the host that runs them, by content and
size. Different fingerprints make the verdict `inputs-moved`. A fingerprint that could
not be taken makes it `unmeasured`: an unreadable snapshot is never reported as clean.
There is no escape hatch. A command that rewrites its own inputs is a build step, not
a test.

Between the two readings the inputs are watched, since two snapshots cannot see an edit
undone before the second. A watch reports what happens once it exists, except on macOS,
which numbers file events as it reads them: a write made a moment before a watch began
is sometimes delivered to it. There, each file the watch is told of is looked at as it
is told: one that stands as the tests found it - the same size, and written and created
when it was - was told of late, and counts for nothing; any other counts. Looked at then,
not once the tests are done, because a file moved aside and put back reads the same at
both ends and was something else while they ran. What goes unseen on macOS is a change
undone before word of it is looked at, and one of the same size undone in place by a
tool that also puts the old time back. The times are compared for equality alone, never
ordered.

### Clocks are never trusted to order anything

One host this tool must serve has a wall clock that steps forward by about 25 seconds,
for about 200 milliseconds, every few seconds, and the steps reach file modification
times: a file written one second after a marker carried a timestamp 24 seconds before
it. So:

- Nothing decides that something changed by comparing two timestamps taken at different
  moments or on different hosts. Change is detected by equality, as above, which a clock
  cannot distort because both readings carry the same distortion. Sync decides what to
  delete by comparing manifests, never by stamp order. Where dates are still ordered, it
  is to ask what a build system that orders them will do with a change already found by
  content, below, or which of two files CMake wrote in one configure came first.
- Durations come from the monotonic clock. UTC times are for display only.
- Each phase compares elapsed wall-clock time with elapsed monotonic time. Drift beyond
  `defaults.clockStepToleranceMilliseconds` records a clock step or a host sleep inside
  that phase: its durations are suspect, and so is every timestamp it wrote.
- Incremental builds are protected from it. Ninja, Make and MSBuild decide what is
  stale by ordering timestamps, which a stepped clock defeats without a word: an object
  stamped during a forward step looks newer than a source edited just after it. In each
  variant's build directory the harness keeps a record of the build that last ran there:
  a content fingerprint of the inputs the build system was given, and when each had last
  been written, taken before it runs, so a build that fails or is stopped by its caller's
  time limit leaves the record of what it compiled from. A phase that spans a clock step
  marks the record at once; the end of the build writes it again with the newest file the
  build left, and marks it unordered, with why, if anything doubted it: inputs that did
  not hold still, a directory something else used, an input that could not be read.
  Before building again, if the record is marked unordered, or an input whose content
  changed since is dated no later than the newest file that build left, the variant is
  rebuilt from clean and the ledger says why, naming the file and both dates; so is a
  directory that holds files and no record, as a clean start stopped part way through its
  delete can leave one, since nothing says what they were built from. The newest
  file, not the declared outputs or the record: a step forward and back inside one phase
  measures no drift, and an object compiled in it is dated ahead of the binary linked
  after. What the build left, not the directory as it stands: a test run writes there
  too - ctest its logs as a suite ends - and an edit made while the suite ran would be
  dated behind them. After a build that never finished, which recorded no newest file and
  whose guards never said whether its tree held still, the directory is read as it
  stands, and an input written again since it began counts as changed though its content
  held: a stash and its pop leave one exactly as it was, having let the compiler read
  something else in between. A stale binary reported as a pass is the one price an
  incremental build must never pay. An input changed and dated after all of that is the
  build system's to act on - newer than every output, it rebuilds what reads it - as is
  one deleted since, which a build system sees gone without asking its date, and a CMake
  project is configured on every build. What a build system does not know reads a file -
  a custom command's input it names in no `DEPENDS` - is not remade. Rebuilding from clean
  for every change put a consumer through 1,186 steps from nothing for one edit to one
  input, and a build stopped for running long would have started from clean again every
  time. The record keeps `clock-stepped` on its first line for an unordered build,
  and each input's fingerprint on a line of its own, as 0.5.8 wrote and reads them.

### One run per build directory

The lock (see *Locking*) keeps two harness runs apart, but a lock cannot see a tool
someone starts by hand. Measured: a test run started in a shared build directory
while a gate ran turned a green suite red, with four test processes live at once.

- Each leg samples the process table when it starts, when it ends, and every
  `defaults.processSampleSeconds` between. A `contention.buildTools` process outside the
  harness's own process tree whose command line names the leg's build directory makes the
  verdict `contended`. A `contention.sharedResourceTools` process, one that shares a cache
  rather than a build directory, is reported as a warning.
- Every sample is kept, and the report says when each process was seen: throughout, at
  the start, or at the end. A process table that could not be read is reported as
  unknown, never as nothing found.
- **A leg no sample could read the process table for is `unmeasured`, not passed.** The
  platform's own source is the only one that carries a command line, and a command line is the
  whole of what contention is decided by, so a machine whose query is blocked by policy would
  otherwise report "no contender" for every leg, for ever, without a word. One failed reading
  among several is a stated limit rather than a verdict: the samples that succeeded did look.
- No verdict depends on a sample finishing within a time window. Sampling costs
  seconds on one platform and a fraction of that on another, and one such overhead
  asymmetry was once read, for a whole cycle, as a speed difference between legs.
- **A phase is bounded by silence, and silence starts when the child does.** Reading the request,
  opening the log and starting the process are this tool's own time; counting them against the
  child made a slow launch on a loaded machine read as a hung command. A child that starts and then
  says nothing is still bounded, because starting is itself something the clock is told about.
- A process is identified by its id together with a stamp that tells it from the next
  holder of that id, and a parent link is followed only when the parent started no later
  than the child. Process ids are recycled: on Windows a freed id was measured coming back
  after about a hundred allocations.
- **That stamp holds no clock.** On Linux it is the boot this machine is on and the tick
  within it the process started, read from `/proc`; on Windows and macOS it is the start
  time the kernel records once at creation and never works out again. A start time
  recomputed from the current clock — which is what `ps lstart` reports, and what adding
  `/proc/stat`'s `btime` to ticks-since-boot produces — moves for every live process the
  moment the clock steps, and every live holder then reads as a recycled id at once. On a
  host whose clock steps by about 25 seconds every few seconds, that is not an edge case.
- What sampling cannot see is stated in the report: a tool started from inside the
  build directory with a relative path, since another process's working directory
  cannot be read, and processes that do not expose their command line.
- Selecting two legs that resolve to the same build directory is refused before either
  starts.

### Legs that stay comparable

- Core counts are handed to every runner explicitly, through `coresEnv` where the runner
  reads a variable and `coresArgs` where it does not. A runner left to its own default
  runs serially on one host and on every core on another. A variable is preferred: an
  explicit option in the invocation's own `args` still wins.
- A leg's environment is carried to its host explicitly. WSL passes on only the
  variables named in `WSLENV`, and ssh passes on none.
- `countPattern` extracts how many tests each leg ran. Legs running the same tests - the same
  project, and the same `testSet` - that report different counts are marked, among at least
  three: a platform that quietly skips a group of tests passes on less evidence than its
  siblings. The mark is its own part of the leg's line, `test count differs: it ran 2238
  test(s), where 2 other leg(s) ran 2237`, and its own fields in `--json`, `testCountDiffers`
  and `testCountNote`, beside the `project` and `testSet` the count belongs to. It is never a
  timing mark - a Windows-only test once read as a Windows leg's timings being suspect - and
  never changes a verdict.
  - **A difference that is expected is declared.** A test invocation naming a `testSet` - a
    platform's own tests, a sanitizer leg's subset - is compared only with the legs naming the
    same one, and every other leg of the project with the rest. A leg of another project runs
    another suite, and is compared only with that project's legs; a count whose leg resolves no
    project - which only `test --no-build` can reach - is compared with nothing.
  - **A count is recorded with what it belongs to where it is made**, on the host that ran the
    leg, and read back with it. A host running what it has staged may have run another set than
    this machine's configuration now names, and is compared as it counted.
- The ledger reports command time and harness overhead (sync, fingerprints, sampling)
  separately. A phase slower than `defaults.durationWarningFactor` times the same phase
  on sibling legs of the same kind is marked suspect. A timing mark never
  changes a verdict. An emulated leg is never compared with a native one.
- `keepAwake` holds a host awake while a leg's own work runs there. A host that slept once
  reported a 4 millisecond test at 729 seconds. The command is started on the machine that
  runs the work - by the DssHarness on a host a leg was dispatched to, under that host's own
  section - with `{pid}`, the one name it is filled in with, replaced by the DssHarness process
  running the leg, and it is stopped when the work ends. `["caffeinate", "-dimsu", "-w",
  "{pid}"]` on macOS also stops by itself should that process end first. A command that cannot
  start, or ends early, is said and fails nothing: a sleep it did not prevent is still seen, as
  wall time outrunning the monotonic clock, and marks the phase it interrupted suspect, as it
  does on a host that declares no command at all. The survey asks about the command, so a
  directory it is found in reaches its PATH, and turns no leg away for it.
- `holdAwakeSeconds` holds an ssh host awake between commands, and never during one. Every
  keepAwake a command starts on a host ends with the connection that started it, and a personal
  Mac falls back asleep in the seconds before the next command, which then cannot find it. So a
  host that declares it is recorded as each command reaches it, and asked, as the command ends -
  however it ended - to hold itself awake that long. The DssHarness there records the hold and
  starts a process of its own, detached from the connection, which runs the host's keepAwake with
  `{pid}` filled in with itself and goes on once the connection has ended. The next command's own
  keepAwake ends the hold there, as it starts; a newer hold replaces an older one; and a hold
  ends by itself when its seconds are up. The hold's process watches its record rather than being
  stopped by an id, which a process started since could have been given, and the record is kept
  among the user's own application data, one per user of the host. A hold that cannot be left is
  said, and fails nothing. On a Windows host, OpenSSH may end the hold's process with the
  connection.
- A host's compiler cache is that cache's own variable in the host's `env` - `CCACHE_DIR` for
  ccache - so two hosts never share one store, and a build keys it against the leg's own tree.
  The `compilerCacheDirectory` key that once said the same is retired, and refused where it is
  read, naming `env` instead.

### Hosts and trees

- An ssh host bounds how long a connection may take to open and how long it may go
  unanswered (`connectTimeoutSeconds`, `keepAliveSeconds`). Without both, a dead link
  hangs a leg indefinitely, with no output and no verdict.
- A host's copy of a tree is a git repository sync creates: the main checkout's at the host's
  `repositoryPath`, and each worktree's beside it, at `<repositoryPath>.worktree-<name>`, named for
  the worktree's directory as a worktree's name is spelt. One copy per host had every worktree whose
  legs reached a host wait for every other's, under one lock, each sync replacing the tree the one
  before had put there. Beside the main copy rather than inside it, because the agent a sync starts
  begins in the copy's parent, which must already be there, and a copy inside another would be taken
  for part of it by git. This machine records which hosts hold a copy of which worktree, and of
  which tree on this machine, in `.harness-config/host-copies` in the main checkout - as the lock is,
  at a place no branch's configuration moves - which ignores itself, as the runs directory does, so
  git never sees it and no sync carries it. A sync claims its copy there before it writes anything,
  so a first sync that stops part way is recorded too, and is refused one another worktree of the
  same name - made by hand, or by another tool, outside the worktrees root - still holds: synced by
  both, each would replace the tree the other put there. Deleting a worktree asks each host that
  holds one of its copies to remove it, only where the harness made it, and no other host. A host is
  reached through the worktree's own configuration, read before it goes - its branch may declare a
  host the configuration the command runs in does not - or else through that one; a host neither
  declares is not asked, and its copy is forgotten, named. Each copy is removed under the lock a leg
  this machine runs there takes, the record read again once it is held, so a copy another worktree
  of the name has claimed since is left for it; it is forgotten before that lock is let go, and its
  marker goes last, so a removal that stops part way leaves the rest marked as the harness's. The
  host is asked from its home directory, which is there when the directory the copy was kept in is
  not, so a copy whose directory is gone is answered as not there, and forgotten.
  A copy that cannot be removed then stays recorded, and the deletion fails naming it though the
  worktree is gone, with the highest code a copy was left with - 13 where a run holds one or it was
  refused, 15 where its host is unreachable, 20 where the removal failed there - so whoever deleted
  it learns something of it is left; deleting the worktree again finishes the job, and for a name
  whose worktree is gone removes what any worktree of that name left, never the copies of one that
  still exists. On a host a leg was sent to,
  the copy it was sent to is its tree, whatever worktree the leg names. The working tree being tested
  is transferred into its copy file by file, compared by content hash, so what the host holds is this
  tree including its uncommitted changes. Nothing is pushed and it is never
  a clone from a remote, either of which would need credentials on the host and neither of which
  could carry a change nobody has committed. It is made a git repository because the host's
  DssHarness finds everything through git, and sync never writes into a directory it did not
  create, because it deletes whatever the source does not have.
- A remote tree's identity is its content manifest, confirmed equal to the source after
  every sync. The ledger records the commit and manifest each leg built, and a build
  directory produced from a different manifest is flagged.
- A build directory's recorded source directory must be the leg's own tree, such as
  CMake's `CMAKE_HOME_DIRECTORY`. A build directory configured from a different worktree
  is refused, not reused: watching the wrong tree produced both a false refusal and a
  silent wrong answer.
- Commands run as argument lists, never through a shell, so no shell's process
  emulation sits between the harness and a runner. MSYS's emulation was measured losing
  tests from a parallel test run with no failure reported.

## Cross-leg contamination

The hazard: two legs sharing state and silently corrupting each other's results.
Seven independent guarantees, each addressing a measured failure mode:

1. **Variant-keyed build directories.** `<tree>/build/<processor>-<toolchain>-<config>[-<sanitizer>]`.
   CMake refuses a compiler change on an existing cache, so `msvc` and `gcc`
   cannot share `build/release`, and neither can a native build and a cross-build.
2. **Tree-rooted paths.** Every path derives from the leg's tree root, so a
   worktree's build output can never land in the main checkout's.
3. **Build directory guard.** Before configuring, `CMakeCache.txt` is read and
   the run is refused if `CMAKE_HOME_DIRECTORY` or the recorded compiler
   disagrees with this leg. The compiler is compared by the file it starts: the name the
   leg's toolchain gives is resolved on the PATH the build's phases are given - its
   environment's own, or this process's, with the directories a survey found programs in
   appended - and held to the whole path CMake cached. A name compared with a name let a
   directory configured with one gcc be rebuilt with another earlier on the PATH, and the
   leg reported on objects from both. A name the search finds nowhere cannot start, and is
   compared by name until the build says so.
4. **Per-host compiler cache.** `CCACHE_DIR` and `CCACHE_BASEDIR` are set
   explicitly per host rather than inherited, so hosts never share a store.
5. **Clean run directories, incremental build directories.** Scratch and run
   directories are wiped before every leg; build directories are preserved.
6. **Locking.** See below.
7. **One sync per tree.** Legs sharing a host's tree share its single sync instead
   of each writing the same files at the same time.

### Incremental builds across a transport

Existing tooling forces a clean rebuild after every remote sync, because
`rsync -a` and `tar` preserve mtimes: a synced source whose mtime lands behind
an existing object file makes Ninja skip the rebuild and report a stale binary
as success.

`DssHarness` syncs by **content hash**, writing only files whose content actually
changed. An unchanged file is not touched, so its mtime does not move; a changed
file is rewritten now, so its mtime advances. Ninja's incremental check is therefore
correct after a sync, and incremental builds are preserved on every host.

That holds while the host's clock is honest. *Clocks are never trusted to order
anything*, under Leg integrity, covers what the harness does when it is not.

### Locking

`.harness-config/lock.json`, always in the **main checkout** (a worktree has its
own `.harness-config`, so a per-tree lock would make two runs of the same leg
invisible to each other). Gitignored.

One entry per `(host, tree, variant)`, recording host, pid, process start
time, run id, UTC timestamp and the command. A host's copy of the repository is locked
by the DssHarness on that host, in that copy's own `.harness-config/lock.json`, so runs
started from two different machines against the same host see each other.

Two granularities, because two kinds of work share a tree. Syncing a tree takes the
tree exclusively, since it rewrites files every variant reads. Building or testing takes
the tree shared and its own variant exclusively, so variants build side by side but never
while their sources are being replaced. A lock is released only by the run that took it.

- A held lock **refuses immediately**. It never waits: silently blocking for
  hours is worse than a refusal that names the holder.
- Staleness is decided by **liveness, never by a timeout**. A timeout is a guess
  about how long honest work takes, and it eventually breaks an honest run.
- A dead holder on this host is reclaimed automatically, and the reclaim is
  reported. A holder on another host requires `--force-lock`, which is always a
  human decision.
- A clock-free process stamp is recorded alongside the pid so a recycled pid is not
  mistaken for a live holder, and so a clock that steps cannot turn a live one into a
  dead one. Compared exactly: there is no clock in it for a tolerance to absorb.
- `--force-lock` takes any lock actually in the way, on this host or another. On this
  host it is the only way out of an id that has come back around to something live,
  which would otherwise hold a tree until the file was edited by hand. It takes the log
  path with it, for the same reason.
- A lock file, or the file that records who owns a run's logs, that cannot be read or written
  refuses the run, exit 13, naming the file - a runs directory an earlier run under sudo left to
  root is the usual cause. It stops every run on every tree alike, so it is never reported as
  each leg being locked by a run that does not exist, nor as a defect in this tool.
- A lock, or a log path, that cannot be given up once its work is done is a warning naming it,
  and the work's verdict stands. The entry names a process that has ended, and is reclaimed as
  a dead holder's is.

### Where a run's records live

A run's records - its logs, and what it has already completed - are kept in
`.harness-config/runs/<run id>/` of **the tree that ran it**, a worktree's own included, so a
worktree reads what it judged without leaving it. Kept in the main checkout instead, as they
once were, a worktree's runs landed beside the main checkout's. Nothing in `runs/` is shared
between runs: each writes only the directory named by its own id. What two runs from different
trees contend over is the lock above, which stays in the main checkout.

The directory ignores itself: the run about to write there first gives it a `.gitignore` of its own,
holding `*`, so its records never show in git status, whatever the tree's `.gitignore` says. Kept on
another disk through a link, it is the link git sees, and never what is beyond it: the managed
block ignores it by name, so the link is ignored too. A
worktree of a branch that predates the harness holds no rule for it, and its records were committed
by the next `git add -A` and made `delete-worktree` refuse over the harness's own logs. No sync
carries them, as none carries any of the harness's own state; deleting a worktree deletes its runs
with it; and a run is resumed from the tree it was started in. A caller never works the directory out:
`build`, `test` and `run` name it on every exit that created one, as `logs: <directory>` and as
`runDirectory` in `--json`. A leg another host ran was run there under a run of its own, and its
line names that host's directory, as `logs of <leg> on <host>: <directory>` and as the leg's own
`runDirectory`.

## Disk space

### Removing a build directory

`clean` removes each selected leg's build directory where the leg runs: in this machine's
tree, or in the copy of the tree a WSL distribution or an ssh host holds. It is for a disk a
build filled, where nothing else helped: a build that starts from clean fills it again as it
goes, and deleting a worktree takes its local tree too. So it writes nothing on the machine it
removes from before it has removed - no sync, no lock entry, no run records.

- **The lock is read, never written.** A leg is kept from a build of it by the lock that build
  takes, keyed the same way - host, tree there, variant - on the machine the command runs on and,
  for a leg on a host, by the DssHarness there in that copy's own lock file. It is read under the
  machine-wide mutex a run needs to write it, and while nothing holds it the directory is renamed
  aside, so no run can take the lock between the reading and the renaming. A held lock refuses the
  leg, `refused-locked`, naming the holder. An entry of a run that has ended is passed over and
  left in the file: taking it back would write the file.
- **Renamed, then removed.** A rename writes no file's contents, so it needs no room the disk
  does not have, and it takes the directory out of a build's way at once; the removal, which
  can take minutes, happens outside the mutex. A build started meanwhile starts in a new
  directory. What an interrupted removal left aside, hidden beside the build directory, the next
  clean of that leg removes first.
- **A link is left alone.** A build directory that is a link was put somewhere on purpose, and
  removing the link would free nothing and have the next build fill this disk instead.
- **Said per leg.** Each leg's line says what was removed and the room left on its filesystem,
  or, with `--dry-run`, what it holds, removing nothing; `--json` carries both as the leg's
  `space`. A leg on a host is asked of the DssHarness there, in the copy - once the copy is known
  to be there, so a tree never synced to a host has nothing removed rather than a host refusing it.

A host whose DssHarness is older than this machine's is brought to this build first, as it is by
every command that asks it anything, and that write needs room. A host that is both full and
behind is freed by hand, once.

### Room before a build

A leg is placed only where its host has the room its build still needs, as it is only where the
programs it starts are. A consumer's two variants - the first builds of a new worktree's copy -
filled a host's disk half way through and died writing an object, while `legs` said the host
could run them.

- **What a build needs** is what its build directory comes to once built: the leg's
  `buildSpaceGiB`, or, left out, what a build of its variant recorded as it finished - in the
  tree's own copy on that host, or else in the main checkout's copy there - less what the
  directory already holds. A build records what its directory came to in its `.harness-build`,
  summed from the walk it already makes of the directory as it finishes.
- **Nothing is walked to decide.** Each host is asked, in the same measuring that finds its
  programs, the room on the filesystem its copies are kept on and, for a command that builds,
  what each leg's build directory - and the main checkout's copy of the same variant - holds as
  recorded, with the room where each is. The room is the filesystem's own count.
- **Legs sharing a filesystem add up.** Legs building on one filesystem of one host are counted
  together, in the order they were selected, because every build directory stays once built. A
  leg that does not fit beside the ones before it is `skipped-unavailable`, naming what is free,
  what it needs and why, and what the legs before it need; they are kept.
- **Unknown is not refused.** A leg nothing has measured that declares no `buildSpaceGiB` is
  placed as it always was, and so is one whose directory no build of this version recorded,
  since what that directory holds is an amount nothing measured.
- **Only a command that builds.** `sync` and `clean` need no room: clean is how room is made.

`legs -v` says the room on each host it measured - where its copies are kept, and the tree the
command was typed in for this machine - or why it could not be measured, so a host that is nearly
full shows before a run fills it. `--json` always carries it, as each host's `space`.

## Syncing a tree

`sync` puts a host's copy of the repository in step with this tree. It is the same code path
for this machine, a WSL distribution and an ssh host, so a sync to a host and a sync to a
directory here cannot drift apart.

- **The copy is the tool's.** Sync creates it, records that it did, and refuses to write into a
  directory it did not create. It deletes whatever the source does not have, so taking over a
  checkout somebody made by hand could delete work nothing here knows about, on a machine whose
  owner is not watching.
- **The refusal says what taking it over would cost.** It is worked out from the same manifest
  and plan a real sync uses, so the reader is told which files would be overwritten and which
  deleted, rather than only that the directory is not the tool's. An overwrite is named apart
  from a write everywhere it is reported — in the refusal, in `--dry-run`, and while it happens —
  because a file the copy already had, holding an edit nobody committed, reads exactly like a file
  the copy never had, and only one of the two loses anything. For the same reason an overwrite is
  reported by default rather than only under `--verbose`, as a deletion already was.
- **`--adopt` names the hosts it takes over** — `--adopt vps`, not a bare yes. One sync reaches
  every host, so a flag meaning "go ahead" would take over whatever unexpected directory is found
  at another host's `repositoryPath`, a mistyped one included, without being asked again. A host
  nobody named is refused exactly as it would have been without the flag, and told which spelling
  would take it.
- **It says what that is about to cost before it starts** — not only in the refusal, because
  somebody who reads the flag in the help and types it never sees a refusal.
- **The marker records which it was.** Afterwards a copy taken over and one the tool made are the
  same directory, and only one of them deleted somebody's files; the marker is the only thing left
  that can say so.
- **What survives an adoption is narrower than it looks.** Its `.git` and so every commit in it,
  the rest of `.harness-config`, the worktrees root and whatever `sync.neverTransfer` names are
  protected from the deletion — though `config.json` there is replaced with this tree's, which the
  refusal says. The ignore list is *not* read from that host: it is this tree's, listed by asking
  git which ignored files exist **here**. A directory only the host has — a build tree under a name
  `sync.neverTransfer` does not carry, a `node_modules`, a virtual environment — is ignored by
  nothing this side can see and is deleted like any other file. It appears in the list the refusal
  prints, which is why the list is the thing to read; name it in `sync.neverTransfer` first if it
  should stay.
- **`sync.maxDeleteFraction` bounds an adoption too.** A directory that exists and carries no marker
  is exactly what a mistyped `repositoryPath` produces, which is the case that bound was written
  for: the path meant to name a checkout names a home directory, and every other project under it
  is what the source does not have. Two gates for that is the point of having one.
- **A takeover is marked as begun before anything is deleted, and as finished only once the copy is
  one.** Both of the obvious markings are wrong about a run that stopped part way: unmarked, the
  next run refuses and reports a smaller loss than the first did, because what has gone no longer
  appears in a plan; marked complete, the next ordinary `build` or `test` — which never carries an
  adopt list — would quietly delete the rest with nobody asked at all. So a stopped takeover is its
  own state: it still needs `--adopt <host>`, and its refusal says plainly that what the earlier run
  removed is not in the list.
- **A link the copy holds is named too.** A link sits in no manifest — the walk refuses to follow
  one — so a file written at its name replaces it and reads as an ordinary write, and everything
  behind a linked directory is outside any list a plan can build. Reported rather than guarded
  against: a build directory pointed at another volume is ordinary, and refusing to write through
  one would refuse `--pull build/...` — a path the reader named, inside the tree they named.
- **`--dry-run` shows the plan instead of refusing,** for a directory the tool did not create and
  for one whose deletions are over the bound. A preview changes nothing, so there is nothing for
  either refusal to protect — and the bound's own message says to run with `--dry-run` to see the
  list it was until now refusing to show.
- **The copy is created, with its parents,** when the declared `repositoryPath` is not there, so
  the first sync to a fresh host needs no hand-made clone. A path that exists and is not a
  directory, or that cannot be created, is a named failure — never a silent fallback to
  somewhere else, because a leg would then report on a tree the reader cannot find.
- **The copy is a git repository,** because the DssHarness on that host finds everything through
  git. It is made one after the transfer, so a copy that failed part way is never left looking
  complete.
- **The copy's index holds what the sync carried,** and nothing else: every file of the transfer and
  the configuration placed with it, staged as they stand, and anything the index held that the sync
  did not carry removed - on every sync, so a copy made before this is put right by the next one,
  whatever that carries. Everything on the host that reads "the files git tracks" reads the index: a
  build's input fingerprint, which lets the next build keep its directory, and the guards that hold
  a build's or a step's inputs still. Written without staging one, a copy's index named nothing, so
  every build there after the first started from clean and every such guard watched nothing,
  without a word. A file written through a link in the copy is outside it and is not staged; the
  write is warned of, and the verification refuses the copy. A tree git tracks nothing in is now
  said, by a build and by a step that asked for its inputs held still. The copy's history stays its
  own.
- **Content, never timestamps.** A file is written only when its content differs. An unchanged
  file is not touched, so its modification time does not move and an incremental build on that
  host stays correct; a changed file is rewritten now, so its time advances. Nothing compares two
  times taken at different moments or on different machines.
- **Deletions propagate.** A file removed from the source is removed from the copy in the same
  sync. A copy that only ever gains files is not a copy of the tree: a deleted source file keeps
  compiling, a deleted test keeps running, and a renamed file exists twice, so the leg's verdict
  describes a tree that no longer exists.
- **The never-transfer floor is also a never-delete floor.** `.git`, `.harness-config`, the
  worktrees root and everything under `sync.neverTransfer` are neither written nor deleted. A
  path the harness will not write is one it cannot know the source lacks, so deleting it would
  remove the host's own state rather than a file the source gave up — including the build
  directories that make an incremental build possible. `sync.exclude` is different: the source
  chooses not to send those, and a copy that kept them for ever would be a copy of a tree that no
  longer exists, so they are deleted.
- **What git ignores is never transferred, and never deleted.** Asked of git once per sync, so a
  local `.env`, a virtual environment or an editor's cache never reaches a host, and the host's own
  copies of such things are left alone. `sync.exclude` names paths to withhold *in addition* to
  these.
- **A `sync.neverTransfer` name that protects nothing is named.** An entry is rooted: `.secrets`
  covers the root's and no other, so one absent from the root while the name exists deeper protects
  nothing, and a reader takes it for protection. Before every sync the tree is searched for such
  names, and each is named with the fix, `**/<name>`. A name counts only where nothing the
  configuration writes covers it - not where an entry covers the path, the `**/<name>` the warning
  asks for among them, and not in the worktrees root - or the author who followed the advice would
  be told it again on every sync. The search goes where a sync goes, and into the harness's own
  directory besides: never into what a sync withholds - what an entry covers, the worktrees root,
  what git ignores, what `sync.exclude` names - though such a directory's own name is seen from the
  one holding it, so a `node_modules` git ignores still counts. What lies inside is generated,
  fetched or another checkout's, and it is where a tree's size is: on a consumer's tree 68,697 of
  69,890 directories lay under what its entries name, and a search that went in spent its
  20,000-directory budget before it reached most of the tree, and said so on every sync. The
  harness's own directory is searched all the same, though git ignores most of it by design,
  because a `.secrets` there is what the search was written to find. A search that still runs out
  says so; it never reads as having found none.
- **The copy gets `.harness-config/config.json`, and nothing else from that directory.** A leg
  placed on a host runs DssHarness there, and DssHarness in a directory holding no configuration
  refuses as not initialised — so without it the copy is a tree no leg can run in. The rest of the
  directory is connection data, credentials, locks and logs, each local to a machine by design.
- **Deletion is bounded.** A sync that would delete more than `sync.maxDeleteFraction` of the
  copy stops and changes nothing. A mistyped repository path makes the source look empty, which
  is indistinguishable from a source that deleted everything, and without the bound that empties
  a host. A first sync into an empty copy has nothing to measure and is never over it.
- **Deletion stays inside the tree.** Every path is resolved against the copy's declared root and
  refused if it leaves by `..` or by being absolute. Links are compared as spelled rather than
  followed, for the reason the takeover bullet above gives, and are disclosed instead.
- **What was deleted is reported** by name, at the level a reader sees by default rather than
  behind `--verbose`: what a sync removed from another machine is the one thing running it again
  cannot recover. `--dry-run` lists every write and every deletion and changes nothing.
- **The result is verified, not assumed.** After the transfer the copy's manifest is read back and
  compared with the source's. A tree that still differs fails, names what differs, and says
  nothing should be run against it.
- **Staging is the two commands, not a flag.** `sync` transfers and stops — that is all it ever
  does — and `build`, `test` and `run` take `--use-staged` to act on what is already there without
  syncing again. A `--stage-only` on `sync` would name a mode `sync` is always in.
- **Artefacts come home** with `--pull`, each file hashed on the far side and checked again on
  arrival. Evidence that a binary built here runs there is not evidence if nobody checked it
  survived the journey.

## Predefined runners

A procedure specific to one repository — a corpus build-and-test, a benchmark, a round trip —
lives in `predefinedRunners` rather than in the tool. `run <name>` executes one across the legs
it declares, with the same isolation, locking, stall bounds, witnesses and reporting every other
leg-running command gets. A runner that declares `requireBuild` has its leg built before it runs:
a runner that calls a program the build produces otherwise runs against whatever was left there.

**`requireBuild` gates the build, never the sync.** A leg on an ssh host or a WSL distribution runs
from that host's own copy of the tree — the host reads `config.json` and the runner's action file
from it — so the tree is put there whether or not anything is compiled. A runner that skipped the
sync because it compiles nothing would find no configuration on the host and fail saying so.
`--use-staged` is how a run says the copy there is already current.

Runners are keyed by name because a name is how one is selected — by `run`, and by the checks
below. An unnamed entry in a list could not be selected at all.

### Action files

A runner declares either `phases` or an `action` naming a YAML file under
`.harness-config/runner/actions`, never both: two descriptions of what one runner does would
eventually disagree, and nothing could say which one ran. Every field a phase carries —
`workingDirectory`, `env`, `successPattern`, `stallSeconds`, `continueOnError` — is a key on a
step, so nothing the verdict contract depends on is lost by declaring one instead of the other.

**One directory per action.** An action lives at `actions/<name>/<name>.yml`, and everything its
steps run — a program, a fixture, a data table — lives in that same directory. A `run` line is a
program and its arguments with no shell, so anything that is not a one-liner has to be a file;
a flat directory gives that file nowhere to live that is obviously owned by the action it belongs
to, and two actions' supporting files would sit side by side with nothing saying which was whose.
The file carries its directory's name so that neither can be renamed quietly into disagreeing.

The rule is applied in two places on purpose. Its **spelling** — two segments, no `.` or `..`, not
rooted, ending in `.yml` or `.yaml`, the file named for its directory — needs no file system, so
`config.json` is refused for it when it is read, and `legs` and `run` therefore answer the same way
about the same repository. Its **resolution** — that the file is there, and that no link along the
path leads out of the actions directory once every link is followed — is what only the file system
knows; `legs` and `run` both check it before a leg is placed, and the parser checks it again when
it opens the file, because the last line of defence does not get to assume a caller validated
first. Containment is compared with this platform's path rules and at a directory boundary, so a
sibling directory whose name merely starts the same way is outside, not inside.

- A step either `uses` a predefined action or carries a `run` block. There are two predefined
  actions, confirming the tree is at a named commit and reading inputs; an unknown one is refused
  naming what is available.
- A `run` block is split on newlines and each line is trimmed, so indentation and blank lines
  cannot change what runs.
- **A step runs at the leg's tree root unless it says otherwise.** That is measured behaviour and
  it did not change with the layout above, which reads as though a step ran beside its own file.
  `workingDirectoryRoot` names what `workingDirectory` starts from — `tree` (the leg's worktree or
  the repository, the default), `harness` (`.harness-config`), or `action` (the action's own
  directory) — and `workingDirectory` is a path under it, the root itself when absent. A step that
  runs a program it ships says `workingDirectoryRoot: action` and names it `./probe.py`; a step
  that builds or tests the repository says nothing and keeps the root it always had. The roots are
  resolved relative to the leg's own tree, so a leg on a worktree reaches that worktree's copy.
- **`runOn` picks a step per operating system.** A step naming `runOn: [windows]` runs only on a
  Windows leg; one without it runs on every leg. A leg of another system drops the step before the
  file is vetted, its names demanded or its phases made, so nothing about the step is asked of that
  leg - neither its program, which no survey requires of that leg's host, nor a name its lines use.
  The leg says so as it runs, and lists the step as `skippedSteps` on its line in `--json`, so a
  step left out is never simply absent. A step that reads what a skipped step would have made finds
  nothing there; the two take the same `runOn`. A run in which some leg's system runs no step at
  all is refused before any host is measured, naming every such leg: it would pass having run
  nothing.
- **A step can run only where a run names it.** `manual: true` keeps a step out of a run that
  names no step, for work that belongs with an action and is not part of what running it means - a
  benchmark sharing modules with the build and test beside it, which one action per directory would
  otherwise leave nowhere to live. `run <runner> --manual-step <step>` runs only the manual steps it
  names; a runner may name its own steps in `config.json` (`"steps": [...]`), manual or not, and
  becomes a runner with legs of its own that a gate names like any other; the command line wins over
  the runner. Whichever chose them, the steps chosen are the file from then on: selection runs
  before `runOn`'s, and the tool policy, the names a step may use, the inputs a run may be given,
  the programs a host is asked for and the refusal of a leg that would run nothing all read only
  what will run. A step lists under `needs` the steps declared before it that run first whenever it
  does - one declared after it could not have run by then, one that does not run on every system the
  step runs on would leave a leg there running the step without it, and a step every run runs
  needing a manual one would make a plain run run it, so each is refused when the file is read.
  Steps of one name are one step to whatever names them, narrowed by a leg's system to the one it
  runs, so a manual step cannot share its name with one that is not. A manual step declares a
  `successPattern`, since it is the step whose green line is read as having done the work it was
  named for; a predefined action cannot be manual. A run says of every step it did not select that
  it did not run it, as it goes and as `unselectedSteps` on each leg's line in `--json`, and lists
  the steps a leg ran as `ranSteps` and the manual ones among them as `manualSteps`, so a plain run
  is never read as having benchmarked. A `--manual-step` naming a step the action lacks or one that
  is not manual, a runner naming a step its action lacks, and a leg on whose system none of the
  steps a run names runs - it would run only what they need, and pass, with the step named run
  nowhere - are refused before any host is measured, naming the steps there are.
- **An input's value comes from `run --input name=value` first**, the runner value directories
  second and the input's own `default` last. A step may declare inputs of its own beside the
  action's, resolved the same way and read by that step alone: another step naming one names
  nothing, and a name the action already declares is refused, since one value could not mean both.
  `--input` takes one pair each time it is given, for an input the action declares or a step the
  run runs declares, and only for the runner the command line names - a runner a run check
  starts reads its own values. Any other name, a runner of phases, an empty value and a name given
  twice are refused before a host is measured: an unset shell variable is not a request to run
  with nothing, and a value for a name the file never reads changes nothing while the command line
  says it did. A host running one of the run's legs is handed the same pairs, and the same
  `--manual-step`s, so no leg there runs a default where the command line gave a value, or the
  runner's own steps where it named others. The value is a plain one, on a command line and so in
  the process table; a secret stays in `.secrets`.
- **Each line is a program and its arguments, never a shell string.** No shell parses it, so no
  shell's word splitting, globbing or process emulation sits between the harness and the program.
- The splitter honours double quotes only, understands no escape, and strips every `"` from the
  token it returns. Measured: an **unterminated** double quote makes it drop the rest of the line
  silently. A line with unbalanced quotes is therefore refused when the file is read, because the
  alternative is a command that runs with arguments nobody can see are missing. An argument that
  must keep a quote, or that uses single quotes, is carried as its own list item rather than
  inside a `run` line.
- The first token must be a program the configuration declares under `tools`, or a path inside
  the repository. An undeclared program is refused before anything runs — the same list
  `install-missing-tools` guarantees is installed. It is judged as it will start: its names
  filled in, and a relative path read from the directory its step runs in, both worked out by the
  one function the run starts it with. Read as written, `{dir}/tool` was inside the repository
  while the step started a program wherever `{dir}` pointed. A refusal says what a line was
  read as where the file does not already say it, masked where a secret filled it in.
- A line that fills in to nothing starts nothing, and is refused naming it; so is a step whose
  directory fills in to nothing, or whose names hold a character no path can. A runner's own
  steps are held to the same rule before the first one runs.
- Values come from `.harness-config/runner/.env` and `.harness-config/runner/.secrets`, each a
  directory of files. A value that came from `.secrets` never reaches a log, an argument list or
  an error message.
- An unknown key, at the top level or on a step, is an error, as it is in `config.json`: a
  silently ignored key is a rule nobody applied.

### Expected exceptions

A runner may declare failures it is allowed to produce, each with the outcome to report instead
of an unexplained one. An entry has two halves that must not be confused: what it **matches** —
an exception type and a list of messages, each plain text or a regular expression, any one
matching being a match — and the **outcome** the match produces.

**An entry must show its work.** Every entry carries when it was earned, where, the mechanism
measured, and the anchor holding the evidence, and the file is refused without them. The lint
also refuses an entry that repeats another's type and messages, a message that does not compile,
and an entry that names no message or names one matching anything: that is an unconditional claim
spelled as a scope, and it excuses whatever happens to fail, hiding the regression it was written
to explain. Every measurement here is biased toward ABSENT — where the tool cannot establish that
an entry is scoped and earned, it refuses the entry rather than giving it the benefit of the
doubt.

An entry is scoped by the runner carrying it and the legs that runner declares, so an excusal
earned under emulation is never available to a native leg.

### The gate

An expected exception may carry `runChecks`, and until every one passes it excuses nothing.

- Each check invokes **another** predefined runner by name and compares its outcome with what the
  check expects. A field left out is not a requirement. `sameException` requires the same success,
  warning, result code and message as the entry it gates.
- The runner a check names is never the one carrying it, and a runner reached that way may carry
  no checks of its own, so a check is one level deep and cannot recurse.
- **Unconfirmed, the failure stays genuine.** That is the whole point of the gate.
- **The window is the failing unit's own** — its output up to its verdict line — never a
  once-per-run sample. A failure is excused only when at least `minStepsInFailureWindow` steps of
  at least `minStepSeconds` fall inside that window. A once-per-run sample was measured charging
  genuine-looking failures to the tool under test on a loaded machine and excusing them on a quiet
  one, on the same day; a sample taken before a run says only what the machine was doing then.

## Reporting

Progress is one line per leg transition, not a stream of child process output.
Child output goes to a per-leg log file and is echoed only under `--verbose`.

Every run ends with a per-leg ledger:

```
LEG                   VERDICT        DURATION  DETAIL
win-msvc-release      passed            2m14s  412 tests
wsl-clang-asan        failed            6m02s  3 of 412 tests failed
mac-clang-release     passed           12m40s  412 tests; timings suspect: the host slept
lin-gcc-release       inputs-moved      3m51s  2 inputs changed: config/c.lang.json, ...
vps-arm64-gcc-rel     skipped-unavailable      ssh vps: ssh could not connect
```

Beneath it, `logs:` names where the run's records are, and each leg another host ran names
that host's own; `--json` carries the same as `runDirectory`, at the top and on such a leg (see
"Where a run's records live").

## CI legs

`check-ci-legs` reads each leg's verdict from the forge's job metadata, one job at a time and never
from a run's rollup, and tells a test step that failed at or past its time budget - a possible
overrun, whose budget is to be re-derived - from one that failed before reaching it, which is a real
failure. It assumes no workflow of its own. Which jobs are legs, what a leg is called, and which
steps build and test it are the repository's `ci` settings: `legJobPattern`, a regular expression
whose `leg` group names the leg and whose optional `budget` group reads its budget from the job's
name, and `buildStep` and `testStep`, by their exact names. Until they are set, the command refuses
and names them. No forge fixes a leg's job name or a step's, and a command that assumed one
workflow's would read every other as having no legs at all. A leg whose job name gave no budget - a
long name the forge cut short, which a `legJobPattern` must still match, so what follows the leg's
name in it is kept optional - takes it from its workflow's text through `workflowBudgetPattern`,
where `{leg}` stands for the leg's name, and then from `legBudgetMinutes`. A failure with no budget
from any of them is called neither, and counted apart in the summary: a discriminator that invents
its denominator is worse than one that says it has none. A pattern that runs out of time, or a
`budget` group that captures anything but a whole number of minutes, refuses the command as
configuration rather than being read as no match, or as no budget, either of which would let a red
leg pass unseen.

## Exit codes

`0` always means success. "The thing you asked about failed" never shares a code
with "the harness could not run", because the remedies differ.

| Code | Meaning |
|---|---|
| 0 | Success |
| 1-9 | Reserved for per-command meanings (e.g. `verify-git`) |
| 10 | Usage error |
| 11 | Not initialised |
| 12 | Invalid configuration |
| 13 | Refused: precondition not met (dirty tree, lock held, name taken, a host runs a newer DssHarness) |
| 14 | A required tool is missing, or could not be started |
| 15 | A host could not be reached, DssHarness could not run there, or a command run there never reported how it finished |
| 20 | The wrapped command ran and failed |
| 21 | Ran with nothing failing, but a leg reached no verdict; it is not a pass |
| 70 | The harness itself failed unexpectedly (a defect in the tool) |
| 130 | The run was interrupted before it finished; what it had already done is still reported |

`verify-git` keeps its own contract: `0` success, `1` git not installed,
`2` not a git repository. `legs` exits `1` when a leg named with `--legs` cannot run,
or when no selected leg can, and `70` when whether a leg can run was never established, through
a defect in this tool. `install-missing-tools` exits `1` when a tool is missing, out of
date or could not be installed, and `15` when a host could not be reached: a tool that is not
there and a host that did not answer call for different things. `check-anchor-balance` and
`check-anchor-citations` exit `1` on a finding, which is what they were asked to look for rather
than a failure of the command. `check-ci-legs` exits `1` when a leg is red and `2` when no job
is a leg - the matrix did not run, or `legJobPattern` matches none of its jobs - an empty answer
indistinguishable from every leg passing, and never read as one. `host-exec` returns the exit code of the command it ran on the host,
unchanged, or 15 when that command never reported how it finished.
`dssharness help exit-codes` prints the shared table from the code itself; this copy, and the
per-command codes above, are maintained by hand.

Commands that run legs (`build`, `run`, `test`) use four codes from the range reserved for
command contracts, because each calls for a different remedy:

| Code | Verdict | Remedy |
|---|---|---|
| 3 | `inputs-moved` or `unmeasured` | Let the tree settle, then run again |
| 4 | `contended` | Wait for the other run |
| 5 | `unwitnessed` | Find out what actually ran |
| 6 | `log-held` | Find out which run still owns this leg's logs |

`failed` reports 20, `refused-locked` 13 and `poisoned` 70. When legs disagree, the
more fundamental verdict decides the code, in the order given under *Verdict vocabulary*.
Five outcomes therefore carry five codes — refused before starting, the tree moved under the
run, another run in the build directory, another run holding the logs, and a zero exit code
with no witness — because a reader who cannot tell which fired cannot pick the remedy.

## Success witnesses

A zero exit code is not proof a command ran. A test invocation must declare a
`successPattern`, and a runner phase may; where one is declared, the work passes only
if the exit code is zero **and** the pattern matches the command's own output. This
exists because a wrapper that reports success without evidence is indistinguishable
from one that never ran, and it was measured happening three separate ways: a suite
that printed `failed=0` while exiting 2, an exit code read after a pipe, and a test
command that exited 0 having run no tests at all. An emulator's witness applies the same
rule to the emulator itself.

## Timeouts

Wall-clock timeouts are not used **for phases and legs**: a time budget is a guess
about workload size, and honest runs exceeding it get killed. Where a bound is
needed, a phase declares a **stall** bound instead — no output for N seconds means
hung — because output cadence stays stable even when total duration is not.

`IProcessRunner` does support a per-process budget, for a probe that must not hang: the
probes that measure a host, and an emulator's witness, each have one.
