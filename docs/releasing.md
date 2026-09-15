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
  job that publishes exchanges the workflow's short-lived GitHub OIDC token for a
  temporary API key at publish time. There is no long-lived credential to leak,
  rotate, or accidentally commit.
- **The deploy app's token is minted for one push.** The organisation's rulesets protect
  `main` and the release branches and let its deploy GitHub App push to them. Deploy mints
  a token from that app only once every check has passed, for this repository alone and
  for its contents and workflow files alone, and hands it to the one `git push` that
  promotes: it is never written to the working copy, and it is revoked when the job ends.
  The app's private key is an organisation secret, not stored here. GitHub gives it to
  workflow runs from this repository's own branches, never to runs for a fork's pull
  request or for Dependabot, so who can push branches here decides who can reach it. The
  workflows are otherwise self-contained: they do not call the reusable workflows in the
  private DevOps repository, and never use `secrets: inherit`, which would hand every
  secret this repository can read to the workflow it calls.
- **Pull requests cannot reach a credential.** `pipeline-pr.yml`, and the `test.yml`
  matrix it calls, declare `permissions: contents: read`, use no secrets, and check out
  without leaving a credential in the working copy, so a pull request from a fork runs
  the full test matrix with nothing to steal. The one job with more is `ci-check`, which
  asks for `statuses: write` to post the status merging requires; it checks out nothing
  and runs none of the repository's code. `pull_request_target`, which would run
  fork code with repository permissions, is not used anywhere. The only other
  pull-request-triggered workflow, `cleanup-cache.yml`, asks for `actions: write` and is
  skipped outright for forks, which GitHub would issue a read-only token for regardless.
- **Each publishing job holds only what it needs.** Package Pipeline builds, tests and
  verifies in a job with read-only permissions, so repository code never runs with a
  credential that can write. The tag and the GitHub release are created by jobs that run
  no repository code. Only the job that publishes to nuget.org runs in the `nuget`
  environment, which admits `release/stable` alone, and only it can request the OIDC
  token nuget.org trusts.
- **Actions are pinned to commits.** Every action a workflow uses is pinned to a full
  commit SHA, with its version in a comment. A tag can be moved to different code after
  the fact, and the publishing job can mint a key that publishes to nuget.org, so what runs
  there must be exactly what was reviewed. Dependabot (`.github/dependabot.yml`) proposes
  updates, the new SHA and version together.

The deploy app's credentials are organisation secrets and need nothing configured here.
The only other configuration required is one **variable** (not a secret), set on this
repository or inherited from the organisation:

| Kind | Name | Value |
|---|---|---|
| Variable | `NUGET_USER` | The nuget.org username (profile name, not email) of the account that created the trusted publishing policy |

## One-time setup

1. **On nuget.org**, add a trusted publishing policy, owned by the account or organisation
   that is to own the `RepoHarness` package:
   - Scopes: push new packages and package versions, since the first publish creates the
     package; the glob can be `RepoHarness` alone
   - Repository owner and name: this repository
   - Workflow file: `pipeline-pkg.yml`
   - Environment: `nuget`
2. **Set the variable `NUGET_USER`**, on this repository or on the organisation, to the
   nuget.org username of the account that created that policy.
3. **Create the `nuget` environment**, limited to the `release/stable` branch: only stable
   publishes to nuget.org, and the job that does runs in that environment. Add an approval
   if a publish should wait for one. Create it before the first stable publish: a run that
   names an environment which does not exist yet creates it, with no branch limit and no
   approval.
4. **Give the deploy app the `workflows` permission.** A promotion carries every change to
   `.github/workflows` made since the last one, Dependabot's included, and GitHub lets an
   app push those only with the `workflows` permission, so Deploy's token asks for
   `contents` and `workflows`. An app that lacks either cannot mint that token, and Deploy
   stops before anything moves.

Until step 1 exists on nuget.org, a stable publish stops at the nuget.org login, after its
tag is created and before anything reaches nuget.org. Once the policy exists, start Package
Pipeline again for `release/stable`, and it resumes from that tag.

## Channels

`repo-harness` ships as an **app**: people install the released tool rather than build on
it, so it uses two channels.

```
main  ──deploy beta──▶  release/beta  ──deploy stable──▶  release/stable
                             │                                   │
                 GitHub prerelease 0.2.0-beta      nuget.org and GitHub release 0.2.0
```

`beta` releases each new version on GitHub alone, as a prerelease, `<version>-beta`, with
the package attached; nothing goes to nuget.org. `stable` publishes a version `beta` has
already released, as the bare version, to nuget.org and as a GitHub release with the
package attached: it is refused unless that version's beta tag names exactly the commit
being published and its prerelease exists. Tags and GitHub releases carry a `v` prefix:
`v0.2.0-beta`, `v0.2.0`.

To try a beta, download its `.nupkg` from the prerelease into a folder, then install it
from there:

```bash
dotnet tool install --global RepoHarness --version 0.2.0-beta --add-source ./folder
```

WSL distributions and ssh hosts install repo-harness from nuget.org alone, so a beta build
cannot bring a host to its version: legs on other hosts need a stable build.

A beta needs no build number to be new. Every beta deploy bumps `<Version>` first, and a
version that is already tagged is refused before the bump is committed, so the same
version is never published twice.

