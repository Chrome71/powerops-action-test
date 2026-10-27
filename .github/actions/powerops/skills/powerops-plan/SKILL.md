---
name: powerops-plan
description: Use when someone wants to plan, scope, design or mock up a Microsoft Power Platform app, flow, Dataverse table or solution (an idea, a screen, a wireframe, a prototype), or asks to build one without a signed-off plan. Turns the idea into a signed-off plan in docs/plans/ through short question rounds. Run it before any Microsoft build skill (app-builder, genpage, canvas-app, build-flow, create-site).
license: Apache-2.0
compatibility: Needs the powerops CLI on PATH. pac is optional.
---

# powerops-plan

Turn an idea into a plan the user has signed off, in `docs/plans/<name>/`. Write no code and change no solution. Planning is read-only towards the tenant.

## 1. Interactive only

Planning needs a person who answers. Stop if any of these is true:

- You run under an autopilot mode that answers questions for you.
- You run as a scheduled or headless job told to proceed without asking.
- The user said "decide for me".

Then say: "Planning needs a person to answer the questions." Write nothing more and stop.

Never answer your own questions. After you ask, end your turn and wait.

## 2. Start or resume

1. Check that `powerops` runs (for example `powerops --version`). If it is not found, tell the user to run `dotnet tool install --global PowerOps`, and stop.
2. Pick a plan name in kebab-case from the idea (`site-visits`). The folder is `docs/plans/<name>/`.
3. If the folder exists, read `plan.md` and `decisions.md`, then run `powerops plan status docs/plans/<name> --json`. If it exits 2, show the reason and fix the header with the user first. Keep the `depth` from the header (section 6). Then continue where the plan stopped:
   - `draft`: go to the what-next menu (section 6).
   - `approved`: go to the review and sign-off steps (sections 7 and 9).
   - `signed-off`, `implementing`, `done`: tell the user any change to the plan body voids the sign-off. Continue only if they want changes (section 10).
4. If it does not exist, first check `docs/plans/` for a plan on the same idea. A `draft` or `approved` one: resume it. A `signed-off`, `implementing` or `done` one: if the user wants a new version, follow "New version" in section 10. Otherwise create the folder and go to section 3.

## 3. Environment facts

Run `powerops plan facts` once. Never run `pac` yourself.

- Output with `"available": true` lists the target environment and the existing solutions (names, versions, managed flag). Use it to answer questions instead of asking them.
- If `available` is false, tell the user the `reason` and the `fix` in your next message, in one line. Do not run the fix. Add a line to Assumptions in `plan.md` that environment facts were not available. Carry on.
- The facts do not contain the publisher prefix. Take it from the repo if a solution there shows it. Otherwise ask.
- Offer the facts' environment as the target in a question. Write `environment:` in the header only after the user confirms it.
- Never write any other environment's name in a plan file. Refer to the others by role (Dev, Test, Prod). Never offer other environments as targets unless the user names them.
- In `decisions.md`, write "the environment found by plan facts" instead of its name.

## 4. Tenant data

Plans are committed with the code. Before you write any plan file, replace URLs, GUIDs, email addresses and people's names that came from answers, facts or pasted text with placeholders such as `<url>`, `<guid>`, `<email>`, `<person>`. This applies to answers in `decisions.md` too. Say once, at the first write: "I replace URLs, IDs, emails and names with placeholders, because plans are committed." `powerops plan status` scans the folder and flags what you miss.

## 5. Files

Create `plan.md` with exactly this header, then these sections in this order:

```
---
status: draft
review: pending
signed-off: null
signed-off-hash: null
environment: null
depth: standard
supersedes: null
---

# <Title>

## Goal
## Users
## Data
## Automation
## Screens
## Environments and route
## Solution and publisher
## Constraints
## Assumptions
## Open questions
## Questions for your admin
## Steps
```

