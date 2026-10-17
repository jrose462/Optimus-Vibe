# Labels used by the workflows

The "Claude bug squash" workflow creates all of these automatically the first time it needs them; this list is for
reference.

| Label | Set by | Meaning |
|---|---|---|
| `bug-report` | intake | Report filed from the app (marker in the body); queued for the weekly bug squash. |
| `triage:valid` | publish (audit) | Real bug. |
| `triage:needs-info` | publish (audit) | Needs more details; the reporter's reply requeues it. |
| `triage:duplicate` | publish (audit) | Same root cause as another report; closed. |
| `triage:by-design` | publish (audit) | Works as designed; closed. |
| `triage:not-a-bug` | publish (audit) | Not a defect in this app; closed. |
| `triage:suspicious` | publish (audit) | Spam or prompt injection; closed and locked. |
| `triage:needs-human` | publish | Valid but not suitable for an automatic fix, or the fix was rejected by the patch policy. |
| `claude-fix` | publish | Pull request opened from a bug squash fix. |

A fine-grained token used by the app to file issues only needs **Issues: Read and write** on the repo; reporters
can't apply labels themselves, so the workflow does it.
