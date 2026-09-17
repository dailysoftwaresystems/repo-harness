# DssHarness

One cross-platform CLI for a repository's worktrees, builds, tests and cross-host
work — configured entirely from a file in the repository, not compiled into the tool.

```bash
dotnet tool install --global DssHarness
DssHarness --help
```

## Why

A repository's build and test work usually accumulates as a pile of paired
`.sh`/`.ps1` scripts. They drift apart, they encode one repository's facts, and they
fail differently on every platform — a Windows path limit here, a frozen macOS bash
there, a missing `rsync` somewhere else.

`DssHarness` replaces that with a single tool whose behaviour is declared in
`.harness-config/config.json`. **Nothing about a specific repository, language or
toolchain is built into the tool.** If a behaviour cannot be expressed in
configuration, that is a defect.

## Getting started

```bash
DssHarness verify-git     # is git installed, and is this a repository?
DssHarness init           # create .harness-config and seed config.json
DssHarness legs           # where each leg can run, or why it cannot
DssHarness help           # reference material: exit codes, config, legs, layout
```

`init` inspects the repository and seeds a configuration that already matches it —
a CMake project gets toolchains and `ctest`, a .NET solution gets `dotnet test` — with
legs for the operating system and processor of the machine it ran on. With no project
detected it seeds no legs, and `legs` fails until some are declared.

## Commands

| Command | Does |
|---|---|
| `init` | Create `.harness-config`, seed `config.json`, add ignore rules |
| `verify-git` | Check git is installed and this is a repository |
| `create-worktree <name>` | Create a worktree (`--random` generates the name) |
| `delete-worktree <name> [--force]` | Remove a worktree and everything under it; refuses one holding work that would be lost, a locked one, or one whose evidence directories hold measurements, without `--force` |
| `list-worktree` | List existing worktrees with the commit each was made from |
| `check-root-litter` | Report files left loose at the root of the checkout, ignored ones included |
| `write-anchor <id> --priority P --trigger TEXT` | Add an anchor: to the pending registry, or to done when closed |
| `set-anchor <id>` | Change an anchor; changing its status moves it between registries |
| `read-anchor <id>...` | Show anchors in full |
| `read-anchors` | List anchors, or check the registries with `--lint` |
| `check-anchor-balance` | Fail a change that leaves more open anchors than it found |
| `check-anchor-citations` | Check every anchor cited in the declared roots resolves to a row |
| `fix-line-endings [--all \| --changed]` | Apply the line-ending policy `.gitattributes` declares; `--check` refuses instead |
| `check-ci-legs` | Report each CI leg, separating a real failure from a budget overrun |
| `legs [--legs a,b]` | Measure the hosts and show where each leg can run, or why it cannot |
| `install-missing-tools [--legs a,b]` | Install or update what each configured leg's host is missing |
| `sync` | Put a host's copy of the repository in step with this tree, deletions included |
| `build [--legs a,b] [--time]` | Build every selected leg, in its own variant-keyed build directory |
| `test [--legs a,b] [--time]` | Build and test every selected leg, with a witness for each verdict |
| `run <runner> [--legs a,b] [--time]` | Run a predefined runner across the legs it declares |
| `host-exec --ssh <name> \| --wsl [<distro>] -- <command>` | Run a DssHarness command on an ssh host or in a WSL distribution |
| `help [topic]` | Explain exit codes, configuration, legs, worktrees, anchors, layout, secrets, runners |

Every command takes `-C, --directory <dir>` and `-v, --verbose`.

Zero means every selected leg reached a verdict and none failed. A run where nothing failed but
some leg never reported exits `21` and names those legs: a leg that did no work proves nothing
about the code, so it is never counted among the legs that passed.

`--time` pulls each phase's own timing marks out of its output, using `buildTimingRegex`,
`testTimingRegex` or `runTimingRegex`, and prints them under the ledger as a `TIMINGS` block naming
the leg and phase that reported each one. `host-exec` needs no `--time` of its own: everything after
`--` is the command run on the host, so `host-exec --ssh vps -- test --legs a,b --time` asks for it
there.

