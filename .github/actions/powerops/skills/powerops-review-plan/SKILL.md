---
name: powerops-review-plan
description: Use to review an approved PowerOps plan in docs/plans/<name>/ against a Power Platform checklist, in a fresh context. Writes findings to review.md and never asks the user questions.
license: Apache-2.0
compatibility: Needs the powerops CLI on PATH. pac is optional.
---

# powerops-review-plan

Review an approved plan and write `review.md`. The input is the plan name. You work alone: do not ask the user anything. Anything unclear becomes a finding.

## 1. Read only the plan

Read only these:

- `docs/plans/<name>/plan.md`, `decisions.md` and `mockups/` if present
- the repo, for solutions, publishers and existing patterns
- the output of `powerops plan facts` (never run `pac` yourself)

Do not read the planning chat. If it is in your context, say so in `fresh-context` below.

## 2. Check the status

1. Run `powerops plan status docs/plans/<name> --json`. If `powerops` is not found, say so and stop. If the plan is not found or the header is malformed, say so and stop.
2. If `status` is not `approved`, say "the plan is `<status>`, a review needs `approved`", and stop. Write nothing.
3. Note `bodyHash`, `openQuestions` and the plan's `review` value.

## 3. Review against the checklist

Check the plan for each item. Report a gap, a risk or an unanswered area as a finding.

1. Solution boundaries and publisher prefix: one clear solution per deployable unit, a named publisher and prefix, no overlap with solutions from `plan facts`.
2. Connection references and environment variables instead of hard-coded values (URLs, IDs, mailboxes, connection names).
3. Premium connector and licence impact: which connectors and tables need which licence, and who pays.
4. DLP fit: the connectors used must be allowed together. If nobody checked, say so.
5. Dev to Test to Prod route: where it is built, how it moves, who approves.
6. Managed or unmanaged: right for each environment.
7. Data volume and delegation limits: row counts, filters and sorts that may not delegate.
8. Security roles: who can read, write and administer, and whether the plan names roles.
9. Testability: can each step in `Steps` be checked, and is there a way to test before Prod.
10. Unanswered areas and the open-question count: areas never asked (see the `## Log` in `decisions.md`), items under `Open questions` and `Questions for your admin`.

If the plan or `decisions.md` contains a URL, GUID, email or person's name, report it as a finding. Do not repeat the value.

## 4. Write review.md

Write `docs/plans/<name>/review.md`. The first three lines are exactly:

```
plan-hash: <bodyHash>
model: <the model you run on, as your agent reports it, else unknown>
fresh-context: yes|no
```

Write `fresh-context: yes` only if this review runs in a context that does not contain the planning conversation. Be honest. If unsure, write `no`.

Then:

```
## Findings

### High

#### R1. <short title>
- Area: <checklist item>
- Finding: <what is wrong or missing>
- Suggested fix: <what to change in the plan>
- Resolution: open

### Medium
...

### Low
...
```

- Number findings R1, R2, ... across the whole file, not per severity.
- Every finding has all four lines. Start each with `Resolution: open`.
- If a severity has no findings, write "No findings." under it.
- High means the plan should not be signed off as it stands (wrong boundary, missing route, unlicensed premium use, hard-coded tenant values). Medium means a gap the build would hit. Low means polish.
- Before writing, replace URLs, GUIDs, emails and people's names with placeholders such as `<url>`, `<guid>`, `<email>`, `<person>`. Reviews are committed.

## 5. Finish

1. In `plan.md`, set `review: done` in the header, one line only. Do not change the body: that would change the hash and make this review stale.
2. Reply with a short summary for the parent session: the count of High, Medium and Low findings, the open-question count, and the path `docs/plans/<name>/review.md`. Say that the next step is sign-off in `powerops-plan`.

## Per agent

- **Claude Code:** the installed copy of this skill runs in a forked context on the top model, so `fresh-context` is `yes`.
- **Codex and other agents:** a subagent with no chat history gives `fresh-context: yes`. A review run in the planning session gives `no`.
