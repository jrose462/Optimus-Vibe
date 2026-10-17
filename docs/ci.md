# CI, releases and the bug-report loop

The app repo (`jrose462/Optimus-Vibe`) is **public** and holds code only; users keep their own settings in a private
config repo. Four GitHub Actions workflows live in `.github/workflows/`. Claude's prompts for the bug loop are in
`.github/claude/`, and the labels they use are listed in `.github/labels.md`.

| Workflow | Trigger | Runner | What it does |
|---|---|---|---|
| `ci.yml` | push to `main`, every pull request, manual | `windows-latest` | restore, build Release, `dotnet test`; uploads the `.trx` results as an artifact when something fails |
| `release.yml` | push of a tag `vX.Y.Z`, or manual with a version | `windows-latest` | test, publish both exes single-file self-contained win-x64 at that version (baking in the bug-report token and OAuth client id), write `.sha256` files, create the GitHub Release |
| `project-board.yml` | issue/PR/release events | `ubuntu-latest` | moves items on the project board (needs `PROJECT_TOKEN`); never runs Claude or untrusted code |
| `claude-bug-triage.yml` | issue opened/edited whose body carries the app's hidden report marker (any author); `@claude` comments from the owner/members/collaborators | `ubuntu-latest` | The workflow labels the issue `bug-report`; Claude posts an action plan and, for clear small fixes, opens a PR that `ci.yml` then tests. At most 5 runs per UTC day, checked before any Claude step |

## CI (`ci.yml`)

Runs `dotnet restore`, `dotnet build -c Release` and `dotnet test` on `OptimusVibe.sln`. On a failure the
`TestResults/` folder (`test-results.trx`) is uploaded as an artifact named `test-results-<run>-<attempt>`
(kept 14 days); open it in Visual Studio or any TRX viewer. Superseded runs on the same PR are cancelled.

Tests and the hosted Windows runner:

- `RegistryHandlerTests` create and delete temp keys under `HKCU`; fine on hosted runners.
- `GitSyncTests` create temp repos with the `git` CLI (preinstalled on the runner) and set a local test
  identity; they skip themselves when `git` is missing.
- `GitHubMirrorTests.CredentialManager_RoundTrip` writes and deletes a generic credential in Windows Credential
  Manager. This is the one environment-sensitive test: it should work under the runner's user account, but if it
  ever fails only in CI with a Win32 error from `CredWrite`/`CredRead`, suspect the runner environment before the
  code.
- Device detection tests use fixture hardware data, not WMI; no test needs Steam, a GPU or NVAPI. No test reads a
  real config: every test builds its own fixtures (the generic starter config in `seed/config/` is embedded in Core
  and has its own tests).

## Cutting a release (`release.yml`)

```sh
git tag v0.3.0 && git push origin v0.3.0
```

Or run **Actions → Release → Run workflow** with `version` = `0.3.0` (no `v`); the workflow then creates the
tag `v0.3.0` at the commit it ran on (the branch you picked). The version must be `X.Y.Z`; the run fails if that
release already exists.

The workflow:

1. Builds and tests the solution with `-p:Version=<version>` (overriding the default `<Version>` in
   `Directory.Build.props`).
2. Publishes `src/OptimusVibe.App` and `src/OptimusVibe.Cli` with
   `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Version=<version>` and checks that
   each exe's ProductVersion starts with the version.
3. Writes `OptimusVibe.exe.sha256` and `optimus-vibe.exe.sha256` in `sha256sum` format
   (`<lowercase hex><two spaces><file name>` + LF, no BOM).
4. Also copies `THIRD_PARTY_NOTICES.md` and `LICENSE` (repo root, required) into the assets. Runs `gh release create v<version>` with those and the four exe files and `--generate-notes` (notes come from merged PRs
   and commits since the previous release).

Release asset names are exactly `OptimusVibe.exe`, `OptimusVibe.exe.sha256`, `optimus-vibe.exe` and
`optimus-vibe.exe.sha256`; the in-app updater looks for these, so do not rename them. To verify a download by
hand: `Get-FileHash .\OptimusVibe.exe -Algorithm SHA256` and compare with the `.sha256` file.

### Build-time values baked into the exes

