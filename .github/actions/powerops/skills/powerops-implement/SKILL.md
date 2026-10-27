---
name: powerops-implement
description: Use when asked to build, implement or change a Microsoft Power Platform app, flow, table or solution in this repo. Checks for a signed-off PowerOps plan first and, without one, drafts a plan instead of writing code.
license: Apache-2.0
compatibility: Needs the powerops CLI on PATH. pac is optional.
---

# powerops-implement

Build what a signed-off plan says, and nothing else. Until the gate below passes, write no code and change no solution.

## 1. Find the plan

1. If `powerops` is not found, tell the user to run `dotnet tool install --global PowerOps`, and stop.
2. Run `powerops plan status --json` from the repo root. It lists every plan under `docs/plans/`. The exit code covers all plans, so read the entry of the plan you pick, not the exit code.
3. Pick the plan that matches the request. If more than one fits, ask the user which. If none fits, there is no plan.

## 2. The gate

The gate passes only when both are true:

- the plan's `status` is `signed-off`, or `implementing` (resuming);
- its `violations` list is empty.

Warnings do not block. Mention them (for example "review.md is stale").

If the gate fails, write no code and change no solution:

- **`done`**: the plan was already built. For new work on it, hand over to `powerops-plan` to reopen and re-sign the plan (its section 10); do not draft a second plan.
- **No plan, or not signed off** (`draft`, `approved`): hand over to the `powerops-plan` skill in fast-path mode with the user's request. Say what you are doing in one line.
- **A violation "changed since sign-off"**: explain that the plan body was edited after sign-off. The user either re-signs through `powerops-plan` or reverts the edits. Do not choose for them.
- **Any other violation, or a malformed header** (a `malformed header` violation): show the message and tell the user how to fix it. Do not edit the plan yourself.

## 3. Implement

1. Set `status: implementing` in the plan header. Edit that one line only. Skip this if it already says `implementing`.
2. Read `plan.md` in full, especially `Environments and route`, `Solution and publisher` and `Steps`.
3. Do the `Steps` in order, one at a time. For the build work use Microsoft's power-platform-skills, by what the step builds: Canvas apps `canvas-apps` (`canvas-app`), model-driven apps `model-apps` (`app-builder`, `genpage`), cloud flows `power-automate` (`build-flow`), Power Pages `power-pages` (`create-site`), code apps `code-apps` (`create-code-app`). If no such skill is registered, look for a routing skill, or read the plugin's `SKILL.md` from a checkout of microsoft/power-platform-skills. If you find neither, continue without them.
4. After each step, check it (build, test or inspect, as fits the step). Then commit with the step number in the message, for example `feat: step 2, add the Visit table (plan site-visits)`. Never push unless asked.
5. Keep progress in commit messages. Do not write it to `plan.md`.
6. Never edit the plan body. If a step cannot be done as written, or the plan must change, stop and go back to `powerops-plan`. A changed plan needs a new sign-off.
7. Never write to an environment (import, deploy, publish, create) without the user's explicit go-ahead for that step. Ask each time. This covers every tool, Microsoft's too: the `pac` MCP server's `solution_import`, model-apps `solution-settings.js import`, power-pages `import-solution` and `deploy-pipeline`, and the Dataverse skills' `dv-solution`. To promote exported solutions to another environment, use `powerops deploy plan` and then `powerops deploy apply`, not those tools. Use the plan's `environment` only as the named target. Never run `powerops settings apply --apply` unless the user said yes to that environment in this session: run it without `--apply` first, show the user the target line, and pass the name they approved as `--target`.
8. Do not add features, tables, flows or settings that are not in the plan.

## 4. Finish

1. Run `powerops analyze` on each solution you changed (a path to an unpacked folder or an exported zip). Report the findings. If it could not run, say "not verified" and why.
2. Before you open a pull request, for each cloud flow the plan adds or changes, write or update its tests once the solution is exported and unpacked into the repo, and commit them with that export. Tests are `*.flowtest.json` files in the folder that the repo's PowerOps Action `tests` input names, else `flowtests/` at the repo root: one test for each path through the flow that the plan describes, with a mock for each connector action (see "Flow tests" in the PowerOps README). Use fake values in triggers and mocks; never paste a real run's raw outputs without replacing tenant URLs, ids, emails and names. Run `powerops test <solution> --tests <folder>` and report the result. A test that does not pass blocks the merge in CI: fix the flow or the test, and never delete or weaken a test to make it pass. If a test is `unsupported`, tell the user and let them decide. If the solution is not in the repo yet, say "not verified" and why.
3. Set `status: done` in the plan header, one line only.
4. Tell the user what was built, the commits, and the analyze and test results.

## Per agent

- **Claude Code and others:** the steps are the same. Ask the user plainly in chat before any write to an environment, and wait for the answer.
- **Copilot autopilot:** do not take any environment write without a person's yes. If no person can answer, skip that step and report it.
