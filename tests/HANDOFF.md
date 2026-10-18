# Handoff: working on Optimus-Vibe

For a new Claude session (or another account, or a human) picking this up cold. Read this, then the root
[`CLAUDE.md`](../CLAUDE.md), which holds the binding rules. This file is orientation, state and lessons learned; when
the two disagree, `CLAUDE.md` wins. Keep this file current when you change how things work (it goes through a PR like
everything else).

Last updated: 2026-10-18, at v0.5.7.

## What the app is

Optimus-Vibe applies per-device graphics settings to PC games, so one machine (e.g. a desktop with an NVIDIA GPU)
and another (e.g. a ROG Xbox Ally X with an AMD APU) each get their own settings even when Steam Cloud syncs config
files between them. It detects the device, writes the game's config files, registry keys, display mode and GPU driver
profile before launch, and restores the display afterwards. Presets come from Claude (the
`game-settings-recommender` skill, or the game lab in CI) or from in-game captures ("manual saves").

- `OptimusVibe.exe`: WPF app (library grid, presets, saves, settings, game lab, bug reports; controller friendly).
- `optimus-vibe.exe`: CLI, also the Steam launch wrapper (`optimus-vibe run <game> -- %command%`).
- Both are .NET 8 single-file, self-contained win-x64 exes, published by the release workflow. The app updates itself
  from GitHub Releases.

## Two repos

| Repo | Visibility | Holds |
|---|---|---|
| `jrose462/Optimus-Vibe` (this one) | public, MIT | code, tests, docs, workflows, the skill, the seed config (`seed/`) |
| each user's config repo (e.g. the owner's `optimus-vibe-config`) | private | `config/` (devices, games, saves, library, requests) and the installed game-lab workflow |

Never put personal data, tokens, Steam ids, user paths or machine names in this repo (see "Public repo rules" in
`CLAUDE.md`).

## First 10 minutes

```sh
dotnet build OptimusVibe.sln
dotnet test tests/OptimusVibe.Tests          # ~830 tests, ~3 s, no network, no real games/registry
gh auth status                               # you need repo access to open PRs
gh run list -L 5                             # is CI green on main?
gh pr list; gh issue list                    # anything in flight?
```

The tests use fakes (fake GitHub API, fake registry, temp folders); they never touch the real machine except one
Windows Credential Manager test (`LegacyMigrationTests`), which has been flaky once. Re-run before blaming your change.

## Where things are

| Path | What |
|---|---|
| `src/OptimusVibe.Core/Config` | YAML models (`Models.cs`), loader, profile merge, path tokens, VDF |
| `src/OptimusVibe.Core/Handlers` | ini, json, xml, registry, keyvalue, file-snapshot handlers |
| `src/OptimusVibe.Core/Launch`, `Display`, `Driver` | run wrapper, display modes, NVIDIA NVAPI (own P/Invoke) |
| `src/OptimusVibe.Core/Library` | launcher scanners (Steam, Battle.net, EA, Xbox, Epic, GOG), Steam launch options |
| `src/OptimusVibe.Core/CloudSync` | "does this game sync its settings?" detection |
| `src/OptimusVibe.Core/Sync` | git and GitHub-API sync providers, device-code sign-in, Credential Manager |
| `src/OptimusVibe.Core/GameLab` | lab client, setup wizard steps, evidence collector, background cycle |
| `src/OptimusVibe.Core/Feedback` | bug reports (sanitizing in `BugReport.Sanitize`) |
| `src/OptimusVibe.App` | WPF: `ViewModels/`, `Views/`, `Themes/` (palettes + density tokens), `Input/` (XInput) |
| `src/OptimusVibe.Cli` | System.CommandLine verbs |
| `seed/config` | starter config for new users; `seed/config-repo/.github/**` = game-lab files embedded in the exe |
| `.claude/skills/game-settings-recommender` | the preset skill (also used by the lab in CI) |
| `.github/workflows` | `ci.yml`, `release.yml`, `claude-bug-triage.yml` (bug squash), `claude-automerge.yml`, `project-board.yml` |
| `.github/claude` | prompts for the bug squash audit, fix and review |
| `docs/` | `pipeline.md` (diagram), `maintainer-guide.md` (week by week), `ci.md` (secrets/threat model), `game-lab.md` |

## How changes land (enforced by GitHub)

Branch → PR with a Conventional Commits title → CI green → squash-merge → tag. Nobody can push to `main`: the
ruleset **main: PRs and CI** has no bypass. **main: code-owner review** requires the owner's approval for paths in
`.github/CODEOWNERS` (admins may bypass only when merging a PR). Low-risk paths (tests, docs, views, themes,
handlers, ...) are unowned.

- If your PR touches `.github/` or other owned paths, open it and let the owner merge. Do not use admin bypass.
- Release: bump `Version` in `Directory.Build.props` in a PR (`chore(release): X.Y.Z`), merge, then
  `git tag vX.Y.Z && git push origin vX.Y.Z` on `main`. `release.yml` refuses commits not on `main` or without green
  CI, builds, tests, publishes the exes with `.sha256` files, and writes notes grouped by commit type.

## Automation you'll see

