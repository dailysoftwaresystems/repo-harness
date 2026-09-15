# Releasing repo-harness

`repo-harness` ships as a public .NET tool on nuget.org, under the Apache License 2.0:

```bash
dotnet tool install --global RepoHarness
```

## Security posture

This repository is public, so the pipelines are built to need **no stored secrets**:
the only credentials involved are the automatic `GITHUB_TOKEN` and a short-lived
nuget.org key minted through OIDC at publish time.

- **No stored NuGet key.** Publishing uses nuget.org [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the
  package job exchanges the workflow's short-lived GitHub OIDC token for a
  temporary API key at publish time. There is no long-lived credential to leak,
  rotate, or accidentally commit.
- **No organisation secrets.** The workflows are self-contained and deliberately do
  not call the reusable workflows in the private DevOps repository, and never
  use `secrets: inherit`. A public repository that inherits organisation secrets
  exposes them to every workflow run, which is precisely what must not happen here.
- **Pull requests cannot reach a credential.** `pipeline-pr.yml`, and the `test.yml`
  matrix it calls, declare `permissions: contents: read`, use no secrets, and check out
  without leaving a credential in the working copy, so a pull request from a fork runs
  the full test matrix with nothing to steal. `pull_request_target`, which would run
  fork code with repository permissions, is not used anywhere. The only other
  pull-request-triggered workflow, `cleanup-cache.yml`, asks for `actions: write` and is
  skipped outright for forks, which GitHub would issue a read-only token for regardless.
- **Publishing is gated.** The package job runs in the `nuget` environment, so
  approval and branch restrictions apply to the one job that can change what the
  public installs.

The only repository configuration required is one **variable** (not a secret):

| Kind | Name | Value |
|---|---|---|
| Variable | `NUGET_USER` | The nuget.org account that owns the trusted publishing policy |

## One-time setup

1. **On nuget.org**, sign in as the owner of the `RepoHarness` package ID and add a
   trusted publishing policy for this repository:
   - Repository owner and name: this repository
   - Workflow file: `pipeline-pkg.yml`
   - Environment: `nuget`
2. **In this repository's settings**, add the repository variable `NUGET_USER` with
   that nuget.org account name.
3. **Create the `nuget` environment.** Limit its deployment branches to `release/*`:
   with the branch check inside the pipeline, that makes a release branch the only place
   a publish can start from. Add whatever approval the release should require.
4. **If `main` is protected**, let GitHub Actions push to it, for example as a bypass in
   the branch ruleset. The beta channel commits the version bump to `main`, and a push
   the protection refuses stops the release before anything is promoted.

Until step 1 exists on nuget.org, the publish step fails closed: nothing is
published and no credential is created.

## Channels

`repo-harness` is a **LIB** — other people consume it as a package — so it uses the
three-channel chain. `rc` exists so a consumer can integration-test before `lts`.

```
main  ──deploy beta──▶  release/beta  ──deploy rc──▶  release/rc  ──deploy lts──▶  release/lts
                             │                            │                            │
                        0.2.0-beta.N                 0.2.0-rc.N                      0.2.0
```

`beta` and `rc` publish prereleases; `lts` publishes the bare version. The run
number is part of a prerelease so repeating a channel produces a new, ordered
version rather than colliding with what is already published — a collision would
otherwise be skipped as a duplicate and report success while publishing nothing.

## Cutting a release

Run the **Deploy** workflow from the Actions tab.

| Channel | Run from | What it does |
|---|---|---|
| `beta` | `main` | Tests `main`, bumps `<Version>` on the tested commit, moves `main` and `release/beta` to it |
| `rc` | anywhere | Tests `release/beta`, moves `release/rc` to it |
| `lts` | anywhere | Tests `release/rc`, moves `release/lts` to it |

`beta` accepts an optional explicit version; without one it bumps the patch. A
major or minor change is always a deliberate decision, so it must be passed
explicitly. `rc` and `lts` refuse an explicit version: they promote exactly what
`beta` already published.

Deploy runs as three jobs:

1. **plan** validates the request and pins the source branch's current commit.
2. **test** runs the three-OS matrix — `test.yml`, the same gate every pull request
   passes — on that pinned commit.
3. **promote** fast-forwards the release branch to the tested commit, then starts
   **Package Pipeline** for that branch, naming the exact commit it promoted.

Package Pipeline builds, runs the suite on the commit being published, packs, verifies
that the packed tool installs from the local package alone and reports the version it
was packed as, creates the immutable tag, publishes to nuget.org, and creates the
GitHub release with the `.nupkg` attached.

Deploy starts Package Pipeline explicitly rather than relying on the push to the
release branch. GitHub starts no workflow from a push made with the `GITHUB_TOKEN`, so
a push trigger would never fire for Deploy's pushes, and every release would promote
and then publish nothing. A workflow dispatch is the one event that token may start.
Package Pipeline can also be started by hand for a release branch, for example to retry
a publish after fixing the trusted publishing policy.

The tag is created **before** publishing: a tag is cheap to delete, a published
NuGet version is permanent. Package Pipeline tests on Linux only; the three-OS matrix
has already passed for that commit in Deploy.

## Refusals worth knowing

The pipelines fail loudly rather than reporting a green run that shipped nothing, or
shipped something that was never tested:

- **Nothing to promote.** A promotion that brings no commits across pushes nothing, so
  nothing would be published. Deploy checks first and refuses instead.
- **Release branch diverged.** A release branch only ever fast-forwards to a tested
  commit. One holding commits its source lacks is refused, because merging would put a
  tree on it that the matrix never tested; a person reconciles it.
- **The branch moved during the release.** For beta the bump is pushed as a
  fast-forward of the tested commit, so if `main` moved while the matrix ran, git
  rejects the push and nothing is promoted. For every channel, Package Pipeline refuses
  to publish a release branch that no longer points at the commit Deploy promoted.
- **Not a release branch.** Package Pipeline publishes from `release/beta`,
  `release/rc` and `release/lts` only.
- **Tag already exists.** A published version is never rewritten.
- **Version unchanged.** A bump that changes nothing is refused before it commits.
- **Wrong branch.** `beta` is cut from `main` only.
- **Version moves backwards.** A lower version publishes successfully but sorts below
  what is already released, so nobody can resolve it — a green run that shipped
  nothing installable. Refused before the bump commits.
- **Nothing was cleaned.** A cache cleanup where every deletion failed fails the job
  rather than reporting a successful cleanup of nothing.

## Pipelines

| Workflow | Fires on | Does |
|---|---|---|
| `pipeline-pr.yml` | pull request, push to `main` | Runs `test.yml`; packs, installs and runs the tool |
| `test.yml` | called by `pipeline-pr.yml` and `deploy.yml` | Builds and tests on Ubuntu, Windows and macOS |
| `deploy.yml` | manual | Tests, bumps the version, promotes, starts `pipeline-pkg.yml` |
| `pipeline-pkg.yml` | dispatched by `deploy.yml`, or manual | Tests, packs, verifies, tags, publishes, releases |
| `cleanup-cache.yml` | pull request closed | Evicts that pull request's caches |

Both pipelines verify a package through one composite action,
`.github/actions/verify-tool-package`, so a pull request and a release check a package
in exactly the same way. Packages are written to `build/package`, inside the `build/`
directory every build writes to and git ignores.