## Cutting a release

Run the **Deploy** workflow from the Actions tab.

| Channel | Run from | What it does |
|---|---|---|
| `beta` | `main` | Tests `main`, bumps `<Version>` on the tested commit (committed as `v<version>`), then moves `main` and `release/beta` to it in one atomic push |
| `stable` | anywhere | Tests `release/beta`, checks that beta released that version from that commit, moves `release/stable` to it |

`beta` accepts an optional explicit version; without one it bumps the patch. A
major or minor change is always a deliberate decision, so it must be passed
explicitly. `stable` refuses an explicit version: it promotes exactly what `beta`
already released.

Deploy runs as three jobs:

1. **plan** validates the request and pins the source branch's current commit.
2. **test** runs `test.yml`, the same gate every pull request passes: Linux, Windows and
   macOS, each on x86_64 and arm64, on that pinned commit.
3. **promote** first runs every check that can refuse the release: that no publish of the
   release branch, or for stable of `release/beta`, is still running; that the release
   branch can fast-forward to the tested commit and its last publish finished; for beta,
   that no release tag already contains the tested commit; and for stable, that beta
   released it. Only then does it commit the beta bump, mint the deploy app's token and
   push, `main` and `release/beta` together for beta, and start **Package Pipeline** for
   that branch, naming the exact commit it promoted.

Package Pipeline runs as four jobs:

1. **package** checks, for stable, that beta released the commit, and refuses to publish a
   version again. It then builds, runs the suite on the commit being published, packs, and
   verifies that the packed tool installs from the local package alone and reports the
   version it was packed as. It holds read-only permissions.
2. **tag** creates the immutable tag.
3. **nuget**, for stable only, publishes to nuget.org from the `nuget` environment.
4. **release** creates the GitHub release at the promoted commit, a prerelease for beta,
   with the `.nupkg` attached.

Deploy starts Package Pipeline by dispatch, naming the commit it promoted, which Package
Pipeline refuses to substitute. A release branch moving publishes nothing by itself, so a
stray push to one can never ship. Package Pipeline can also be started by hand for a
release branch, for example to finish a publish that stopped.

The tag is created **before** publishing: a tag is cheap to delete, a published NuGet
version is permanent. A run that stops part way resumes when it is started again for the
same commit: a tag already naming that commit is reused, a version nuget.org already has
is neither rebuilt nor pushed again, and the GitHub release, created last and carrying the
package nuget.org has, is what marks a version published. nuget.org lists a version only
minutes after accepting it, so a run started again within those minutes stops and says so;
start it again a little later. Package Pipeline tests on Linux x86_64 only; the full matrix
has already passed in Deploy, for that commit on stable, and on beta for its parent, which
differs only in `<Version>`. Runs of either workflow wait in the order they started rather
than cancelling a run that waits.

## Refusals worth knowing

The pipelines fail loudly rather than reporting a green run that shipped nothing, or
shipped something that was never tested:

- **Nothing new to publish.** `beta` refuses a commit that a release tag already contains,
  and `stable` a version that is already tagged. Tags record what was published, not where
  the branches point: a release branch moved by hand publishes nothing, and Deploy
  promotes from wherever it was left.
- **A publish that has not finished.** Deploy refuses while Package Pipeline has a run
  that has not finished for the release branch, or for stable, for `release/beta`. It also
  refuses to move a release branch whose newest tag for that channel was never released:
  moving on would leave that publish unable to finish. Start Package Pipeline again to
  finish a publish from the branch's head. A tag the branch has already moved past is
  released by hand, and a publish nuget.org never received is abandoned by deleting its
  tag.
- **Release branch diverged.** A release branch only ever fast-forwards to a tested
  commit. One holding commits the tested commit lacks is refused, because merging would
  put a tree on it that the matrix never tested; a person reconciles it. Every check
  runs before the beta bump is committed, so a refusal leaves `main` as it was.
- **The branch moved during the release.** Nothing is force-pushed, and beta pushes `main`
  and `release/beta` atomically, so if `main` moved while the matrix ran, git rejects the
  whole push and nothing is promoted. A Package Pipeline run publishes the commit its
  branch held when the run started, and refuses one that is not the commit Deploy promoted.
- **Beta never released.** `stable` refuses a version whose beta tag is missing, names a
  commit other than the one being published, or has no GitHub prerelease. Deploy checks
  this before it promotes, and Package Pipeline again before it publishes, however it was
  started.
- **The deploy app lacks a permission.** Deploy's token asks for `contents` and
  `workflows`; an app without either cannot mint it, and Deploy stops with nothing moved.
  See One-time setup.
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
| `pipeline-pkg.yml` | dispatched by `deploy.yml`, or manual | Checks, tests, packs, verifies and tags; publishes stable to nuget.org; releases on GitHub, as a prerelease for beta; resumes a run that stopped part way |
| `cleanup-cache.yml` | pull request closed | Evicts that pull request's caches |

Both pipelines verify a package through one composite action,
`.github/actions/verify-tool-package`, so a pull request and a release check a package
in exactly the same way. Packages are written to `build/package`, inside the `build/`
directory every build writes to and git ignores.
