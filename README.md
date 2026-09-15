# repo-harness

One cross-platform CLI for a repository's worktrees, builds, tests and cross-host
work — configured entirely from a file in the repository, not compiled into the tool.

```bash
dotnet tool install --global RepoHarness
repo-harness --help
```

## Why

A repository's build and test work usually accumulates as a pile of paired
`.sh`/`.ps1` scripts. They drift apart, they encode one repository's facts, and they
fail differently on every platform — a Windows path limit here, a frozen macOS bash
there, a missing `rsync` somewhere else.

`repo-harness` replaces that with a single tool whose behaviour is declared in
`.harness-config/config.json`. **Nothing about a specific repository, language or
toolchain is built into the tool.** If a behaviour cannot be expressed in
configuration, that is a defect.

## Getting started

```bash
repo-harness verify-git     # is git installed, and is this a repository?
repo-harness init           # create .harness-config and seed config.json
repo-harness legs           # where each leg can run, or why it cannot
repo-harness help           # reference material: exit codes, config, legs, layout
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
| `delete-worktree <name>` | Remove a worktree and everything under it |
| `list-worktree` | List existing worktrees |
| `write-anchor <id> --priority P --trigger TEXT` | Add an anchor: to the pending registry, or to done when closed |
| `set-anchor <id>` | Change an anchor; changing its status moves it between registries |
| `read-anchor <id>...` | Show anchors in full |
| `read-anchors` | List anchors, or check the registries with `--lint` |
| `check-anchor-balance` | Fail a change that leaves more open anchors than it found |
| `legs [--legs a,b]` | Measure the hosts and show where each leg can run, or why it cannot |
| `host-exec --ssh <name> \| --wsl [<distro>] -- <command>` | Run a repo-harness command on an ssh host or in a WSL distribution |
| `help [topic]` | Explain exit codes, configuration, legs, worktrees, anchors, layout |

Every command takes `-C, --directory <dir>` and `-v, --verbose`.

## Design

Three principles the implementation actually holds to:

**Fail loud.** Zero always means success, and "the thing you asked about failed"
never shares an exit code with "the harness could not run" — the remedies differ.
Run `repo-harness help exit-codes` for the full table, which is generated from the
code rather than written by hand. A configuration file with an unknown key or a
reference to something undeclared is rejected when it is read, with every problem
listed at once.

**One behaviour everywhere.** Windows, macOS and Linux run the same code path. The
operating system is observed in two tightly scoped places and nowhere else;
everything downstream is platform agnostic. CI runs the whole suite on all three, on
both x86_64 and arm64.

**Refuse early, with the arithmetic.** `create-worktree` will not create a worktree
whose build paths cannot fit inside Windows' path limit, because that failure
otherwise appears as compile errors in files the worktree never touched. It says how
long a name would fit instead. The name limit and both sides of the budget are set
in `config.json`.

## Layout

```
.harness-config/config.json                  tracked; the whole contract
.harness-config/worktrees/                   contents ignored, .gitkeep tracked
.harness-config/ssh/                         contents ignored, .gitkeep tracked
.harness-config/ssh/config                   ignored; the ssh hosts --ssh names
.harness-config/lock.json                    ignored; records in-progress runs
.plans/_deferred-anchor-registry.md          tracked; live anchors
.plans/_deferred-anchor-registry-done.md     tracked; closed anchors
```

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
repo-harness legs                                       # every leg: where it runs, or why it cannot
repo-harness legs --legs linux-release,mac-x64-release
repo-harness host-exec --ssh mac-mini -- verify-git
```

An ssh host is a `Host` entry in `.harness-config/ssh/config`, which git ignores, spelt
exactly as `hosts.ssh` declares it, case included; ssh runs in batch mode, so it never
waits at a prompt. Every WSL distribution and ssh host runs repo-harness itself,
installed from nuget.org at this machine's exact version: a host that is behind is
updated, never downgraded. `host-exec` runs in the host's copy of the repository at its
`repositoryPath`, which, until sync can create it, has to be a checkout made by hand. An
emulator counts only once its witness proves it runs programs for its processor.

`legs` runs the witness of each emulator the selected legs use, and both commands install
or update repo-harness on the hosts they reach, as `config.json` declares. That is the
trust building the repository already asks for. Run `repo-harness help legs` for the rules.

## Anchors

An anchor is a named piece of deferred work, kept as a row in a markdown registry so it
cannot be forgotten. Two registries hold every anchor: pending, for open, gated and
disclosed anchors, and done, the archive of closed ones. Their paths are set in
`config.json` under `anchors`, and `init` creates each one that is missing.

```bash
repo-harness write-anchor D-AUTH-TOKEN-REFRESH --priority P1 --trigger "tokens expire mid-request"
repo-harness set-anchor D-AUTH-TOKEN-REFRESH --status closed   # moves it to the done registry
repo-harness read-anchor D-AUTH-TOKEN-REFRESH
repo-harness read-anchors --open --band P0 P1
repo-harness check-anchor-balance --base main
```

The Status cell (`🟠 OPEN`, `⏳ GATED`, `🔵 DISCLOSED`, `✅ CLOSED`) is the only verdict a
row carries. Closing an anchor moves its row to the done registry, and
`check-anchor-balance` fails a change that leaves more open anchors than it found. Run
`repo-harness help anchors` for the rules.

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