| Value | Where it comes from | Used for |
|---|---|---|
| GitHub OAuth **client id** (public) | `OptimusVibeGitHubClientId` in `Directory.Build.props` (default is the Optimus-Vibe OAuth App); the release can override it with the repo **variable** `OPTIMUSVIBE_GITHUB_CLIENT_ID` | device-code sign-in, used only to pick/create the user's private config repo. At runtime: `settings.json` `gitHubClientId`, then env `OPTIMUSVIBE_GITHUB_CLIENT_ID`, then the baked value |
| **Bug-report token** | repo **secret** `BUG_REPORT_TOKEN` (env var / `-p:BugReportToken=` at build time) | the fallback identity of "Report a problem" / `optimus-vibe report-bug` when the user has not signed in to GitHub |

The OAuth App needs **Device Flow** enabled (github.com/settings/developers → the app → Enable Device Flow). The client
id is not a secret.

#### Who files a report

1. **Signed in to GitHub** (the user chose a private config repo that way): the app files the issue with *their* token (the OAuth `repo` scope can open issues on a public repo), so they are the author and get the notifications. The panel and CLI say "Filed as @login".
2. **Not signed in** (e.g. local-folder users): the built-in token files it ("Filed anonymously via Optimus-Vibe"); the issue is authored by the repo owner's account.
3. **Neither** (dev build, no secret): the browser fallback below.

If a saved sign-in has been revoked the app retries once with the built-in token.

#### The bug-report token

Create it once, from the repo owner's account (there is no bot account):

1. github.com/settings/personal-access-tokens → **Generate new token** (fine-grained).
2. Resource owner: your account. Repository access: **Only select repositories** → `jrose462/Optimus-Vibe`.
3. Permissions: **Issues: Read and write**, nothing else (Metadata read is added automatically). Pick the longest
   expiry GitHub allows.
4. Add it as the repository secret `BUG_REPORT_TOKEN` (Settings → Secrets and variables → Actions). The next release bakes it
   in. Without the secret the release still builds, but the exes fall back to the browser form (see below), and the workflow
   prints a warning.

How it is baked: `OptimusVibe.Core.csproj` runs a small inline MSBuild task that XORs the token with a random 32-byte
key into a generated `BakedSecrets.g.cs` (in `obj/`, never committed). The test `BakedToken_RoundTrips_WhenTheBuildHadOne`
checks that the decoded value equals `BUG_REPORT_TOKEN` when it is set.

**Be honest about what this is.** The XOR only keeps the token out of a plain `strings`/grep of the exe. Anyone who wants it
can extract it from the exe, so treat it as public. Its only power is reading and writing **issues on the public
`jrose462/Optimus-Vibe` repo** (which anyone with a GitHub account can already do by hand), but issues filed with it are authored
by *your* account. That is why the triage workflow does not trust issue authorship or the marker, why the app limits itself to 5 reports a day per machine (client-side courtesy only), and why triage has a hard daily cap checked before Claude runs.

**Rotation:** generate a new fine-grained PAT, update the `BUG_REPORT_TOKEN` secret, cut a new release, then delete the old
token on GitHub. Old exes then get a "rejected" message and tell the user to open an issue by hand; the updater delivers
the new release.

**Without a baked token** (dev builds, forks, or if you skip the secret) "Report a problem" copies the report to the
clipboard and opens a prefilled `issues/new` page in the browser (title, body and label in the URL, shortened to a safe
length; the marker line is included). The user submits it with their own GitHub account. Triage treats them like any other marked issue.

## The bug-report loop (`claude-bug-triage.yml`)

```
App "Report a problem"  ->  issue (marker comment, description, device info, sanitized logs)
  -> gate job: credentials present? already triaged? daily cap (5/UTC day)? add bug-report label
  -> claude job: action-plan comment; if the fix is clear and small, branch claude/issue-<n> + fix + regression test + PR "Fixes #<n>"
  -> guard job: policy check of the pushed branch (closes the PR and deletes the branch on a violation)
  -> ci.yml builds and tests the PR on Windows; you review and merge (closes the issue); tag a release; the app updates itself
```

Triage is fully automatic: every marked report gets a plan, and a fix PR when Claude is confident. A human still reviews and
merges every PR (branch protection, below). End-to-end flow of the whole product: [pipeline.md](pipeline.md).

- **Triggers.** An opened/edited issue whose body contains `<!-- optimus-vibe:report v1 -->` (any author), or an `@claude`
  comment on an **issue** (not a PR) from an OWNER, MEMBER or COLLABORATOR (bots ignored). The marker is public, so anyone can
  trigger a run by copying it; that is what the daily cap and the guard are for.
- **Three jobs.** `gate` (no Claude; reads only whether the Claude secrets exist), `claude` (the only job with the Claude
  credential; permissions `contents/pull-requests/issues: write`, `id-token: write`; checkout with `persist-credentials: false`,
  the action pushes with its own Claude GitHub App token), `guard` (no Claude, no secrets, no checkout, REST API only).
  `BUG_REPORT_TOKEN` and `PROJECT_TOKEN` are never referenced in these jobs.
