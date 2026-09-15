# Releasing repo-harness

`repo-harness` ships as a public .NET tool on nuget.org, under the Apache License 2.0:

```bash
dotnet tool install --global RepoHarness
```

## Security posture

This repository is public, so no secret is stored in it. The credentials the pipelines
use are the automatic `GITHUB_TOKEN`, a short-lived nuget.org key minted through OIDC at
publish time, and a short-lived token from the organisation's deploy GitHub App, minted for
the one push that promotes a release.

- **No stored NuGet key.** Publishing uses nuget.org [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): the
  package job exchanges the workflow's short-lived GitHub OIDC token for a
  temporary API key at publish time. There is no long-lived credential to leak,
  rotate, or accidentally commit.
- **One organisation credential, for one push.** The organisation's rulesets protect
  `main` and the release branches and let its deploy GitHub App push to them, and the
  organisation provides that app's credentials to every repository. Deploy mints a token
  from them only once every check has passed, for this repository alone and for its
  contents alone, and hands it to the one `git push` that promotes: it is never written to
  the working copy, and it is revoked when the job ends. The workflows are otherwise
  self-contained: they do not call the reusable workflows in the private DevOps
  repository, and never use `secrets: inherit`, which would expose every organisation
  secret to every workflow run.
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

The deploy app's credentials are organisation secrets and need nothing configured here.
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
   a publish can start from. Add whatever approval the release should require. Create it
   before the first publish: a run that names an environment which does not exist yet
   creates it, with no branch limit and no approval.
4. **Let Deploy promote workflow changes.** A promotion carries every change to
   `.github/workflows` made since the last one, Dependabot's included, and GitHub lets an
   app push those only with the `workflows` permission. Deploy's token asks for `contents`
   alone, so such a promotion stops before anything moves, and says so. To let Deploy
   promote them, grant the deploy app `workflows` and add `permission-workflows: write`
   beside `permission-contents: write` in `deploy.yml`; a token cannot ask for a
   permission its app lacks, so it is not asked for before then. Until then, someone the
   rulesets let bypass them first fast-forwards the release branch by hand, `release/beta`
   to `main` or `release/stable` to `release/beta`, with credentials allowed to change
   workflows. Moving a release branch publishes nothing, and Deploy then promotes from
   there as usual.

Until step 1 exists on nuget.org, the publish step fails closed: nothing reaches
nuget.org, no tag is created and no credential is minted. Once the policy exists, start
Package Pipeline again for that release branch.

## Channels

`repo-harness` ships as an **app**: people install the released tool rather than build on
it, so it uses two channels.

```
main  ──deploy beta──▶  release/beta  ──deploy stable──▶  release/stable
                             │                                  │
                        0.2.0-beta                            0.2.0
```

`beta` publishes each new version as a prerelease, `<version>-beta`. `stable` publishes a
version `beta` has already published, as the bare version: Deploy refuses unless that
version's beta tag names exactly the commit being promoted and its release is published.
Tags and GitHub releases carry a `v` prefix: `v0.2.0-beta`, `v0.2.0`.

A beta needs no build number to be new. Every beta deploy bumps `<Version>` first, and a
version that is already tagged is refused before the bump is committed, so the same
version is never published twice.

## Cutting a release

Run the **Deploy** workflow from the Actions tab.

| Channel | Run from | What it does |
|---|---|---|
| `beta` | `main` | Tests `main`, bumps `<Version>` on the tested commit (committed as `v<version>`), then moves `main` and `release/beta` to it in one atomic push |
| `stable` | anywhere | Tests `release/beta`, checks that beta published that version from that commit, moves `release/stable` to it |

`beta` accepts an optional explicit version; without one it bumps the patch. A
major or minor change is always a deliberate decision, so it must be passed
explicitly. `stable` refuses an explicit version: it promotes exactly what `beta`
already published.

Deploy runs as three jobs:

