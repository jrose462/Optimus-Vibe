# Labels used by the workflows

| Label | Color | Used by | Meaning |
|---|---|---|---|
| `bug-report` | `d73a4a` | App "Report a problem"; `claude-bug-triage.yml` trigger | Bug filed from the app (description, device info, sanitized logs). The triage workflow adds it itself. |
| `claude-plan` | `5319e7` | optional, set by a human | Claude has posted an action plan comment (the workflow no longer sets it; the plan comment marker is what automation reads). |
| `claude-fix` | `0e8a16` | optional |  Pull request opened by Claude for a bug report. |

Create them once (needs the `gh` CLI signed in with access to the repo; `--force` updates an existing label):

```sh
gh label create bug-report --color d73a4a --description "Bug filed from the app's Report a problem" --force -R jrose462/Optimus-Vibe && gh label create claude-plan --color 5319e7 --description "Claude posted an action plan" --force -R jrose462/Optimus-Vibe && gh label create claude-fix --color 0e8a16 --description "PR opened by Claude for a bug report" --force -R jrose462/Optimus-Vibe
```

Create them before the first report so they get these colors and descriptions (GitHub may otherwise create a
label implicitly, or drop it, depending on the token). A fine-grained token used by the app to file issues needs
**Issues: Read and write** on the repo to apply labels.
