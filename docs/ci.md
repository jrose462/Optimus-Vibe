# CI, releases and the bug-report loop

The app repo (`jrose462/Optimus-Vibe`) is **public** and holds code only; users keep their own settings in a private
config repo. Four GitHub Actions workflows live in `.github/workflows/`. Claude's prompts for the bug loop are in
`.github/claude/`, and the labels they use are listed in `.github/labels.md`.

| Workflow | Trigger | Runner | What it does |
|---|---|---|---|
| `ci.yml` | push to `main`, every pull request, manual | `windows-latest` | restore, build Release, `dotnet test`; uploads the `.trx` results as an artifact when something fails |
| `release.yml` | push of a tag `vX.Y.Z`, or manual with a version | `windows-latest` | test, publish both exes single-file self-contained win-x64 at that version (baking in the bug-report token and OAuth client id), write `.sha256` files, create the GitHub Release |
| `project-board.yml` | issue/PR/release events | `ubuntu-latest` | moves items on the project board (needs `PROJECT_TOKEN`); never runs Claude or untrusted code |
| `claude-bug-triage.yml` (named "Claude bug squash") | intake: issue opened/edited with the app's report marker, and reporter replies to `needs-info`; weekly plan: Thursdays 00:01-09:30 Pacific; explicit: Run workflow, or an `@claude` comment from an owner/member/collaborator | `ubuntu-latest` | Intake labels and queues reports (no Claude). The weekly run and explicit runs audit each issue with Claude and, for a small valid fix, have Claude implement it. A publish job (no Claude) posts the result and opens any fix PR. See [The bug-report loop](#the-bug-report-loop-claude-bug-triageyml) |

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
by *your* account. That is why the workflow does not trust issue authorship or the marker, why the app limits itself to 5 reports a day per machine (client-side courtesy only), and why Claude only runs in the weekly window or on an explicit maintainer request.

**Rotation:** generate a new fine-grained PAT, update the `BUG_REPORT_TOKEN` secret, cut a new release, then delete the old
token on GitHub. Old exes then get a "rejected" message and tell the user to open an issue by hand; the updater delivers
the new release.

**Without a baked token** (dev builds, forks, or if you skip the secret) "Report a problem" copies the report to the
clipboard and opens a prefilled `issues/new` page in the browser (title, body and label in the URL, shortened to a safe
length; the marker line is included). The user submits it with their own GitHub account. Triage treats them like any other marked issue.

## The bug-report loop (`claude-bug-triage.yml`)

```
App "Report a problem"  ->  issue (marker comment, description, device info, sanitized logs)
  -> intake (no Claude): label bug-report, "queued" comment, card in Reported
  -> weekly queue (Thu 00:01-09:30 PT, or an explicit run): oldest queued issues first, at most SQUASH_WEEKLY_MAX (default 10)
  -> squash job, stage 1 (Claude, file tools, read-only token): audit.json + audit.md (+ plan.md if a small valid fix)
  -> squash job, stage 2 (Claude, cheaper model, only for valid + fixable): edits files per the plan, writes a patch
  -> publish job (no Claude): post audit, labels, board; for a fix: check the patch, push claude/issue-<n>, open PR "Fixes #<n>"
  -> ci.yml builds and tests the PR on Windows; you review and merge (closes the issue); tag a release; the app updates itself
```

Claude never comments, labels, pushes or opens PRs itself. Anything it writes goes to `.claude-out/`, and the publish job
validates it first. A human still reviews and merges every PR (branch protection, below). End-to-end flow of the whole
product: [pipeline.md](pipeline.md).

- **Intake (any time, no Claude).** An opened/edited issue whose body contains `<!-- optimus-vibe:report v1 -->` (any author)
  gets the `bug-report` label, a "queued for the weekly bug squash" comment, and a board card in Reported. A reporter's
  comment on a `triage:needs-info` issue removes that label, which requeues the issue.
- **Weekly bug squash.** Runs only Thursdays 00:01-10:00 America/Los_Angeles (the window ends at the Claude weekly usage
  reset). It takes the oldest queued issues (no `triage:*` label yet), at most `SQUASH_WEEKLY_MAX` (repo variable, default 10).
  No new issue starts after 09:30; anything left over waits a week. Its purpose is to protect the maintainer's Claude
  subscription usage.
- **Explicit runs (any time).** Actions → "Claude bug squash" → Run workflow, with an optional issue number and a stage
  (`audit` or `audit-and-fix`); or an `@claude` comment on an **issue** (not a PR) from an OWNER, MEMBER or COLLABORATOR
  (bots ignored).
- **Stage 1: audit.** Model from repo variable `SQUASH_AUDIT_MODEL` (default `claude-sonnet-5-5`). Verdict: `valid`,
  `duplicate`, `by-design`, `needs-info`, `not-a-bug` or `suspicious`. For a valid small bug it also writes an implementation
  plan that a smaller model can follow.