- **Tools Claude gets.** Allowed: `Read, Glob, Grep, Edit, MultiEdit, Write, TodoWrite` and Bash only for `gh issue view`,
  `gh issue comment ... --body-file .claude-out/*`, `gh pr create --base main --head claude/issue-* ... --body-file
  .claude-out/*`, `gh pr view`, `git status/diff/log/add`, `git commit -m`, `git switch -c` / `git checkout -b claude/issue-*`,
  `git push [-u] origin claude/issue-*`. Denied: reading `/proc`, `/etc`, `/sys`, `/root`, `/home`, `~`, `.git/config`;
  editing `.github/**`, `*.csproj`, `*.props`, `*.targets`, `nuget.config`; WebFetch/WebSearch; `env`, `printenv`, `set`,
  `export`, `curl`, `wget`, `cat`; pushes to `main`, refspecs with `:`, force/delete pushes; `git commit -F`, `git diff
  --no-index`, `git config/remote`; `gh api`, `gh issue edit`, `gh run`, `gh secret`, `gh auth`; `dotnet` (Linux cannot run our
  tests; CI does). Bodies are written to `.claude-out/` (git-excluded) so `--body-file` cannot be aimed at `/proc/self/environ`.
- **Guard.** After Claude, every `claude/*` branch is compared with `main` through the compare API (no code is checked out or
  run). The branch is rejected when its name is not `claude/issue-<n>`, it touches `.github/**`, `*.csproj`/`*.props`/
  `*.targets`/`*.sln`, `nuget.config`, `packages.lock.json`, `global.json`, `.config/`, changes more than 200 lines (the
  prompt asks for ~150), or has an open PR not targeting `main`. A rejected branch gets its PR closed with an explanatory
  comment and the branch deleted.
- **Daily cap** (`DAILY_CAP: '5'` in the gate) and the "already has a `<!-- claude-bug-triage -->` plan comment" check run
  before Claude; runs are serialised per issue.
- **Caveat.** `claude-code-action` checks that the triggering actor has write access; a report filed under a
  non-collaborator's own GitHub login may be refused by the action (reports filed with the built-in token are authored by
  the owner and pass). If runs fail with "actor does not have write permissions", post `@claude` on the issue yourself.
- **Cost.** One Claude Code session per run (40 turns, 45 min), billed to the API key or Claude subscription.

## The project board (`project-board.yml`)

Board: <https://github.com/users/jrose462/projects/5> (Status field: Reported, Planned, Fix in PR, Fixed, Released).
A user-owned Project cannot be written with `GITHUB_TOKEN`, so this separate small workflow uses the secret `PROJECT_TOKEN`.
It never runs Claude and never checks out or runs any code. Status only moves forward.

