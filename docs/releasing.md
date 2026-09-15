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
  the full test matrix with nothing to steal. The one job with more is `ci-check`, which
  asks for `statuses: write` to post the status merging requires; it checks out nothing
  and runs none of the repository's code. `pull_request_target`, which would run
  fork code with repository permissions, is not used anywhere. The only other
  pull-request-triggered workflow, `cleanup-cache.yml`, asks for `actions: write` and is
  skipped outright for forks, which GitHub would issue a read-only token for regardless.
- **Publishing is gated.** The package job runs in the `nuget` environment, so
  approval and branch restrictions apply to the one job that can change what the
  public installs.
- **Actions are pinned to commits.** Every action a workflow uses is pinned to a full
  commit SHA, with its version in a comment. A tag can be moved to different code after
  the fact, and the package job can mint a key that publishes to nuget.org, so what runs
  there must be exactly what was reviewed. Dependabot (`.github/dependabot.yml`) proposes
  updates, the new SHA and version together.

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

`repo-harness` ships as an **app**: people install the released tool rather than build on
it, so it uses two channels.

```
main  ──deploy beta──▶  release/beta  ──deploy stable──▶  release/stable
                             │                                  │
                        0.2.0-beta                            0.2.0
```

`beta` publishes each new version as a prerelease, `<version>-beta`. `stable` publishes a
version `beta` has already published, as the bare version. Tags and GitHub releases carry
a `v` prefix: `v0.2.0-beta`, `v0.2.0`.

A beta needs no build number to be new. Every beta deploy bumps `<Version>` first, and a
version whose tag or release already exists is refused, so the same version is never
published twice.

## Cutting a release

Run the **Deploy** workflow from the Actions tab.

| Channel | Run from | What it does |
|---|---|---|
| `beta` | `main` | Tests `main`, bumps `<Version>` on the tested commit (committed as `v<version>`), moves `main` and `release/beta` to it |
| `stable` | anywhere | Tests `release/beta`, moves `release/stable` to it |

`beta` accepts an optional explicit version; without one it bumps the patch. A
major or minor change is always a deliberate decision, so it must be passed
explicitly. `stable` refuses an explicit version: it promotes exactly what `beta`
already published.

Deploy runs as three jobs:

1. **plan** validates the request and pins the source branch's current commit.
2. **test** runs `test.yml`, the same gate every pull request passes: Linux, Windows and
   macOS, each on x86_64 and arm64, on that pinned commit.
3. **promote** fast-forwards the release branch to the tested commit, creating it on a
   channel's first release, then starts **Package Pipeline** for that branch, naming the
   exact commit it promoted.

Package Pipeline builds, runs the suite on the commit being published, packs, verifies
that the packed tool installs from the local package alone and reports the version it
was packed as, creates the immutable tag, publishes to nuget.org, and creates the
GitHub release at the promoted commit with the `.nupkg` attached.

Deploy starts Package Pipeline explicitly rather than relying on the push to the
release branch. GitHub starts no workflow from a push made with the `GITHUB_TOKEN`, so
a push trigger would never fire for Deploy's pushes, and every release would promote
and then publish nothing. A workflow dispatch is the one event that token may start.
Package Pipeline can also be started by hand for a release branch, for example to retry
a publish after fixing the trusted publishing policy.

The tag is created **before** publishing: a tag is cheap to delete, a published
NuGet version is permanent. Package Pipeline tests on Linux x86_64 only; the full matrix
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
- **Not a release branch.** Package Pipeline publishes from `release/beta` and
  `release/stable` only.
- **Version already published.** A published version is never rewritten. The tag and the
  GitHub release are both checked, and a check that gets no clear answer refuses too:
  "could not tell" is not "does not exist".
- **Version unchanged.** A bump that changes nothing is refused before it commits.
- **Wrong branch.** `beta` is cut from `main` only.
- **Version moves backwards.** A lower version publishes successfully but sorts below
  what is already released, so nobody can resolve it — a green run that shipped
  nothing installable. Refused before the bump commits.
- **Nothing was cleaned.** A cache cleanup where every deletion failed fails the job
  rather than reporting a successful cleanup of nothing.

## The required check

Merging into `main` waits for one result: the `Pipeline / ci-check result` commit status,
which the organisation's ruleset requires on every pull request. A status, unlike a job's
check, exists only once something posts it. The `ci-check` job in `pipeline-pr.yml` posts
it once the test matrix and the package check have finished: `success` when both
succeeded, and `failure` otherwise, including when either was skipped or cancelled.

- **The job agrees with the status.** `ci-check` fails whenever the status it posts is a
  failure. Ending on the successful post would show a green job beside a red status, and
  people read the job first.
- **A pull request from a fork cannot post it.** GitHub gives a fork's pull request a
  read-only token whatever the workflow asks for, so `ci-check` fails there and says why.
  A maintainer runs the change from a branch in this repository.
- **Read required checks from rulesets.** The requirement is defined in a ruleset, so
  `branches/main/protection` answers that `main` is not protected at all. Ask
  `gh api repos/<owner>/<repo>/rules/branches/main` instead.

## Pipelines

| Workflow | Fires on | Does |
|---|---|---|
| `pipeline-pr.yml` | pull request, push to `main` | Runs `test.yml`; packs, installs and runs the tool; uploads the package, kept for the repository's artifact retention period; posts the `Pipeline / ci-check result` status |
| `test.yml` | called by `pipeline-pr.yml` and `deploy.yml` | Builds and tests on Linux, Windows and macOS, each on x86_64 and arm64 |
| `deploy.yml` | manual | Tests, bumps the version, promotes, starts `pipeline-pkg.yml` |
| `pipeline-pkg.yml` | dispatched by `deploy.yml`, or manual | Tests, packs, verifies, tags, publishes, releases |
| `cleanup-cache.yml` | pull request closed | Evicts that pull request's caches |

Both pipelines verify a package through one composite action,
`.github/actions/verify-tool-package`, so a pull request and a release check a package
in exactly the same way. Packages are written to `build/package`, inside the `build/`
directory every build writes to and git ignores.
