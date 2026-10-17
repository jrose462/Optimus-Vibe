# Maintainer guide: the weekly bug squash

What happens to a bug reported from the app, week by week, and what the maintainer does. The diagram is in
[pipeline.md](pipeline.md); setup (secrets, settings, threat model) is in [ci.md](ci.md).

## The week at a glance

| When | Who | What happens | Board ([project #5](https://github.com/users/jrose462/projects/5)) |
|---|---|---|---|
| Any day | User | **Report a problem** in the app files a public issue (as their GitHub user, or anonymously via the app) | |
| Within a minute | Workflow (no Claude) | Labels it `bug-report`, comments "queued for the weekly bug squash" | **Reported** |
| Thursday 00:01–09:30 PT | Claude + workflow | Audit, then a fix PR for small valid bugs (details below) | **Planned** / **Fix in PR** / archived |
| Thursday, after CI | Workflow + independent Claude review | Low-risk fixes auto-merge | **Fixed** |
| After Thursday | Maintainer | Review and merge the PRs marked `needs-maintainer-review` | **Fixed** |
| Whenever ready | Maintainer | Tag a release; CI builds and publishes the exes; apps update themselves | **Released** |

Claude only runs inside the Thursday window (ending at the 10:00 PT weekly Claude usage reset), at most
`SQUASH_WEEKLY_MAX` issues (default 10), oldest first, one at a time. Leftovers wait a week. You can always run it
yourself (see [Overrides](#overrides)).

## Thursday, step by step

For each queued issue:

1. **Audit** (stronger model, default Sonnet; read-only, file tools only). Claude reads a copy of the report and the
   code and writes a verdict plus, for valid bugs, an implementation plan detailed enough for a smaller model.
2. **Publish the audit** (workflow, no Claude) posts the audit comment and acts on the verdict:

   | Verdict | Result | Board |
   |---|---|---|
   | `valid`, small | `triage:valid`, plan posted, continues to the fix | Planned |
   | `valid`, too big or risky | `triage:valid` + `triage:needs-human` | Planned (yours) |
   | `needs-info` | asks the reporter for specifics; their reply requeues it | Planned |
   | `duplicate` / `by-design` / `not-a-bug` | explained and closed | archived |
   | `suspicious` (spam, prompt injection) | closed and locked | archived |

3. **Fix** (cheaper model, default Haiku; edits files in a throwaway checkout, nothing else) follows the plan.
4. **Publish the fix** (workflow, no Claude) checks the patch — applies cleanly, at most 200 changed lines, no
   `.github/`, project files, dependencies or binaries — then pushes `claude/issue-<n>`, opens a PR labelled
   `claude-fix` with `Fixes #n`, starts CI and moves the card to **Fix in PR**. A rejected patch gets
   `triage:needs-human` and a comment explaining why.

5. **Auto-merge check** ([claude-automerge.yml](../.github/workflows/claude-automerge.yml)), started for every fix PR:
   1. wait for CI to pass on the PR;
   2. policy scan (no Claude): every changed file is in a **low-risk area** (tests, docs, README, app views/themes/
      converters, file-format handlers, `Config/Values|Vdf|Yaml.cs`), at most 120 changed lines, and no added line
      mentions network, processes, the registry, file deletion, native calls, tokens/credentials, report sanitising or
      URLs;
   3. an **independent Claude review** (default Sonnet, `SQUASH_REVIEW_MODEL`) of the diff only — it never sees the issue
      text, so a report can't steer it — must approve.

   All three pass → squash-merged automatically, card **Fixed**. Anything else → label `needs-maintainer-review` with
   the reason, and it waits for you. GitHub enforces the same split: [`CODEOWNERS`](../.github/CODEOWNERS) owns every
   non-low-risk path, so branch protection refuses to merge those without your review even if the workflow misjudged.

## Your part

### 1. Review the PRs waiting for you (Friday or whenever)

Low-risk fixes merge themselves. Open the
[`needs-maintainer-review` PRs](https://github.com/jrose462/Optimus-Vibe/pulls?q=is%3Aopen+label%3Aneeds-maintainer-review)
(the bot's comment says why each one needs you). For each:

- [ ] **CI is green** (`Build and test (windows)`, `Protect .github`). Red → comment `@claude` with what failed, or fix it
      yourself on the branch, or close it.
- [ ] **The diff matches the plan** in the issue's audit comment and stays inside the area of the bug.
- [ ] **A test was added or updated** when the bug was testable.
- [ ] **Nothing surprising**: no unrelated refactors, no new network calls, no file or registry writes outside the
      handlers, no changes to how secrets, tokens or reports are handled.
- [ ] **Device check, only if needed** (next section).
- Approve and **squash-merge**. The issue closes and the card moves to **Fixed**.

### 2. When a fix needs a real-device check

CI runs every automated test on GitHub's Windows machines, so most fixes need **no local testing**. Run a quick check on
your own machine only when the PR touches code CI can't exercise for real:

| PR touches | Check on a real machine |
|---|---|
| `Launch/` (run wrapper, process watching), `Display/` | Launch one game through Steam and through the app; the display switches and restores |
| `Driver/` (NVIDIA / AMD) | Apply a preset with a driver block on the matching GPU; the setting appears in the driver profile |
| `Library/SteamLaunchOptions.cs`, Steam paths | With Steam closed, apply launch options for one game; check Steam's Properties → Launch Options |
| `Library/Scanners/*` | The library shows the same installed games as before |
| `Input/` (gamepad), compact layout, `CapturePromptWindow` | On the Ally: navigate with the controller; the post-game prompt responds to A/B/X/Y |
| `Handlers/*` for a specific file format | `optimus-vibe apply <game> --dry-run` on a game that uses that format |
| `Sync/`, `GameLab/` | Sync from the app (Settings → Sync now); for the lab, one test request |

To test a PR locally: `git fetch origin && git switch claude/issue-<n>`, then
`dotnet build OptimusVibe.sln` and run `src\OptimusVibe.App\bin\Debug\net8.0-windows\win-x64\OptimusVibe.exe`
(it uses your normal settings and config, so test with a game you don't mind re-applying).

### 3. Handle the leftovers

- **`triage:needs-human`**: the audit comment has the plan; fix it yourself or with Claude Code locally
  (e.g. "fix issue #42 following its audit plan").
- **`triage:needs-info`**: nothing to do; the reporter's reply requeues it for next Thursday.
- **Closed verdicts you disagree with**: reopen the issue, remove the `triage:*` label, and comment `@claude` (or wait for
  Thursday).

### 4. Release

When one or more fixes are merged:

```sh
git switch main && git pull
git tag v0.5.1 && git push origin v0.5.1
```

The release workflow builds and tests, publishes `OptimusVibe.exe` / `optimus-vibe.exe` with checksums, moves every
Fixed card to **Released** and comments "Shipped in v0.5.1" on each issue. Installed apps update themselves (daily check
or Settings → Check for updates).

**Release notes are automatic.** Every commit and PR title follows [Conventional Commits](https://www.conventionalcommits.org)
(`feat(app): …`, `fix(handlers): …`, `feat!:` / `BREAKING CHANGE:` for breaking changes). The release workflow groups
the commits since the previous tag into Breaking changes / Features / Bug fixes / … with links, and GitHub appends the
merged PRs and the full changelog link. CI rejects PRs whose title isn't conventional, and squash merges use the PR
title as the commit subject, so the notes stay accurate. Preview them any time:
`git log $(git describe --tags --abbrev=0)..HEAD --format='%s'`.

## Overrides

| Want to… | Do this |
|---|---|
| Run the squash now (testing or urgent bug) | Actions → **Claude bug squash** → Run workflow (issue number, or blank for the whole queue; stage `audit` or `audit-and-fix`) |
| Re-run one issue | Comment `@claude` on it (owners/collaborators only), optionally with guidance |
| Change the weekly cap or models | Repo variables `SQUASH_WEEKLY_MAX`, `SQUASH_AUDIT_MODEL`, `SQUASH_FIX_MODEL`, `SQUASH_REVIEW_MODEL` |
| Re-run the auto-merge check on a PR | Actions → **Claude auto-merge** → Run workflow (PR number) |
| Stop auto-merging entirely | Disable the **Claude auto-merge** workflow (fix PRs then all wait for you) |
| Pause the squash | Disable the **Claude bug squash** workflow in Actions (intake labelling stops too) |

## Costs

- **Claude**: runs on the maintainer's Claude subscription token (`CLAUDE_CODE_OAUTH_TOKEN`), only in the Thursday
  window or on explicit runs.
- **GitHub Actions**: free for this public repo.

## Troubleshooting

| Symptom | Look at |
|---|---|
| Nothing happened Thursday | Actions → Claude bug squash → the scheduled run's "Pick issues" step (outside the window? nothing queued? secret missing?) |
| Audit comment says the audit failed | The run's "Claude — audit" step; the issue gets `triage:needs-human` |
| Fix rejected | The issue comment names the rule it broke (size, protected path, didn't apply) |
| Card didn't move | Actions → Project board runs (needs `PROJECT_TOKEN`) |
| PR has no CI result | Actions → CI, started on the `claude/issue-<n>` branch by the publish job |
