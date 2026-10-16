# CI, releases and the bug-report loop

Three GitHub Actions workflows live in `.github/workflows/`. Claude's prompts for the bug loop are in
`.github/claude/`, and the labels they use are listed in `.github/labels.md`.

| Workflow | Trigger | Runner | What it does |
|---|---|---|---|
| `ci.yml` | push to `main`, every pull request, manual | `windows-latest` | restore, build Release, `dotnet test`; uploads the `.trx` results as an artifact when something fails |
| `release.yml` | push of a tag `vX.Y.Z`, or manual with a version | `windows-latest` | test, publish both exes single-file self-contained win-x64 at that version, write `.sha256` files, create the GitHub Release |
| `claude-bug-triage.yml` | issue opened/labelled `bug-report`; `@claude` comments from the owner/members/collaborators | `ubuntu-latest` | Claude posts an action plan on the issue and, for clear small fixes, opens a PR that `ci.yml` then tests |

## CI (`ci.yml`)

Runs `dotnet restore`, `dotnet build -c Release` and `dotnet test` on `GameProfiles.sln`. On a failure the
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
- Device detection tests use fixture hardware data, not WMI; no test needs Steam, a GPU or NVAPI.

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
2. Publishes `src/GameProfiles.App` and `src/GameProfiles` with
   `-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Version=<version>` and checks that
   each exe's ProductVersion starts with the version.
3. Writes `GameProfilesApp.exe.sha256` and `gameprofiles.exe.sha256` in `sha256sum` format
   (`<lowercase hex><two spaces><file name>` + LF, no BOM).
4. Runs `gh release create v<version>` with the four files and `--generate-notes` (notes come from merged PRs
   and commits since the previous release).

Release asset names are exactly `GameProfilesApp.exe`, `GameProfilesApp.exe.sha256`, `gameprofiles.exe` and
`gameprofiles.exe.sha256`; the in-app updater looks for these, so do not rename them. To verify a download by
hand: `Get-FileHash .\GameProfilesApp.exe -Algorithm SHA256` and compare with the `.sha256` file.

## The bug-report loop (`claude-bug-triage.yml`)

```
App "Report a problem"  ->  issue labelled bug-report (description, device info, sanitized logs)
  -> claude-bug-triage.yml: Claude reads the issue + logs + CLAUDE.md/README + code
  -> issue comment "Action plan" (root cause hypothesis, files to change, risks, test plan), label claude-plan
  -> if the fix is clear and small: branch claude/issue-<n>, fix + regression test, PR "Fixes #<n>" (label claude-fix)
  -> ci.yml builds and tests the PR on Windows
  -> you review and merge (closing the issue), then tag a release; the app's updater picks it up
```

Details:

- **Triggers.** A new issue that already carries `bug-report`, or `bug-report` being added to an existing
  issue. When the app creates an issue with the label, GitHub sends both `opened` and `labeled`; the runs are
  serialised per issue, and the second one finds the `<!-- claude-bug-triage -->` marker in Claude's plan
  comment and skips. To re-run triage on an issue, comment `@claude <what you want>` rather than re-labelling.
- **`@claude` comments** on any issue or PR start a follow-up run (for example "@claude CI failed, fix the
  test" on Claude's PR). Only comments whose `author_association` is OWNER, MEMBER or COLLABORATOR trigger it;
  bot comments are ignored.
- **What Claude may do** is set by `.github/claude/repo-rules.md` and `--allowedTools` in the workflow: read and
  edit code, `git` on its branch, `gh issue` / `gh pr` / `gh run` commands, and
  `dotnet build -p:EnableWindowsTargeting=true` as a compile check. It cannot run the tests (Linux runner,
  Windows targets) and relies on `ci.yml`. It must never touch `config/saves/`, `config/library.yaml`,
  `config/requests.yaml` or `.github/`, never pushes to `main`, and treats issue text and logs as untrusted data.
- **Cost.** Each triage is one Claude Code session (capped at 60 turns and 45 minutes), billed to the API key or
  to your Claude subscription.

## Setup: what you need to do

The Claude workflow is **inert until a credential secret exists**. Its first step reads `ANTHROPIC_API_KEY` and
`CLAUDE_CODE_OAUTH_TOKEN` into environment variables and sets the output `enabled=false` when both are empty;
every later step has `if: steps.gate.outputs.enabled == 'true'`, so the run finishes green with the notice
"Claude bug triage is inactive". (Secrets cannot be used in a job-level `if:`, hence the gate step.) CI and
Release need no secrets.

1. **Create the labels** once: run the one-liner in `.github/labels.md`.
2. **Install the Claude GitHub App** on `jrose462/GameProfiles`: in a local Claude Code session run
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
4. File a test issue with the `bug-report` label and check the run under Actions: you should get a plan comment
   from the Claude app within a few minutes.

To switch the loop off again, delete the secret (or disable the workflow under Actions).

## Private repository implications

- **Actions minutes.** Private repos draw on the account's included minutes (GitHub Free: 2,000/month), and
  **Windows runners are billed at 2x** (Linux 1x). Expect a CI run to take several Windows minutes, so roughly
  double that in billed minutes; a release a little more. Claude triage runs on Linux. Watch Settings → Billing
  if PRs get busy; a $0 spending limit makes overruns fail instead of bill.
- **In-app updater.** Release assets in a private repo cannot be downloaded anonymously. The updater has to call
  the GitHub API with a token that can read this repo, i.e. the Credential Manager token used by the `github`
  sync provider (device-code sign-in, or a fine-grained PAT with **Contents: Read** on
  `jrose462/GameProfiles`), and fetch the asset via the API with `Accept: application/octet-stream`. A device
  without such a token will not see updates.
- **Report a problem.** Filing the issue needs the same token with **Issues: Read and write** on the repo.
- **Artifacts and logs** (test results, workflow logs) are visible only to people with access to the repo and
  count toward the account's Actions storage (test-result artifacts here expire after 14 days).