1. **plan** validates the request and pins the source branch's current commit.
2. **test** runs `test.yml`, the same gate every pull request passes: Linux, Windows and
   macOS, each on x86_64 and arm64, on that pinned commit.
3. **promote** first runs every check that can refuse the release: that the release branch
   can fast-forward to the tested commit, for beta that no release tag already contains
   it, and for stable that beta published it. Only then does it commit the beta bump, mint
   the deploy app's token and push, `main` and `release/beta` together for beta, and start
   **Package Pipeline** for that branch, naming the exact commit it promoted.

Package Pipeline builds, runs the suite on the commit being published, packs, verifies
that the packed tool installs from the local package alone and reports the version it
was packed as, creates the immutable tag, publishes to nuget.org, and creates the
GitHub release at the promoted commit with the `.nupkg` attached.

Deploy starts Package Pipeline by dispatch, naming the commit it promoted, which Package
Pipeline refuses to substitute. A release branch moving publishes nothing by itself, so a
stray push to one can never ship. Package Pipeline can also be started by hand for a
release branch, for example to finish a publish that stopped.

The nuget.org login comes before the tag, so a run nuget.org does not trust leaves nothing
behind. The tag is created **before** publishing: a tag is cheap to delete, a published
NuGet version is permanent. A run that stops part way resumes when it is started again for
the same commit: a tag already naming that commit is reused, a version already on nuget.org
is not pushed again, and the GitHub release, created last and carrying the package
nuget.org has, is what marks a version published. Package Pipeline tests on Linux x86_64
only; the full matrix has already passed in Deploy, for that commit on stable, and on beta
for its parent, which differs only in `<Version>`. Runs of either workflow wait in the
order they started rather than cancelling a run that waits.

## Refusals worth knowing

The pipelines fail loudly rather than reporting a green run that shipped nothing, or
shipped something that was never tested:

- **Nothing new to publish.** `beta` refuses a commit that a release tag already contains,
  and `stable` a version that is already tagged. Tags record what was published, not where
  the branches point: a release branch moved by hand publishes nothing, and Deploy
  promotes from wherever it was left.
- **Release branch diverged.** A release branch only ever fast-forwards to a tested
  commit. One holding commits the tested commit lacks is refused, because merging would
  put a tree on it that the matrix never tested; a person reconciles it. Every check
  runs before the beta bump is committed, so a refusal leaves `main` as it was.
- **The branch moved during the release.** Nothing is force-pushed, and beta pushes `main`
  and `release/beta` atomically, so if `main` moved while the matrix ran, git rejects the
  whole push and nothing is promoted. For every channel, Package Pipeline refuses to
  publish a release branch that no longer points at the commit Deploy promoted.
- **Beta never published.** `stable` refuses a version whose beta tag is missing, names a
  commit other than the one being promoted, or has no published release.
- **Workflow files without the permission.** GitHub refuses a push that changes workflow
  files unless the deploy app's token has the `workflows` permission. Deploy stops with
  nothing moved and says so; see One-time setup.
- **Not a release branch.** Package Pipeline publishes from `release/beta` and
  `release/stable` only.
- **Version already published.** A published version is never rewritten: a GitHub release
  that exists, a tag that names another commit, a version on nuget.org that no tag at the
  commit names, or, when beta bumps, a version that is already tagged, is refused. A check
  that gets no clear answer refuses too: "could not tell" is not "does not exist".
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
| `deploy.yml` | manual | Tests, checks, bumps the version, promotes with the deploy app's token, starts `pipeline-pkg.yml` |
| `pipeline-pkg.yml` | dispatched by `deploy.yml`, or manual | Tests, packs, verifies, tags, publishes, releases; resumes a run that stopped part way |
| `cleanup-cache.yml` | pull request closed | Evicts that pull request's caches |

Both pipelines verify a package through one composite action,
`.github/actions/verify-tool-package`, so a pull request and a release check a package
in exactly the same way. Packages are written to `build/package`, inside the `build/`
directory every build writes to and git ignores.
