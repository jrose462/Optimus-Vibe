# Game lab

The game lab adds or refreshes a game's recommended presets for you, using Claude, without you running anything.
It lives in your private config repo as a GitHub Actions workflow that the app installs
(template: [`seed/config-repo/`](../seed/config-repo/)).

## How a request flows

1. **Request.** In the app you ask for a game to be added, refreshed (after a patch), or checked for cloud sync. The
   app files an issue in your config repo titled `[game-lab] <game> - <kind>`.
2. **Evidence from your PC.** The app collects the game's settings files, registry keys, folder listings, install
   exes and its cloud-sync check from your machine and posts them as comments on that issue. Claude cannot see your
   PC; this is how it learns the real key names.
3. **Claude research.** The workflow gives Claude the evidence plus the game-settings-recommender skill from the
   public Optimus-Vibe repo. It researches the game on the web and writes `config/games/<id>.yaml`. If it cannot
   confirm the keys it asks for more evidence (at most 2 rounds): the app answers automatically.
4. **Validation.** A separate job runs the real `optimus-vibe validate` on the result.
5. **Auto-merge.** If validation passes, the workflow opens a pull request and squash-merges it straight away, posts
   a result comment, labels the issue `lab:done` and closes it. If validation fails, the branch is kept, the errors
   are posted, and the issue is labeled `lab:review` + `lab:failed` for you.
6. **Pull.** The app pulls your config repo and the new presets appear.

Issue labels show progress: `lab:queued`, `lab:researching`, `lab:needs-evidence`, `lab:review`, `lab:done`,
`lab:failed`.

## Limits and queue

One request runs at a time and at most `GAME_LAB_DAILY_MAX` (default 5) research runs per UTC day. Extra requests
wait with `lab:queued` and continue the next day, or when you press **Run now** in the app (or Run workflow on
GitHub). Set the repo variable `GAME_LAB_MODEL` to change the model (default `claude-sonnet-5-5`).

## What it costs

- **Claude usage.** Each research run uses your Claude Pro/Max subscription (via the `CLAUDE_CODE_OAUTH_TOKEN`
  secret); that is what the daily limit protects.
- **GitHub Actions minutes.** Private repos get a monthly allowance; beyond it minutes are billed. Research and
  gate/drain jobs run on Linux (1x). The publish job runs on Windows because it runs `optimus-vibe.exe`; Windows
  minutes count **2x**. A typical request is a few minutes of Windows time.

## Privacy

Evidence is posted only in issues of your **private** config repo, by your own GitHub sign-in, and only the repo
owner's issues and comments are acted on. Claude receives it as data, has no shell, and can only write
`config/games/*.yaml` (the workflow rejects anything else). It may search the web and fetch pages for the game, but is
told never to put evidence content into queries. Do not make the config repo public.

## One-time setup (done for you where possible)

- Secret `CLAUDE_CODE_OAUTH_TOKEN` in the config repo.
- Settings > Actions > General > Workflow permissions: **Allow GitHub Actions to create and approve pull requests**.
- Squash merging allowed on the repo (default), and no branch protection that blocks the workflow's merge. If the
  automatic merge is blocked, the issue is labeled `lab:review` and the PR is left for you.