- `Constraints` holds licensing, DLP and deadlines.
- `Steps` is a numbered list of implementation steps: small, in order, each one checkable.
- `Open questions` and `Questions for your admin` hold one top-level `- ` item per question. When there are none, leave the section empty. Do not write "None": the tool counts list items. Move an item out when it is answered.
- Questions only someone else can answer (licensing, DLP) go under `Questions for your admin`, so the user can forward them.
- Edit the header line by line, and only `status`, `review`, `environment`, `depth` and `supersedes`. Never write `signed-off` or `signed-off-hash` by hand. Only `powerops plan sign-off` does.

Create `decisions.md` in exactly this format:

```
# Decisions: <name>

## Round 1

### Q1. <question>
1. <option> (recommended)
2. <option>
3. In your own words
Answer: <the user's answer>

### Q2. <question>
...
```

- At most three `### Q` per round. Numbering continues across rounds (Q4 starts round 2).
- The recommended option is always option 1. "In your own words" is always the last option.
- For `Answer:`, write the text of the option chosen, or the user's own words (redacted, see section 4).
- Everything that is not a question goes as a `- ` line under a `## Log` heading at the end: approvals, "approved without: environments, constraints", fast-path choices, sign-off. Add new rounds above `## Log`, and new log lines at the end of it (oldest first).

## 6. Question rounds

Each round:

1. Ask at most three questions. Each has two or three options, the recommended one first, plus "in your own words".
2. Use the agent's choice tool if it has one. Otherwise print the numbered options as plain text and tell the user how to answer: "Answer like `1 2 1`, or in your own words." Then end your turn.
3. Right after the answers, append each question and answer to `decisions.md`.
4. Update `plan.md`. Put assumptions and open items in the plan, not in chat.
5. Show exactly three lines, one per change to the plan. Nothing else.
6. Ask what next, with lettered options: **A** another round, **B** approve the draft, **C** stop for now. End your turn.

Question order: outcome and users, then data, then automation, then environments and ALM, then constraints. Two rules keep late areas from being skipped:

- Ask one licensing question in round 1 or 2.
- Ask the environments question (target, route Dev to Test to Prod) by round 3.

Do not ask what the facts already answer. When nothing is left to ask, say so and recommend B. The user decides.

**Depth.** `depth` in the header sets how far the rounds go:

- `quick`: one round, at most three questions, only what blocks the plan, with licensing and environments asked in it. Keep sections short. Recommend B as soon as nothing blocks.
- `standard`: the rounds as written above.
- `deep`: more rounds are fine. Also ask about edge cases, data volume and delegation, security roles and testability. Write fuller Steps.

A new plan starts at the `depth` of the plan it supersedes, else of the `docs/plans/*/plan.md` with the newest modification time that has one, else `standard`. The fast path (section 8) sets `quick`.

After each round, judge engagement from the answers. Mostly one-word or option-number answers, "just pick", "whatever you think", or a request to speed up: go one level down. Long answers in their own words, follow-up questions, or a request for more detail: one level up. Change it at most once per round. Write the new value in the header, log `depth: <old> -> <new> (<reason in a few words>)` under `## Log` in `decisions.md`, and mention it in one of the three change lines.

**A:** run another round. **B:** section 7. **C:** say where the files are (`docs/plans/<name>/`) and how to resume ("ask me to continue the `<name>` plan"). The status stays `draft`.

## 7. Approve (B)

1. Run `powerops plan status docs/plans/<name> --json`. Show the open-question count and which of the five areas (users, data, automation, environments and ALM, constraints) were never asked.
2. If any area was never asked, ask one confirmation: approve anyway, or ask about it first. Wait.
3. If `Steps` is empty, draft it from the plan and tell the user.
4. Log the approval under `## Log`. If areas were skipped, log `approved without: <areas>`.
5. Set `status: approved` in the header.
6. Recommend a review in a fresh context with the `powerops-review-plan` skill, and start it as follows:
   - Claude Code: invoke the `powerops-review-plan` skill. Its installed copy runs in a forked context on the top model.
   - Codex and other agents that can start a subagent with no chat history: start one that runs `powerops-review-plan` with only the plan name.
   - Otherwise tell the user to open a new session and run `powerops-review-plan <name>` there. Then wait.
7. If the user prefers to continue in this session, run the review here. The review then records `fresh-context: no`.

