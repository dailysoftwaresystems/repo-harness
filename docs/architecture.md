# repo-harness architecture

## Why this exists

A repository's build, test and cross-host work is usually a pile of paired
`.sh`/`.ps1` scripts that drift apart, encode one repository's facts, and fail
differently on each platform. `repo-harness` replaces that with one
cross-platform .NET tool whose behaviour is driven entirely by `config.json`.

**Nothing about any specific repository, language or toolchain is compiled in.**
If a behaviour cannot be expressed in `config.json`, that is a defect.

## Status

Implemented today: `init`, `verify-git`, `create-worktree`, `delete-worktree`,
`list-worktree`, the anchor commands (`write-anchor`, `set-anchor`, `read-anchor`,
`read-anchors`, `check-anchor-balance`) and `help`.

Everything from **Targets, trees and legs** onward is the design the remaining
commands — sync, build, run, test and the SSH commands — are built to. It is written
in the present tense because it is the contract those commands must satisfy, not a
description of code that already exists.

## Layering

```
RepoHarness.Cli        Program.cs: argument parsing and dependency wiring only
RepoHarness.Core       domain, services, abstractions
repo-harness-test      tests

(RepoHarness.Adapters arrives with the first build adapter; an empty project would
ship an empty assembly inside the published tool.)
```

`Core` is a library, so "no logic in Program.cs" is enforced by the assembly
boundary rather than by discipline.

### The platform layer

`HostPlatform` and `FilePermissionsFactory` are the **only** types that observe
the operating system. Everything else depends on `IHostPlatform`,
`IFilePermissions`, `IProcessRunner` and `IFileSystem`.

A platform-specific implementation is created only where behaviour genuinely
differs. Today that is one family of behaviour, file permissions: making a file
readable only by its owner, and deciding whether a file is a program at all. Unix
answers both with mode bits; Windows answers them with access lists and file
extensions. Hence a POSIX implementation and a Windows implementation, and no third.

### Process execution

`IProcessRunner` takes arguments as a **list**, never a command line string.
Shell quoting is therefore not part of the system. This is not a style
preference: a neighbouring repository measured a shell-string invocation whose
empty variable expanded into `rsync -a --delete / /`, ran for 70 minutes and
reported exit 0.

Child output is decoded as UTF-8 on every platform. Left to its default, Windows
decodes redirected output with the console's legacy code page, so a path such as
`C:\Users\João` printed by git would reach the harness garbled on Windows alone.

A missing working directory is reported as exactly that. Linux and macOS report it
with the same error number as a missing executable, which would otherwise surface as
"git is not installed".

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

git's messages are matched in one place only: telling "not a repository" apart from
"git could not look". Those read-only queries run with `LC_ALL=C`, so a translated git
still answers in the words being matched. Every other git command, including those that
run the user's hooks, keeps the user's locale.

### Reading config.json

The file is edited by hand, so it is checked as it is read, and every problem is reported
at once, with the line it concerns where the parser knows it:

- An unknown key is an error. A misspelled setting that is silently dropped reverts to
  its default while the file plainly appears to set it.
- `null` is refused wherever the model does not allow it, including inside lists and
  maps, which the serializer does not check on its own. Loaded, it would fail much later
  as a crash in whatever first read it.
- References are resolved: a leg naming an undeclared target, a test aimed at an ssh
  target that does not use ssh, a success pattern that does not compile.

It is written with LF line endings and no byte order mark on every platform. The file
is tracked, and its bytes must not depend on which machine ran `init`.

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

The Status cell is the only verdict: `🟠 OPEN`, `⏳ GATED`, `🔵 DISCLOSED` or `✅ CLOSED`. A
row is closed exactly when its Status cell starts with ✅, and every other glyph, including
one nobody anticipated, reads as open: a row wrongly read as open stays visible as work,
while a row wrongly read as closed disappears from every count. Nothing is inferred from
the prose cells.

### Writing a row

Commands take fields, never rows. Line breaks collapse and pipes are escaped, because a raw
`|` adds a column and shifts every later cell, and a wrapped row hides its id from every
search. A value that already holds an escaped pipe is refused: escaping it again would
double the backslash, and it usually means someone copied a raw table line. Every composed
row is read back through the same parser before anything is written.

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

## Targets, trees and legs

Three concepts that are frequently conflated, kept separate here:

