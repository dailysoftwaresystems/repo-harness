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

A program named without a path is looked up in the `PATH` directories and nowhere else.
Left to the runtime, it would be looked for beside the running executable and in the
current directory first, and the current directory is usually the repository, so a file
committed there under a tool's name would run in place of the tool. On Windows a name
without an extension starts only `<name>.exe`, never a batch file, whose arguments cmd.exe
would parse a second time.

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
avoidable.

**The root is ignored whole, and never holds a placeholder.** `init` writes `/<root>/` for it and
creates nothing there; `create-worktree` makes the directory the first time it needs it. The other
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
these paths by hand keeps its rule beside the managed one. `init` reports each such pair as a note,
naming the line and saying whether the two rules repeat each other or point opposite ways, since
whichever of two contradicting rules comes later in the file wins. The comparison is by exact path
after dropping a leading `!`, one anchoring `/`, a trailing `/*` and a trailing `/`; a rule that
reaches a managed path only through a wildcard is not reported.

`create-worktree` records the commit a worktree was made from, under
`refs/harness/worktree-base/<name>`, and `list-worktree` reports it. A worktree's own HEAD moves
with every commit made in it, so after the first one nothing else says what tree the lane started
from, and the lane can only be reproduced from the moment it happened to be made. The record is
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
  the deletion and names it. A lane's measurements live in an ignored directory precisely because
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

### Citations

`check-anchor-citations` requires every anchor id cited in a **scanned root** to resolve to a
row in either registry. The roots are `anchors.citationRoots`, and nothing outside a declared
root is scanned: which code is production code is a judgement a repository makes, not one a
tool can infer. An empty list scans nothing and the command says so, rather than reporting a
pass over a check that looked at no file. `--current-commit` reads HEAD, `--current-tree` reads
the disk, `--current-pr` reads only what this branch changed.

Resolution is by substring, so a row naming a more specific child answers a citation of its
parent.

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
  measured only for the legs it cannot run, and all at once, so an unreachable host costs
  its connect timeout once.
- A leg runs on the first candidate whose measured operating system matches and whose
  processor matches, or, for an emulated leg, where that emulator's check passed.
- `--legs` takes leg names and leg set names, separated by commas or spaces. A name that is
  neither is a usage error before any host is measured. No `--legs` selects every leg, and
  `--legs` given without a name is a usage error rather than every leg, so an empty
  variable cannot pass a gate. A leg or leg set name holding a comma or a space could never
  be selected, so the file refuses one.
- A leg no host can run is a warning naming the leg and each candidate's reason, and the
  other legs still go ahead. The check fails, and `legs` exits 1, when a leg named with
  `--legs` cannot run, or when no selected leg can: a declared leg on a machine that is
  switched off is normal, and a leg asked for by name is not.

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
  lookup as a matter of course. The lookup is retried and the answer cached briefly, so one
  failed lookup never fails a leg.
- **PATH truth.** A login shell's PATH is not what a command sees: `/opt/homebrew/bin` is absent
  from an ssh command's PATH on macOS, and `~/.dotnet` is in WSL. Programs the harness depends on
  are resolved to an absolute path once per connection, measured rather than assumed, the same
  way the remote shell is. A host whose SDK is installed but off that PATH is reported as exactly
  that, never as "not installed": the remedy differs.
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
other commands share it, and `init` calls it: a fresh clone should be ready to run rather than
ready to be told what is missing.

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
  `.harness-config/sshItems/`, which git ignores, and ssh reads no configuration file of its
  own. A `config.json` that arrives through git cannot point the harness at a machine nobody
  set up here.
- A launcher and a required file are each a program name, looked up on the host's `PATH`,
  or an absolute path. A relative path would resolve against whichever directory a host
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
| `skipped-unavailable` | No host can run the leg | warning |
| `skipped-tool-missing` | A required tool is not installed | warning |
| `refused-locked` | Another run holds the lock for this leg | **yes** |
| `log-held` | Another live run owns this leg's log path | **yes** |
| `poisoned` | The harness could not produce a verdict | **yes** |

`failed` and `poisoned` are deliberately distinct: "your code is broken" and
"the harness broke" call for different responses. `inputs-moved`, `unmeasured` and
`contended` say nothing about the code at all: the first two call for letting the tree
settle and running again, the third for waiting for the other run.

`refused-locked` and `log-held` are deliberately distinct, though both mean another run got
there first. A lock is taken for the duration of the work and is released by the run that
took it; a log path is owned by a run id, and one already owning it means two runs would
write one file and each would read the other's output as its own. The remedies differ — wait,
against find out which run is still holding a finished run's logs — and a reader who cannot
tell which fired cannot pick either.

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
- Every run has its own id, and every log is scoped to it. No two legs ever write to one
  file, so one leg's result can never be read as another's.
- Executables are resolved on the host before a leg starts, so a missing tool is
  `skipped-tool-missing` and named, not a failure halfway through.
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
- `countPattern` extracts how many tests each leg ran. Legs running the same tests that
  report different counts are flagged: a platform that quietly skips a group of tests
  passes on less evidence than its siblings.
- The ledger reports command time and harness overhead (sync, fingerprints, sampling)
  separately. A phase slower than `defaults.durationWarningFactor` times the same phase
  on sibling legs of the same kind is marked suspect. A timing mark never
  changes a verdict. An emulated leg is never compared with a native one.
- `keepAwake` holds a host awake for the leg. A host that slept once reported a
  4 millisecond test at 729 seconds. Without it, timings from a host that can sleep are
  marked suspect.

### Hosts and trees

- An ssh host bounds how long a connection may take to open and how long it may go
  unanswered (`connectTimeoutSeconds`, `keepAliveSeconds`). Without both, a dead link
  hangs a leg indefinitely, with no output and no verdict.
- A host's copy of the repository is a git repository sync creates at its `repositoryPath`: the
  working tree being tested is transferred into it file by file, compared by content hash, so what
  the host holds is this tree including its uncommitted changes. Nothing is pushed and it is never
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
   disagrees with this leg.
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
- **The copy gets `.harness-config/config.json`, and nothing else from that directory.** A leg
  placed on a host runs DssHarness there, and DssHarness in a directory holding no configuration
  refuses as not initialised — so without it the copy is a tree no leg can run in. The rest of the
  directory is connection data, credentials, locks and logs, each local to a machine by design.
- **Deletion is bounded.** A sync that would delete more than `sync.maxDeleteFraction` of the
  copy stops and changes nothing. A mistyped repository path makes the source look empty, which
  is indistinguishable from a source that deleted everything, and without the bound that empties
  a host. A first sync into an empty copy has nothing to measure and is never over it.
- **Deletion stays inside the tree.** Every path is resolved against the copy's declared root and
  refused if it leaves by `..`, by an absolute path, or through a link.
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
  `install-missing-tools` guarantees is installed.
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
or when no selected leg can. `install-missing-tools` exits `1` when a tool is missing, out of
date or could not be installed, and `15` when a host could not be reached: a tool that is not
there and a host that did not answer call for different things. `check-anchor-balance` and
`check-anchor-citations` exit `1` on a finding, which is what they were asked to look for rather
than a failure of the command. `check-ci-legs` exits `1` when a leg is red and `2` when the
matrix did not run at all — an empty answer is indistinguishable from every leg passing, and is
never read as one. `host-exec` returns the exit code of the command it ran on the host,
unchanged, or 15 when that command never reported how it finished.
`DssHarness help exit-codes` prints the shared table from the code itself; this copy, and the
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