| Event | Board effect |
|---|---|
| issue opened/edited with the report marker, or labeled `bug-report` | add issue, Status = Reported |
| comment by a Bot containing `<!-- claude-bug-triage -->` | Status = Planned |
| PR opened/reopened (same-repo branch) linked to an issue (`Fixes #n` via GitHub's closing references, or branch `claude/issue-<n>`) | issue and PR added, Status = Fix in PR |
| PR merged | linked issue and PR Status = Fixed |
| release published (not draft/prerelease) | every Fixed item of this repo whose issue/PR is closed/merged becomes Released, and the issue gets the comment "Shipped in vX.Y.Z — the app will update itself." |

Without `PROJECT_TOKEN` every run is a green no-op with a notice. **Why `pull_request_target` is safe here:** it runs the
workflow from `main` with secrets even for forks, which is dangerous only if PR code or PR text is executed. This workflow
checks out nothing, passes event values through environment variables, uses them only as numbers/ids, ignores fork PRs on
open (a fork could fake a branch name), and takes issue links from GitHub's own `closingIssuesReferences`. A merged fork PR
is processed because the owner merged it.

## Threat model

The repo is public: anyone can open issues, comments and fork PRs, and the bug-report marker and the built-in issues token
(extractable from the exes) are effectively public.

| Threat | Mitigation |
|---|---|
| **Prompt injection** in an issue/comment/log making Claude exfiltrate secrets, change workflows or push malicious code | prompts state issue text is untrusted data (`.github/claude/repo-rules.md`); tight allow and deny lists (above); no network tools; the Claude job holds no secret but its own credential; the plan reaches humans only as a comment; fix PRs are policed by the guard and CI and still need human review |
| **Token extraction** (Claude reading env, `/proc`, `.git/config`, or `--body-file` tricks) | env/proc/config reads and `env`/`printenv`/`curl` denied; body files restricted to `.claude-out/`; checkout does not persist credentials; `BUG_REPORT_TOKEN`/`PROJECT_TOKEN` never present in the Claude job; the built-in `BUG_REPORT_TOKEN` is issues-only on this repo and treated as public |
| **Cost abuse** (spamming marked issues) | daily cap of 5 checked before Claude, per-issue dedupe, 40-turn / 45-min limits |
| **Fork PRs / malicious workflow changes** | `ci.yml` uses `pull_request` (never `pull_request_target`), `contents: read`, no secrets, so forks get a read-only token; the "Protect .github" job fails PRs touching `.github/**` unless the author is the owner; CODEOWNERS requires the owner's review (including `/.github/`); default workflow permissions are read-only and Actions cannot approve PRs |
| **Poisoned release** | releases only from tags/dispatch by people with write access; branch protection on `main`; Claude cannot touch `release.yml` or project files |
| **Board manipulation** | the board workflow reacts only to Bot-authored plan comments, issue events and GitHub's own linkage, never to PR text |

## Branch protection and repo settings (already applied)

- `main`: pull request required with 1 approving review and **CODEOWNERS review**; required status check **Build and test
  (windows)**; no force-push, no deletion; administrators are not enforced (the owner can merge their own work). Consider
  adding **Protect .github** as a required check.
- Default workflow permissions are read-only; Actions cannot create or approve pull requests.
- Secret scanning with push protection, private vulnerability reporting and Dependabot alerts are enabled.
- `.github/CODEOWNERS`: `* @jrose462` and `/.github/ @jrose462`.

## Setup checklist: what you need to do

The Claude workflow is **inert until a Claude credential secret exists** (the gate job ends green with a notice), and the board
workflow is inert until `PROJECT_TOKEN` exists. CI and Release need no secrets (without `BUG_REPORT_TOKEN` the exes use the
browser fallback).

1. **Labels** (once): run the one-liner in `.github/labels.md`. The workflow also creates any missing label itself.
2. **Install the Claude GitHub App** on `jrose462/Optimus-Vibe`: run `/install-github-app` in Claude Code, or install from
   <https://github.com/apps/claude> and select this repo. The action obtains its token from the app via OIDC, so Claude's
   pushes and PRs are made as the app and trigger `ci.yml`.
3. **Claude credential** (Settings -> Secrets and variables -> Actions -> New repository secret), one of:
   - `ANTHROPIC_API_KEY`: key from console.anthropic.com; or
   - `CLAUDE_CODE_OAUTH_TOKEN`: run `claude setup-token` locally and paste the token (counts against Pro/Max).
4. **`BUG_REPORT_TOKEN`**: see "The bug-report token" above (fine-grained PAT, only this repo, Issues read/write); needed before
   the release you want to ship.
5. **`PROJECT_TOKEN`** for the board: github.com/settings/tokens -> **Generate new token (classic)**, scope **`project`** only
   (not `repo`), with an expiry; or a fine-grained token with resource owner = your user and account permission **Projects:
   Read and write** (if board calls fail with a fine-grained token, use the classic one). Add it as the repository secret
   `PROJECT_TOKEN`. The Status option ids are hard-coded in `project-board.yml` (`OPT_*`); deleting and recreating an option
   changes its id.
6. File a test report (an issue whose body contains `<!-- optimus-vibe:report v1 -->`) and watch Actions: expect a plan
   comment, the issue on the board as Reported, then Planned.
7. Optional: make **Protect .github** a required status check. Cut a release and confirm `THIRD_PARTY_NOTICES.md` and
   `LICENSE` are attached (both must exist in the repo root or the release fails).

To switch the loop off, delete the Claude secret or disable the workflow.

## Public repository notes

- **Actions minutes.** Public repos get free standard runners, so CI and releases cost nothing. Claude triage costs
  whatever your API key or subscription is billed, bounded by the daily cap.
- **In-app updater.** Release assets are public, so the updater calls the GitHub API anonymously (60 requests/hour per IP).
  A rate-limit response shows a friendly "try again in a while" message. The updater needs no sign-in.
- **Settings are never here.** The repo holds no user configuration. The app seeds a new config repo from `seed/config/`
  (embedded in the exe): example devices, a game template, the JSON schema and empty library/request files.
- **Artifacts and logs** (test results, workflow logs) are public. Nothing in them should be sensitive; tests use only
  fixtures.
