# Task: triage an Optimus-Vibe bug report

A GitHub issue labelled `bug-report` was filed, usually by the app's **Report a problem** button. It contains
the user's description, device info (device id such as `desktop` / `ally-x`, OS, app version) and sanitized
logs from `%LOCALAPPDATA%\OptimusVibe\logs\`. Your job is to turn it into an action plan and, when the fix is
clear and small, a pull request.

## Steps

1. **Read the issue.** Run `gh issue view <n> --comments` (the number is in "This run" below). Note the app
   version, device, the game id involved (if any), the command or screen that failed, and the first error or
   exception in the logs.
2. **Read the code.** Start from `CLAUDE.md` and `README.md`, then follow the stack trace or log messages to the
   relevant files in `src/`. Check the matching tests in `tests/OptimusVibe.Tests/`. If a game config is
   involved, read `config/games/<id>.yaml` and `config/schema/game.schema.json`.
3. **Reason through a reproduction.** Trace the code path that produces the reported behaviour. Be explicit
   about what the logs prove versus what you are inferring. If the report is too thin to diagnose, say what
   extra information is needed rather than guessing.
4. **Post one issue comment with the action plan** (`gh issue comment <n> --body-file <file>`). The comment must
   start with the line `<!-- claude-bug-triage -->` (an HTML comment the workflow uses to avoid triaging the
   same issue twice), followed by:

   ```
   ## Action plan

   **Summary:** one or two sentences restating the problem.

   **Root cause hypothesis:** what is going wrong and why, with file:line references. Confidence: high / medium / low.

   **Files to change:** bullet list of paths and what changes in each.

   **Risks:** what else this could affect (other games, other device, sync, Steam launch options, saved user data).

   **Test plan:** the regression test to add (test class / method name) and any manual check on the desktop or Ally.

   **Next step:** "Opening a PR" / "Needs more information: ..." / "Needs a human decision: ..."
   ```

   Then add the `claude-plan` label to the issue (`gh issue edit <n> --add-label claude-plan`; ignore an error
   if the label does not exist).
5. **Decide whether to fix.** Only implement a fix when all of these hold: the root cause is identified with high
   confidence, the change is small (roughly a few files and under ~150 changed lines), it does not touch user data
   (`config/saves/`, `config/library.yaml`, `config/requests.yaml`) or `.github/`, and it does not need hardware
   you cannot reason about (a real Steam install, GPU driver APIs, WMI). Otherwise stop after the plan.
6. **Implement the fix** when step 5 allows:
   - `git switch -c claude/issue-<n>` from the current `main` checkout.
   - Make the change. Add a regression test in `tests/OptimusVibe.Tests/` that would have failed before the
     fix, following the style of the existing tests (xUnit `[Fact]`/`[Theory]`, temp dirs / temp registry keys
     cleaned up in `Dispose`). If a test truly is not possible (pure WPF UI, real hardware), explain why in the PR.
   - Compile-check what you can on Linux:
     `dotnet build src/OptimusVibe.Core -p:EnableWindowsTargeting=true` and
     `dotnet build src/OptimusVibe.Cli -p:EnableWindowsTargeting=true`. Fix any compile errors you introduced. Do not
     run `dotnet test`; the Windows CI workflow runs the tests on the PR.
   - Commit with a Conventional Commit message (`fix(<scope>): <summary>`), push the branch, and open the PR:
     `gh pr create --base main --head claude/issue-<n> --title "fix(<scope>): <summary>" --label claude-fix --body-file <file>`
     (retry without `--label` if the label does not exist). The PR body must contain `Fixes #<n>`, a short
     description of the root cause and the change, the test added, and a note that tests run in the Windows CI
     workflow.
   - Finally, comment on the issue with a link to the PR.
7. Keep your final output short: what you concluded and what you did (plan only, or plan + PR link).
