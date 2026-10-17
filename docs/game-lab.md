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

## In the app: Settings > Game lab

The app does the one-time setup with a re-runnable wizard (one card, each step marked done / next / pending, with a one-line reason and its
action). It uses `GameLabSetup`, the same code as `optimus-vibe lab setup`:

1. **A config repo on GitHub.** The sync provider must be the GitHub account, or a git checkout whose remote is on GitHub; otherwise the
   step explains and points at the Config repo section.
2. **Sign in with the extra permissions.** Runs the device-code sign-in again for `repo workflow project`. With a personal access token the
   fine-grained permissions are listed (Contents, Issues, Actions, Secrets, Variables, Workflows, Administration).
3. Labels, 4. workflow files, 5. **Claude token** (paste the output of `claude setup-token`; sent encrypted to GitHub, never logged or stored
   locally, cleared from the box once used), 6. **daily cap** (default 5) and **model** (Sonnet recommended, Opus, Haiku), 7. repository
   settings, 8. project board (skipped without the `project` scope), 9. a test sync-check on a game that is already configured.

When every required step is done the card shows the status (open, queued, researching, needs evidence, in review) with **Run now**,
**Open board** and **Open repo**.

**Per game.** A game's page offers *Request presets* (no presets here yet), *Refresh presets* and *Re-check settings sync* (configured games).
A confirmation lists what would be sent (counts and tokenized paths, never contents) and says the evidence goes only to your private config
repo. The page shows the request's status (Requested, Researching, Needs evidence, Review, Done) with a link to the issue. Everything is
disabled, with an explanation, until the lab is set up.

**In the background** (app start and after each sync, at most every 5 minutes, off the UI thread, only once the lab is set up): pending
"needs evidence" requests are answered automatically ("Sent the extra details Claude asked for"), queued requests are started, the board is
synced, and when a result is merged the config is pulled and you get "New presets for <game>". Newly detected games without presets get a lab
request automatically (a local toggle, default on) instead of only the `requests.yaml` queue, which stays the fallback when the lab isn't set
up; a game is not requested again for 7 days after the app asked.

## Presets arrive by themselves

Separately from the lab, the app pulls the config repo when it starts, every 15 minutes while open, and when the window is re-activated
(if the last pull is over 5 minutes old). The headless `run` wrapper does a best-effort pull before launching, waits at most about 5 seconds
and skips it if the last pull was under 2 minutes ago; sync problems never delay or fail a launch. A toast appears only when game files
changed or after repeated failures. *Settings > Sync* shows "Last updated" and the **Auto-update presets** switch (`autoPull` in settings.json).

## After the game: keep your changes?

When settings changed in-game, `run` shows a full-screen prompt (it takes the foreground over the Xbox full-screen shell / Big Picture): A saves
(updates the active manual save, or creates one), Y asks for a name (on-screen keyboard), B skips, X stops asking for this game on this device
(`capture-prompt.json` in the data folder; re-enable it on the game's page). It closes without saving after 2 minutes. The CLI keeps a message box.