| Concept | Question it answers | Values |
|---|---|---|
| **Transport** | How do I reach and execute on this machine? | `local`, `wsl`, `ssh` |
| **Tree** | Which checkout am I acting on? | main checkout, a worktree, a remote path |
| **Leg** | One unit of work with a verdict | `(target, project, toolchain, config, sanitizer?)` |

A worktree is **not** a transport. It is a tree, and it composes with any
transport. Keeping these apart is what lets one code path serve
`build`, `build --worktree wt-a` and `build --ssh vps` without special cases.

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
| `skipped-unavailable` | Target not available on this host | warning |
| `skipped-tool-missing` | A required tool is not installed | warning |
| `refused-locked` | Another run holds the lock for this leg | **yes** |
| `poisoned` | The harness could not produce a verdict | **yes** |

`failed` and `poisoned` are deliberately distinct: "your code is broken" and
"the harness broke" call for different responses. `inputs-moved`, `unmeasured` and
`contended` say nothing about the code at all: the first two call for letting the tree
settle and running again, the third for waiting for the other run.

When several apply, the more fundamental one is reported: `poisoned`, then
`unmeasured`, `inputs-moved`, `contended`, `refused-locked`, `failed`, and
`unwitnessed`. A leg whose inputs moved is not reported as failed even if its tests
failed, because what failed was a tree that never existed.

## Parallel execution

A command that selects several legs starts them together and waits for **every**
one to finish before it reports. Legs are isolated from one another (see below), so
running them one at a time is never needed for correctness, and doing so would only
make a gate slower. `defaults.maxParallelLegs` caps how many run at once on a busy
machine; left unset, every selected leg starts immediately.

Within a leg the order is fixed:

```
sync (when the target needs it)  →  build on buildCores  →  test on testCores
```

- **A tree is synced once, not once per leg.** Legs on the same target share one
  tree; if each synced it, their copies would race over the same files. The legs
  sharing a tree wait for its single sync, then build and test in parallel, which is
  safe because each variant has its own build directory.
- **Cores are configured, never "all of them".** `defaults.buildCores` and
  `defaults.testCores` both default to 6. A target replaces them with its own
  `buildCores` and `testCores`, because a remote host rarely has the same core count
  as the machine that wrote the configuration, and a test invocation can replace the
  test count again with its own `cores`.

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
- Every run has its own id, and every log is scoped to it. No two legs ever write to one
  file, so one leg's result can never be read as another's.
- Executables are resolved on the target before a leg starts, so a missing tool is
  `skipped-tool-missing` and named, not a failure halfway through.

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

### Clocks are never trusted to order anything

One host this tool must serve has a wall clock that steps forward by about 25 seconds,
for about 200 milliseconds, every few seconds, and the steps reach file modification
times: a file written one second after a marker carried a timestamp 24 seconds before
it. So:

- Nothing compares two timestamps taken at different moments or on different hosts.
  Change is detected by equality, as above, which a clock cannot distort because both
  readings carry the same distortion. Sync decides what to delete by comparing
  manifests, never by stamp order.
- Durations come from the monotonic clock. UTC times are for display only.
- Each phase compares elapsed wall-clock time with elapsed monotonic time. Drift beyond
  `defaults.clockStepToleranceMilliseconds` records a clock step or a host sleep inside
  that phase: its durations are suspect, and so is every timestamp it wrote.
- Incremental builds are protected from it. Ninja, Make and MSBuild decide what is
  stale by ordering timestamps, which a stepped clock defeats without a word: an object
  stamped during a forward step looks newer than a source edited just after it. The
  harness records a content fingerprint of each variant's inputs with its last
  successful build. Before building again, if a changed input is not newer than the
  newest output, or the previous build spanned a clock step, that variant is rebuilt
  from clean and the ledger says why. A stale binary reported as a pass is the one price
  an incremental build must never pay.

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
- No verdict depends on a sample finishing within a time window. Sampling costs
  seconds on one platform and a fraction of that on another, and one such overhead
  asymmetry was once read, for a whole cycle, as a speed difference between legs.
- A process is identified by its id together with its start time, and a parent link is
  followed only when the parent started no later than the child. Process ids are
  recycled: on Windows a freed id was measured coming back after about a hundred
  allocations.
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
- A leg's environment is carried to its target explicitly. WSL passes on only the
  variables named in `WSLENV`, and ssh passes on none.
- `countPattern` extracts how many tests each leg ran. Legs running the same tests that
  report different counts are flagged: a platform that quietly skips a group of tests
  passes on less evidence than its siblings.