- **Stage 2: fix.** Model from `SQUASH_FIX_MODEL` (default `claude-haiku-5-5`). It only runs for `valid` audits marked
  fixable, and implements the plan.
- **Jobs.** `plan` (no Claude; picks the issues and the window), `squash` (the only job with a Claude credential; read-only
  `contents` and `issues` token; checkout with no persisted credentials), `publish` (no Claude; write permissions for
  contents, pull requests, issues and actions; it checks out `main` and applies Claude's patch as data, never running it). `BUG_REPORT_TOKEN` and `PROJECT_TOKEN`
  are never referenced in the `squash` job.
- **Tools Claude gets.** File tools only: stage 1 has `Read, Glob, Grep, Write`; stage 2 adds `Edit, MultiEdit`. No shell
  (no Bash, `git` or `gh`), no web, and no GitHub token beyond the read-only one. Reads outside the checkout (`/proc`, `/etc`,
  `~`, `.git/config`, the runner's temp and home folders) and writes to `.github/**`, `*.csproj`, `*.props`, `*.targets`,
  `*.sln` and `nuget.config` are denied. The report is copied into `.claude-in/` as untrusted input, and Claude writes its
  results to `.claude-out/`. After the audit the checkout is reset, so the fix stage starts clean.
- **Publish job.** Validates `audit.json` (anything malformed becomes `triage:needs-human`). It neutralises `@` mentions and
  HTML comments in Claude's text, then posts the audit comment and applies the label and board change for the verdict:
  `valid` → `triage:valid` and board Planned (or `triage:needs-human` when not fixable); `needs-info` → `triage:needs-info`
  and Planned; `duplicate`, `by-design`, `not-a-bug` → label, close, archive card; `suspicious` → label, close, lock, archive.
- **Patch policy (fixes only).** The publish job rejects a patch that does not apply cleanly to `main`, contains a binary,
  touches `.github/**`, project files (`*.csproj`, `*.vbproj`, `*.fsproj`, `*.props`, `*.targets`, `*.sln`, `*.slnx`),
  `nuget.config`, `packages.lock.json`, `global.json`, `dotnet-tools.json` or `.config/`, or changes more than 200 lines.
  A rejected fix gets a comment and `triage:needs-human`. An accepted one is pushed to `claude/issue-<n>` (replacing an
  earlier push of the same branch), opened as a PR labelled `claude-fix` with `Fixes #<n>`, CI is started on the branch
  explicitly (pushes made with `GITHUB_TOKEN` do not start workflows), and the card moves to Fix in PR.
- **Dedupe.** Issues that already have a `triage:*` label are skipped by the weekly queue. Explicit runs can re-audit one.
- **Cost.** Up to two Claude sessions per issue (audit, then fix), 40 turns each, billed to the API key or Claude subscription
  (see the secrets note in the setup checklist).

## The project board (`project-board.yml`)

Board: <https://github.com/users/jrose462/projects/5> (Status field: Reported, Planned, Fix in PR, Fixed, Released).
A user-owned Project cannot be written with `GITHUB_TOKEN`, so this separate small workflow uses the secret `PROJECT_TOKEN`.
It never runs Claude and never checks out or runs any code. Status only moves forward.

| Event | Board effect |
|---|---|
| issue opened/edited with the report marker, or labeled `bug-report` | add issue, Status = Reported |
| the bug-squash publish job runs this workflow (`workflow_dispatch`: planned, archive, or fix-in-pr) | `valid` / `needs-info` audit → Planned; closed verdicts → archived; fix PR → Fix in PR. Dispatched explicitly because the job's own `GITHUB_TOKEN` events do not trigger workflows |
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
| **Prompt injection** in an issue/comment/log making Claude exfiltrate secrets, change workflows or push malicious code | the report is untrusted data (`.github/claude/repo-rules.md`); Claude has file tools only (no shell, git, gh or web) and a read-only token, so it cannot post, label, push or merge; its only output is files in `.claude-out/`; the publish job validates them, neutralises mentions and HTML comments, and applies the patch policy above; fix PRs still need CI and a human review |
| **Token extraction** (Claude reading env, `/proc`, `.git/config`) | env/proc/config reads denied; Claude's job has no write token; the checkout does not persist credentials; `BUG_REPORT_TOKEN` and `PROJECT_TOKEN` are not referenced in the `squash` job; the built-in `BUG_REPORT_TOKEN` is issues-only on this repo and treated as public |
| **Cost abuse** (spamming marked issues) | intake costs no Claude; Claude runs only in the Thursday window (or on an explicit maintainer request), at most `SQUASH_WEEKLY_MAX` issues a week, skipping issues already labelled `triage:*`; 40-turn limit per session |
| **Fork PRs / malicious workflow changes** | `ci.yml` uses `pull_request` (never `pull_request_target`), `contents: read`, no secrets, so forks get a read-only token; the "Protect .github" job fails PRs touching `.github/**` unless the author is the owner; CODEOWNERS requires the owner's review (including `/.github/`); default workflow permissions are read-only. The repo lets Actions create and approve PRs (needed by the publish job), but an Actions approval does not satisfy the required code-owner review |
| **Poisoned release** | releases only from tags/dispatch by people with write access; branch protection on `main`; Claude cannot touch `release.yml` or project files |
| **Board manipulation** | the board workflow reacts to issue events, bot comments carrying its audit marker, the publish job's dispatches and GitHub's own linkage, never to PR text |

## Branch protection and repo settings (already applied)

- `main`: pull request required with 1 approving review and **CODEOWNERS review**; required status check **Build and test
  (windows)**; no force-push, no deletion; administrators are not enforced (the owner can merge their own work). Consider
  adding **Protect .github** as a required check.
- Default workflow permissions are read-only. **Allow GitHub Actions to create and approve pull requests** is ON so the
  publish job can open fix PRs. An Actions approval does not count toward the required CODEOWNERS review.
- Secret scanning with push protection, private vulnerability reporting and Dependabot alerts are enabled.
- `.github/CODEOWNERS`: `* @jrose462` and `/.github/ @jrose462`.

## Setup checklist: what you need to do

The Claude workflow is **inert until a Claude credential secret exists** (the plan job ends green with a notice), and the board
workflow is inert until `PROJECT_TOKEN` exists. CI and Release need no secrets (without `BUG_REPORT_TOKEN` the exes use the
browser fallback).

1. **Labels** (once): run the one-liner in `.github/labels.md`. The workflow also creates any missing label itself.
2. **No Claude GitHub App is needed.** The squash job passes the built-in `GITHUB_TOKEN` to the action, and Claude has no
   GitHub write access. Pushes and PRs are made by the publish job, and CI is started explicitly.
3. **Claude credential** (Settings -> Secrets and variables -> Actions -> New repository secret), one of:
   - `CLAUDE_CODE_OAUTH_TOKEN`: run `claude setup-token` locally and paste the token (uses your Claude Pro/Max subscription); or
   - `ANTHROPIC_API_KEY`: key from console.anthropic.com (pay-per-use API).
   If both are set the action may prefer the API key, so keep only the one you intend to be billed.
   Optional repository **variables** (Settings -> Variables): `SQUASH_WEEKLY_MAX` (default 10), `SQUASH_AUDIT_MODEL`
   (default `claude-sonnet-5-5`), `SQUASH_FIX_MODEL` (default `claude-haiku-5-5`).
4. **`BUG_REPORT_TOKEN`**: see "The bug-report token" above (fine-grained PAT, only this repo, Issues read/write); needed before
   the release you want to ship.
5. **`PROJECT_TOKEN`** for the board: github.com/settings/tokens -> **Generate new token (classic)**, scope **`project`** only
   (not `repo`), with an expiry; or a fine-grained token with resource owner = your user and account permission **Projects:
   Read and write** (if board calls fail with a fine-grained token, use the classic one). Add it as the repository secret
   `PROJECT_TOKEN`. The Status option ids are hard-coded in `project-board.yml` (`OPT_*`); deleting and recreating an option
   changes its id.
6. File a test report (an issue whose body contains `<!-- optimus-vibe:report v1 -->`): expect the `bug-report` label, a
   "queued" comment and the card in Reported. Then, to see the audit without waiting for Thursday, run Actions → "Claude
   bug squash" → Run workflow with that issue number.
7. Optional: make **Protect .github** a required status check. Cut a release and confirm `THIRD_PARTY_NOTICES.md` and
   `LICENSE` are attached (both must exist in the repo root or the release fails).

To switch the loop off, delete the Claude secret or disable the workflow.

## Public repository notes

- **Actions minutes.** Public repos get free standard runners, so CI and releases cost nothing. Claude bug squash costs
  whatever your API key or subscription is billed, bounded by the weekly window and `SQUASH_WEEKLY_MAX`.
- **In-app updater.** Release assets are public, so the updater calls the GitHub API anonymously (60 requests/hour per IP).
  A rate-limit response shows a friendly "try again in a while" message. The updater needs no sign-in.
- **Settings are never here.** The repo holds no user configuration. The app seeds a new config repo from `seed/config/`
  (embedded in the exe): example devices, a game template, the JSON schema and empty library/request files.
- **Artifacts and logs** (test results, workflow logs) are public. Nothing in them should be sensitive; tests use only
  fixtures.