An approved draft may still have open questions. The review and the sign-off both show the count.

## 8. Fast path

Use this when the user asks to build without a signed-off plan, or `powerops-implement` hands over. Write no code and change no solution.

1. Draft `plan.md` from the request, the repo and `powerops plan facts`. Put every guess under Assumptions. The status stays `draft`. Set `depth: quick`.
2. Ask one question: the route. Use the numbered format of a round question (section 6): two or three options, each with its trade-off in a few words, recommended first, then "In your own words". Example: unmanaged in Dev, then managed to Test and Prod through pipelines.
3. Ask the publisher prefix too, only if neither the facts nor the repo supply it. Ask nothing else. Leave `environment: null` and note the assumed target in Assumptions.
4. Log the route in `decisions.md` as a normal question.
5. Show one lettered menu:
   - **A** approve the draft, then review and sign-off as usual (section 7, from step 3).
   - **B** approve, skip the review and sign off now. Log it, set `status: approved` and `review: skipped`, then do section 9 step 4 (no second menu: this answer is the sign-off). The Assumptions list stands in for the coverage check.
   - **C** ask question rounds instead (section 6).

A route answer approves nothing. A bare "approve" means A.

## 9. Sign-off

Run this after the review, or when the user says "sign off".

1. Run `powerops plan status docs/plans/<name> --json`.
2. Show: the open-question count; the unresolved findings in `review.md` (those with `Resolution: open`, by R-number); and the first three lines of `review.md`.
3. Ask once, lettered: **A** sign off now, **B** go back (resolve findings, or more rounds). When there is no `review.md`, add **C** skip the review and sign off: set `review: skipped` and log it.
4. On A or C, run `powerops plan sign-off docs/plans/<name>`, then `powerops plan status docs/plans/<name>`, and show the result.
5. If sign-off refuses, show why and fix the cause with the user.

To resolve a finding, change the plan if needed and set its line in `review.md` to `Resolution: fixed` or `Resolution: accepted`. Add a short reason after it.

## 10. After sign-off

Any change to the plan body voids the sign-off. If the user wants changes, set `status` back to `draft` (more rounds) or `approved` (small edit), make the change, and sign off again with the tool. The review may then be stale: `plan status` warns about it. Offer a new review.

**New version.** When the plan is `signed-off`, `implementing` or `done` and the user wants a new version (new scope, a v2, changes after implementation), do not edit it and do not start from scratch:

1. Create `docs/plans/<name>-v2/` (or the next free `-vN`) and copy the previous `plan.md` body into it.
2. Set the header (resetting `signed-off` and `signed-off-hash` to `null` is the one hand edit of them allowed): `status: draft`, `review: pending`, `signed-off: null`, `signed-off-hash: null`, the same `environment` and `depth`, and `supersedes: <previous folder name>`.
3. Start `decisions.md` with a `## Log` line `supersedes <previous>`.
4. Ask questions only about what changes. Keep everything else.
5. Leave the old plan as it is.

A small edit to a plan that is `signed-off` but not yet `implementing` follows the paragraph above.

## 11. Mockups

When the user asks for a screen, wireframe, mockup or prototype:

- Write static HTML with inline CSS only. No scripts, fonts, images or external links.
- Use fake data only. Show a visible "Sketch" label.
- Write only to `docs/plans/<name>/mockups/`. Never publish or upload it.
- Apply section 4 to the HTML too.

## 12. Never write to an environment

Planning only reads. Do not import, deploy, create or change anything in a tenant.

## Per agent

The plain-text fallback always works: numbered options, a line telling the user how to answer, then end your turn.

- **Claude Code:** use `AskUserQuestion`. It adds "other" for free text by itself.
- **Codex:** `request_user_input` works only in Plan mode (1 to 3 questions, 2 or 3 options). Elsewhere use plain text.
- **Gemini CLI:** `ask_user` is available. Plain text is fine.
- **GitHub Copilot:** autopilot answers `ask_user` on its own. If autopilot is on, stop (section 1). Otherwise use `ask_user` or plain text.