## Running legs at once

Every leg-running command dispatches its legs in parallel and waits for all of them, reporting each
one live rather than buffering until the end. Output is written a whole line at a time, and every
line says which leg it came from — a streamed child line under `-v` is tagged `<leg>/<phase>:`, so
three hosts building at once stay readable.

Legs are chunked by the **physical machine** they run on. A local leg and every WSL leg are one
machine, because WSL runs on it; each ssh host is its own. `defaults.maxParallelLegs` caps how many
run at once *on any one machine*, so a busy laptop is not asked for more than it has while the
remote hosts sit idle, and `defaults.maxParallelLegsTotal` caps the whole fleet for what it shares
even when its machines do not — a license server, a network share, a sync's bandwidth.

## Design

Three principles the implementation actually holds to:

**Fail loud.** Zero always means success, and "the thing you asked about failed"
never shares an exit code with "the harness could not run" — the remedies differ.
Run `DssHarness help exit-codes` for the full table, which is generated from the
code rather than written by hand. A configuration file with an unknown key or a
reference to something undeclared is rejected when it is read, with every problem
listed at once.

**One behaviour everywhere.** Windows, macOS and Linux run the same code path. The
operating system is observed in two tightly scoped places and nowhere else;
everything downstream is platform agnostic. CI runs the whole suite on all three, on
both x86_64 and arm64.

**Witness the work, never the exit code.** A build passes only when every file the project's
`buildOutputs` names is there afterwards — a build tool exits 0 having produced nothing often
enough that the code alone is not evidence. An entry is a path, or a mapping of platform to path
where the platforms disagree about what the same target is called (`app` against `app.exe`), and
an entry naming no path for a platform some leg builds on is refused when `config.json` is read.

**Refuse early, with the arithmetic.** `create-worktree` will not create a worktree
whose build paths cannot fit inside Windows' path limit, because that failure
otherwise appears as compile errors in files the worktree never touched. It says how
long a name would fit instead. The name limit and both sides of the budget are set
in `config.json`.

## Layout

```
.harness-config/config.json                  tracked; the whole contract
.harness-config/runner/actions/<name>/<name>.yml   tracked; one directory per action
.harness-config/runner/actions/<name>/...          tracked; whatever its steps run
.harness-config/runner/.env/                 contents ignored, .gitkeep tracked
.harness-config/runner/.secrets/             contents ignored, .gitkeep tracked
.harness-config/sshItems/<name>/.env         ignored; address, user, port
.harness-config/sshItems/<name>/.key         ignored; the private key
.harness-config/wslDistros/<name>/.env       ignored; the distribution and its credential
.harness-config/worktrees/                   ignored whole, never a placeholder; created on first use
.harness-config/runs/                        ignored; one directory of logs per run
.harness-config/lock.json                    ignored; records in-progress runs
.plans/_deferred-anchor-registry.md          tracked; live anchors
.plans/_deferred-anchor-registry-done.md     tracked; closed anchors
```

The worktrees root is `worktrees.root` in `config.json`, shown here at its default. A
repository whose build paths are long sets a shorter one, such as `.worktrees`, which buys
back the characters the default spends before a worktree's own name.

Ignored state lives only in the main checkout. A worktree receives the tracked part
of `.harness-config` through git but never the ignored part, so secrets and the run
lock resolve back to the originating checkout.

## Legs and hosts

A leg says what it needs, never where it runs: an operating system, a processor, and
optionally the emulator it runs through, which rules out running it natively. Hosts are
measured before anything starts: this machine first, then WSL distributions and ssh hosts
only for the legs this machine cannot run, and a WSL distribution only for a Linux leg.
Each leg runs on the first host that provides what it needs.