- The ledger reports command time and harness overhead (sync, fingerprints, sampling)
  separately. A phase slower than `defaults.durationWarningFactor` times the same phase
  on sibling legs, or times its own recent runs, is marked suspect. A timing mark never
  changes a verdict.
- `keepAwake` holds a target awake for the leg. A host that slept once reported a
  4 millisecond test at 729 seconds. Without it, timings from a host that can sleep are
  marked suspect.

### Transports and trees

- An ssh target bounds how long a connection may take to open and how long it may go
  unanswered (`connectTimeoutSeconds`, `keepAliveSeconds`). Without both, a dead link
  hangs a leg indefinitely, with no output and no verdict.
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

1. **Variant-keyed build directories.** `<tree>/build/<toolchain>-<config>[-<sanitizer>]`.
   CMake refuses a compiler change on an existing cache, so `msvc` and `gcc`
   cannot share `build/release`.
2. **Tree-rooted paths.** Every path derives from the leg's tree root, so a
   worktree's build output can never land in the main checkout's.
3. **Build directory guard.** Before configuring, `CMakeCache.txt` is read and
   the run is refused if `CMAKE_HOME_DIRECTORY` or the recorded compiler
   disagrees with this leg.
4. **Per-target compiler cache.** `CCACHE_DIR` and `CCACHE_BASEDIR` are set
   explicitly per target rather than inherited, so hosts never share a store.
5. **Clean run directories, incremental build directories.** Scratch and run
   directories are wiped before every leg; build directories are preserved.
6. **Locking.** See below.
7. **One sync per tree.** Legs sharing a target tree share its single sync instead
   of each writing the same files at the same time.

### Incremental builds across a transport

Existing tooling forces a clean rebuild after every remote sync, because
`rsync -a` and `tar` preserve mtimes: a synced source whose mtime lands behind
an existing object file makes Ninja skip the rebuild and report a stale binary
as success.

`repo-harness` syncs by **content hash**, writing only files whose content actually
changed. An unchanged file is not touched, so its mtime does not move; a changed
file is rewritten now, so its mtime advances. Ninja's incremental check is therefore
correct after a sync, and incremental builds are preserved across every transport.

That holds while the target's clock is honest. *Clocks are never trusted to order
anything*, under Leg integrity, covers what the harness does when it is not.

### Locking

`.harness-config/lock.json`, always in the **main checkout** (a worktree has its
own `.harness-config`, so a per-tree lock would make two runs of the same leg
invisible to each other). Gitignored.

One entry per `(target, tree, variant)`, recording host, pid, process start
time, run id, UTC timestamp and the command.

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
- Process start time is recorded alongside the pid so a recycled pid is not
  mistaken for a live holder.

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
vps-arm64-gcc-rel     skipped-unavailable      host not reachable
```

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
| 13 | Refused: precondition not met (dirty tree, lock held, name taken) |
| 14 | A required tool is missing |
| 20 | The wrapped command ran and failed |
| 70 | The harness itself failed unexpectedly (a defect in the tool) |
| 130 | The run was interrupted before it finished |

`verify-git` keeps its own contract: `0` success, `1` git not installed,
`2` not a git repository. `repo-harness help exit-codes` prints this table from
the code itself; this copy is maintained by hand.

Commands that run legs (`build`, `run`, `test`) will use three codes from the range
reserved for command contracts, because each calls for a different remedy:

| Code | Verdict | Remedy |
|---|---|---|
| 3 | `inputs-moved` or `unmeasured` | Let the tree settle, then run again |
| 4 | `contended` | Wait for the other run |
| 5 | `unwitnessed` | Find out what actually ran |

`failed` reports 20, `refused-locked` 13 and `poisoned` 70. When legs disagree, the
more fundamental verdict decides the code, in the order given under *Verdict vocabulary*.

## Success witnesses

A zero exit code is not proof a command ran. A test invocation must declare a
`successPattern`, and a runner phase may; where one is declared, the work passes only
if the exit code is zero **and** the pattern matches the command's own output. This
exists because a wrapper that reports success without evidence is indistinguishable
from one that never ran, and it was measured happening three separate ways: a suite
that printed `failed=0` while exiting 2, an exit code read after a pipe, and a test
command that exited 0 having run no tests at all.

## Timeouts

Wall-clock timeouts are not used **for phases and legs**: a time budget is a guess
about workload size, and honest runs exceeding it get killed. Where a bound is
needed, a phase declares a **stall** bound instead — no output for N seconds means
hung — because output cadence stays stable even when total duration is not.

`IProcessRunner` does support a per-process budget, for a probe that must not hang.