- **Bug reports**: the app files public issues (anonymously via a built-in issues-only token, or as the signed-in
  user). Intake labels them `bug-report` and puts them on the project board.
- **Weekly bug squash** (`claude-bug-triage.yml`): Thursdays 00:01–09:30 America/Los_Angeles only (protects the
  owner's Claude usage). It audits each issue (stronger model) and writes a fix on `claude/issue-<n>` (cheaper model,
  file tools only, no shell). A workflow without Claude opens the PR.
- **Auto-merge** (`claude-automerge.yml`): low-risk paths, ≤120 lines, a policy scan and an independent Claude review
  of the diff only. Anything else gets `needs-maintainer-review`.
- **Game lab** (in the user's private config repo): the app files a request issue with evidence from the PC (mapped
  settings files, registry, install listing, cloud check, current config). A gate checks it (one at a time, daily cap),
  Claude researches and writes `config/games/<id>.yaml`, and publish validates it with the real CLI, opens a PR and
  squash-merges it. The app's background cycle (every 2 min while a request is open) pulls the merge and shows
  "New/Updated presets for <game>".

Repo secrets here (names only): `BUG_REPORT_TOKEN` (baked into release exes for anonymous reports),
`CLAUDE_CODE_OAUTH_TOKEN` (the owner's Claude subscription token for the squash), `PROJECT_TOKEN` (project board).
Optional repo variables: `SQUASH_WEEKLY_MAX`, `SQUASH_AUDIT_MODEL`, `SQUASH_FIX_MODEL`, `SQUASH_REVIEW_MODEL`. A
config repo has `CLAUDE_CODE_OAUTH_TOKEN`, `GAME_LAB_DAILY_MAX`, `GAME_LAB_MODEL`.

## Lessons learned (each cost real time)

- **Claude tokens in CI are subscription tokens** from `claude setup-token` (`sk-ant-oat01-...`), not API keys. The lab
  setup rejects anything else, after a GitHub token was once pasted by mistake and every run failed in 2 s with
  `is_error: true` and no message.
- **Free-plan private repos can't enable `allow_auto_merge`**. The lab doesn't need it (its workflow merges directly).
- **`printf "$big" | grep -q` under `set -o pipefail` fails** (SIGPIPE when grep exits early). Use
  `grep -q ... <<<"$var"`. This silently dropped large lab requests.
- **Line endings**: Windows checkouts embed CRLF. Embedded lab files are normalized to LF (`GameLabSetup.ToLf`) so
  the "installed and current" blob-hash check doesn't flag them as outdated.
- **Sign-in state**: signing in again as the same user doesn't change `IsSignedIn`. Listen for
  `GitHubAccountViewModel.SignedIn`. A 401 now shows as signed out.
- **One GitHub sign-in** for the whole app (config repo, lab, reports as yourself). Don't add a second sign-in UI.
- **Daily cap**: the gate posts a dated `<!-- optimus-vibe:lab-queued v1 YYYY-MM-DD -->` note; the app won't
  re-dispatch that request until the next UTC day (otherwise it burns Actions runs).
- **Evidence**: the collector must send the files and registry keys the current config maps
  (`EvidenceCollector.MappedStores`). Name-based guessing alone missed BG3's `graphicSettings.lsx`.
- **Steam launch options** are written to `localconfig.vdf` and only while Steam is closed.
- **Shell on Windows**: PowerShell here-strings and quoting break easily. For commit messages, write a UTF-8 file
  *without BOM* and use `git commit -F`. Python is not installed on the owner's machine. Prefer the Edit tool over
  sed for multi-line or quoted edits.
- **Commit identity**: the owner's GitHub noreply address (see `git log`); never a personal email.

## State on 2026-10-18

- v0.5.7 released. All tests pass. Rulesets are active; classic branch protection is still on as a redundant layer
  (the owner may delete it).
- Game lab verified end to end in the owner's config repo: request → research → validated PR → squash-merge → app
  pull → notification (Aniimo sync-check, BG3 refresh).
- The research prompt was just changed so a refresh works through every `# TODO verify` item using the evidence plus
  web sources. **Not yet observed in a real run**: a BG3 refresh was queued by the daily cap. Check its result and the
  `TODO verify: before -> after` line in the issue's result comment.

## Open items / not yet verified

- The first real Thursday bug squash plus auto-merge on a real report.
- A refresh that actually shrinks `# TODO verify` counts (BG3 had 49; many games have dozens: run
  `grep -c "TODO verify" config/games/*.yaml` in the config repo).
- On a real Ally X: the controller post-game prompt, the compact layout, and AMD driver settings (ADLX was only
  researched; see `docs/amd-adlx.md`).
- Code signing (Windows SmartScreen shows "unknown publisher"): deferred by the owner.
- Optional idea not built: a setting to confirm before pulling lab results instead of pulling automatically.

## Working style the owner expects

- Ask before spending Claude usage (lab runs, agents). At most 4 parallel subagents, the cheapest capable model each.
- Explain plainly and briefly; say what was verified and what wasn't.
- Changes that need a real device: say exactly what to check (see the table in `docs/maintainer-guide.md`).