```json
{
  "hosts": {
    "wsl": { "Ubuntu": { "repositoryPath": "~/src/app" } },
    "ssh": { "mac-mini": { "repositoryPath": "/Users/dev/src/app" } }
  },
  "emulators": {
    "rosetta": {
      "hostOs": "macos", "hostProcessor": "arm64", "processor": "x86_64",
      "launcher": ["arch", "-x86_64"],
      "witness": { "command": ["/usr/bin/uname", "-m"], "pattern": "^x86_64$" }
    }
  },
  "legs": {
    "linux-release": { "os": "linux", "processor": "x86_64", "config": "release" },
    "mac-x64-release": { "os": "macos", "processor": "x86_64", "emulator": "rosetta", "config": "release" }
  }
}
```

```bash
DssHarness legs                                       # every leg: where it runs, or why it cannot
DssHarness legs --legs linux-release,mac-x64-release
DssHarness host-exec --ssh mac-mini -- verify-git
```

An ssh host's connection data lives in its own directory under `.harness-config/sshItems/`,
which git ignores: an `.env` naming the address, the user and the port, a `.key`, and a
`known_hosts`. `config.json` declares only the directory names, under `sshItems`, so the
tracked configuration never carries an address, a user, a key path or a credential. A WSL
distribution has the same shape under `.harness-config/wslDistros/`. ssh runs in batch mode,
so it never waits at a prompt, and a key other users can read is refused before anything
connects, with the command that fixes it.

Every WSL distribution and ssh host runs DssHarness itself, installed from nuget.org at
this machine's exact version: a host that is behind is updated, never downgraded. Betas are
released on GitHub rather than nuget.org, so only a stable build can bring a host to its
version. `install-missing-tools` installs the .NET SDK, and everything `tools` declares an
`install` for, on every configured leg's host; a `tools` entry with no `install` is an
allowlist entry, reported when missing and never installed. An install that needs a superuser
takes the password from that host's own `.env`, as `SUDO_PASSWORD`, and asks for it at the
terminal when none is declared — once per host, held in memory for that command alone and never
written anywhere. A run with no terminal, a run answering with `--json`, and a run given
`--no-prompt` all refuse instead, naming what would fix it; running the harness as root needs no
password at all, which is usually the answer in CI. An entry may name the platforms it
is needed on — `"platforms": ["windows"]` — and a host whose platform it does not name is never
asked about it, so a repository can declare both a Windows compiler and a POSIX one. `sync` creates the host's copy
of the repository at its `repositoryPath` and keeps it in step, deletions included. An
emulator counts only once its witness proves it runs programs for its processor.

`legs` runs the witness of each emulator the selected legs use, and both commands install
or update DssHarness on the hosts they reach, as `config.json` declares. That is the
trust building the repository already asks for. Run `DssHarness help legs` for the rules.

## Anchors

An anchor is a named piece of deferred work, kept as a row in a markdown registry so it
cannot be forgotten. Two registries hold every anchor: pending, for open, gated and
disclosed anchors, and done, the archive of closed ones. Their paths are set in
`config.json` under `anchors`, and `init` creates each one that is missing.

```bash
DssHarness write-anchor D-AUTH-TOKEN-REFRESH --priority P1 --trigger "tokens expire mid-request"
DssHarness set-anchor D-AUTH-TOKEN-REFRESH --status closed   # moves it to the done registry
DssHarness read-anchor D-AUTH-TOKEN-REFRESH
DssHarness read-anchors --open --band P0 P1
DssHarness check-anchor-balance --base main
```

The Status cell (`🟠 OPEN`, `⏳ GATED`, `🔵 DISCLOSED`, `✅ CLOSED`) is the only verdict a
row carries. Closing an anchor moves its row to the done registry, and
`check-anchor-balance` fails a change that leaves more open anchors than it found. Run
`DssHarness help anchors` for the rules.

## Building from source

```bash
dotnet build RepoHarness.slnx
dotnet test --project tests/repo-harness-test/repo-harness-test.csproj
```

Everything a build produces — binaries, intermediates and packages — lands under
`build/`, which git ignores. Deleting it is a complete clean.

## Documentation

- [Architecture](docs/architecture.md) — layering, paths, hosts and legs, parallel execution, contamination guarantees
- [Releasing](docs/releasing.md) — channels, trusted publishing, the release pipelines

## License

repo-harness is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE)
and [NOTICE](NOTICE).
