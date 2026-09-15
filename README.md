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
repo-harness help           # reference material: exit codes, config, layout
```

`init` inspects the repository and seeds a configuration that already matches it —
a CMake project gets toolchains and `ctest`, a .NET solution gets `dotnet test`.

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
| `help [topic]` | Explain exit codes, configuration, worktrees, anchors, layout |

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
everything downstream is platform agnostic. CI runs the whole suite on all three.

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
.harness-config/lock.json                    ignored; records in-progress runs
.plans/_deferred-anchor-registry.md          tracked; live anchors
.plans/_deferred-anchor-registry-done.md     tracked; closed anchors
```

Ignored state lives only in the main checkout. A worktree receives the tracked part
of `.harness-config` through git but never the ignored part, so secrets and the run
lock resolve back to the originating checkout.

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

- [Architecture](docs/architecture.md) — layering, paths, legs, parallel execution, contamination guarantees
- [Releasing](docs/releasing.md) — channels, trusted publishing, the release pipelines

## License

repo-harness is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE)
and [NOTICE](NOTICE).
