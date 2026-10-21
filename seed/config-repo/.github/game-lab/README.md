# Game lab

This folder belongs to the Optimus-Vibe "game lab", installed into this private repo by the app.
`.github/workflows/game-lab.yml` runs when the app files a request issue (title `[game-lab] <game> - <kind>`) and
when it answers with evidence comments collected from your PC. Claude researches the game's settings, writes
`config/games/<id>.yaml`, the workflow validates it with `optimus-vibe validate`, merges it, and the app pulls it.

- `research-prompt.md` is what Claude is told to do. Edit it to change how research behaves.
- Needs the secret `CLAUDE_CODE_OAUTH_TOKEN` (your Claude subscription token) and, in Settings > Actions > General,
  "Allow GitHub Actions to create and approve pull requests". Squash merging must be allowed.

## Settings (Settings > Secrets and variables > Actions > Variables)

| Variable | Default | Meaning |
|---|---|---|
| `GAME_LAB_DAILY_MAX` | `5` | Research runs per UTC day. More requests wait (label `lab:queued`) and start the next day, or when you press Run now in the app / Run workflow here. |
| `GAME_LAB_MODEL` | `claude-sonnet-5-5` | Claude model used for research. |

Labels: `game-lab` (every request), `lab:queued`, `lab:researching`, `lab:needs-evidence`, `lab:review` (a human is
needed), `lab:done`, `lab:failed`. Only the repo owner's issues and evidence comments are acted on.

A separate label, `research-later`, marks tasks the app files when you flag a game "set in-game" (its settings can't be
written from outside). Those issues do NOT carry `game-lab`, so the workflow never starts on them: they wait for a deeper
research pass (see "`research-later` issues" in `research-prompt.md`), which either finds a writable store (a PR that fixes the
mappings and removes `flagged:`) or confirms the game stays set in-game (a PR that sets `mappable: false` plus `flagged:`).
