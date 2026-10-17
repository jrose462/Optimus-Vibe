# CI, releases and the bug-report loop

The app repo (`jrose462/Optimus-Vibe`) is **public** and holds code only; users keep their own settings in a private
config repo. Three GitHub Actions workflows live in `.github/workflows/`. Claude's prompts for the bug loop are in
`.github/claude/`, and the labels they use are listed in `.github/labels.md`.

| Workflow | Trigger | Runner | What it does |
|---|---|---|---|
| `ci.yml` | push to `main`, every pull request, manual | `windows-latest` | restore, build Release, `dotnet test`; uploads the `.trx` results as an artifact when something fails |
| `release.yml` | push of a tag `vX.Y.Z`, or manual with a version | `windows-latest` | test, publish both exes single-file self-contained win-x64 at that version (baking in the bug-report token and OAuth client id), write `.sha256` files, create the GitHub Release |
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
4. Runs `gh release create v<version>` with the four files and `--generate-notes` (notes come from merged PRs
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
App "Report a problem"  ->  issue labelled bug-report (marker comment, description, device info, sanitized logs)
  -> claude-bug-triage.yml: Claude reads the issue + logs + CLAUDE.md/README + code
  -> issue comment "Action plan" (root cause hypothesis, files to change, risks, test plan), label claude-plan
  -> if the fix is clear and small: branch claude/issue-<n>, fix + regression test, PR "Fixes #<n>" (label claude-fix)
  -> ci.yml builds and tests the PR on Windows
  -> you review and merge (closing the issue), then tag a release; the app's updater picks it up
```

Details:

- **Triggers (issues).** Every app-generated report starts with the hidden comment `<!-- optimus-vibe:report v1 -->`, whoever files it
  (signed-in user, built-in token or browser). Non-collaborators can't apply labels, so the client does not rely on the
  `bug-report` label (it tries it and retries without when GitHub refuses). A new or edited issue whose body contains the marker
  starts the workflow, which (1) checks credentials, skips issues that already have Claude's `<!-- claude-bug-triage -->` plan
  comment and enforces the daily cap, all before any Claude step, (2) adds the `bug-report` label itself with the workflow's
  `GITHUB_TOKEN`, then (3) runs Claude. The marker is not a secret, so anyone can start a run by copying it: that is what the
  cap is for, and issue text is untrusted data for Claude (see `.github/claude/repo-rules.md`). Runs are serialised per issue.
- **`@claude` comments** on any issue or PR start a follow-up run (for example "@claude CI failed, fix the test" on
  Claude's PR, or "@claude triage this" on a browser-filed report). Only comments whose `author_association` is OWNER, MEMBER or
  COLLABORATOR trigger it; bot comments are ignored.
- **Daily cap.** The first step (before checkout and before Claude) counts today's (UTC) non-skipped runs of this workflow with
  `gh run list --workflow claude-bug-triage.yml --created ">=<today>"` and turns the run into a no-op when there are more than
  5, including itself. Change `DAILY_CAP` in the workflow to adjust.
- **What Claude may do** is set by `.github/claude/repo-rules.md` and `--allowedTools` in the workflow: read and
  edit code, `git` on its branch, `gh issue` / `gh pr` / `gh run` commands, and
  `dotnet build -p:EnableWindowsTargeting=true` as a compile check. It cannot run the tests (Linux runner,
  Windows targets) and relies on `ci.yml`. It must never touch `.github/`, never pushes to `main`, and treats issue text and
  logs as untrusted data.
- **Cost.** Each triage is one Claude Code session (capped at 60 turns and 45 minutes), billed to the API key or
  to your Claude subscription.

## Setup: what you need to do

The Claude workflow is **inert until a credential secret exists**. Its first step reads `ANTHROPIC_API_KEY` and
`CLAUDE_CODE_OAUTH_TOKEN` into environment variables and sets the output `enabled=false` when both are empty;
every later step has `if: steps.gate.outputs.enabled == 'true'`, so the run finishes green with the notice
"Claude bug triage is inactive". (Secrets cannot be used in a job-level `if:`, hence the gate step.) CI and
Release need no secrets (without `BUG_REPORT_TOKEN` the exes just use the browser fallback).

1. **Create the labels** once: run the one-liner in `.github/labels.md`.
2. **Install the Claude GitHub App** on `jrose462/Optimus-Vibe`: in a local Claude Code session run
   `/install-github-app` (it installs the app and can add the secret for you), or install it from
   <https://github.com/apps/claude> and pick this repository. The action gets its GitHub token from the app via
   OIDC (`id-token: write`), so Claude's comments, commits and PRs are made as the Claude app and **its PRs
   trigger `ci.yml`**. (Pushes and PRs made with a workflow's own `GITHUB_TOKEN` do not trigger other workflows,
   which is why the app is the supported path.)
3. **Add one repository secret** (Settings → Secrets and variables → Actions → New repository secret):
   - `ANTHROPIC_API_KEY`: an API key from console.anthropic.com (pay per use), **or**
   - `CLAUDE_CODE_OAUTH_TOKEN`: run `claude setup-token` locally and paste the token; usage counts against your
     Claude Pro/Max subscription.

   The workflow passes both to `anthropics/claude-code-action@v1` (`anthropic_api_key` and
   `claude_code_oauth_token`); an unset secret is an empty string and the action uses the one that is set.
4. **Add the `BUG_REPORT_TOKEN` secret** (see "The bug-report token" above) before the release you want to ship, and
   optionally the variable `OPTIMUSVIBE_GITHUB_CLIENT_ID`.
5. File a test report from a released build (or an issue whose body contains the marker line `<!-- optimus-vibe:report v1 -->`) and check the run under Actions: you should get a plan comment from the Claude app within a few minutes.

To switch the loop off again, delete the Claude secret (or disable the workflow under Actions).

## Public repository notes

- **Actions minutes.** Public repos get free standard runners, so CI and releases cost nothing. Claude triage costs
  whatever your API key or subscription is billed, bounded by the daily cap.
- **In-app updater.** Release assets are public, so the updater calls the GitHub API anonymously (60 requests/hour per IP).
  A rate-limit response shows a friendly "try again in a while" message. The updater needs no sign-in.
- **Settings are never here.** The repo holds no user configuration. The app seeds a new config repo from `seed/config/`
  (embedded in the exe): example devices, a game template, the JSON schema and empty library/request files.
- **Artifacts and logs** (test results, workflow logs) are public. Nothing in them should be sensitive; tests use only
  fixtures.
