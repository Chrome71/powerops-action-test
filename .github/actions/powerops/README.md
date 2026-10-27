# PowerOps

A free, open-source, cross-platform toolkit that brings software-engineering discipline (analysis, diffing, testing, repeatable deployment) to Microsoft Power Platform. It works alongside `pac`; it does not replace it.

**Status:** early development, not yet released. Built so far: `analyze`, `baseline`, `rules`, `diff`, `settings` (`generate`, `validate`, `apply`), `drift`, `deploy plan`, `deploy apply`, `plan` and `skills`.

## Install

```sh
dotnet tool install --global PowerOps
powerops --version
```

Until the first release is on NuGet, install from a clone instead:

```sh
dotnet pack src/PowerOps.Cli -c Release -o artifacts && dotnet tool install --global --add-source artifacts PowerOps
```

## Quick start

In any CI system that has the .NET SDK, one `run:` line writes a SARIF report and fails the build on errors:

```sh
dotnet tool install --global PowerOps && powerops analyze --format sarif --out powerops.sarif
```

On GitHub Actions, use the action. It writes a job summary and annotations, and uploads the findings to code scanning:

```yaml
permissions:
  contents: read
  security-events: write   # for the SARIF upload

steps:
  - uses: actions/checkout@v7
  - uses: Chrome71/PowerOps@v1
    with:
      path: solutions        # default: .
      fail-on: error         # error | warning | none
      # baseline: powerops-baseline.json
      # tests: flowtests     # *.flowtest.json files to run; see "Merge gate: flow tests"
```

Until `v1` is tagged, use `Chrome71/PowerOps@main` (the action builds PowerOps from source). Pull requests from forks cannot upload SARIF (their token has no `security-events` permission); they still get the job summary and the annotations.

### Merge gate: flow tests

With `tests` set to a folder of `*.flowtest.json` files ([Flow tests](#flow-tests)), the action runs `powerops test <path> --tests <folder>`, writes the report to the job summary, adds an error annotation for each test file that did not pass, and its last step fails the job when the exit code is not `0`: a test `failed`, is `unmocked` or `unsupported`, or no test file or solution was found. It needs no tenant and no secret, so it runs on pull requests from forks too. GitHub shows at most 10 error annotations per step; the job summary lists every test. The annotations show on the check run, and beside the file in "Files changed" only when the test file is in the PR's diff.

Run the tests on committed files only, never on an export from an environment: the report quotes values from the solution and the test files. It gates only the flows that have test files, and only when `tests` is set: with `tests` empty the action runs no tests, and the job summary has no "Flow tests" section. A release of the action from before v0.15.0 ignores the input with a warning.

A failing test blocks a merge only when the job is a required status check. A workflow for it:

```yaml
name: PowerOps flow tests
on: pull_request   # no filter: see below. With a merge queue, add merge_group.

permissions:
  contents: read

jobs:
  powerops-flow-tests:   # the name of the required check; no other workflow may have a job of this name
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - uses: Chrome71/PowerOps@v1
        with:
          path: solutions
          tests: flowtests
          fail-on: none          # the check fails on flow tests only; remove this line to gate the analysis too
          upload-sarif: 'false'  # so the job needs only contents: read
```

Run it once, so GitHub knows the check. Then, in the repository's settings, add a ruleset (Rules, Rulesets, New branch ruleset) for the default branch, or a branch protection rule, with "Require status checks to pass" and the check `powerops-flow-tests` from GitHub Actions. GitHub's docs give the reasons for these rules:

- The check is named after the job (its `name:`, else its id). The same job name in several workflows makes status check results ambiguous and can block pull requests, so give it a name no other workflow uses.
- Put no path, branch or commit-message filter on the workflow: a workflow skipped by one leaves its required check pending, and the PR cannot be merged. Put no `if:` on the job either: a job skipped by a condition reports success, so the PR could merge untested.
- With a merge queue, add `merge_group` to `on:`. Otherwise the required check is never reported for the queued merge, and the merge fails.

A pull request runs its own workflow file and test files, so a PR could remove the gate: protect `.github/workflows/` and the tests folder with CODEOWNERS and required code-owner reviews, or with a ruleset. Workflows of a fork PR may wait for a maintainer's approval (the repository's Actions setting for fork pull request workflows); until then the check is pending.

### Pull request comment

With `comment: 'true'` on a `pull_request` event, the action posts one pull request comment and edits it on each later run (it finds its own comment by a hidden marker and the `github-actions[bot]` author, so a PR keeps one PowerOps comment). It is off by default, so existing workflows do not start needing `pull-requests: write`. What the comment holds depends on `target`:

- With `target` (and `deploy-folder`) set, it holds the `deploy plan` of the zips in `deploy-folder` against that environment, and the plan file is written to `deploy-folder/powerops-plan.json` (the `plan-file` output names it). `pac` and `az` must be signed in by earlier steps; the action signs in to nothing. The step records the plan's exit code, writes the comment, and then a last step fails the job when the plan is blocked or could not run. If `deploy plan` fails (exit `2` or `3`), the comment says only that the plan could not run and gives the exit code; the log shows only powerops' own `error:` lines, because text forwarded from `az` and the Web API can name tenant ids and the environment URL. Set the runner's debug logging on (`RUNNER_DEBUG`) to see the forwarded text.
- Without `target`, it holds the `diff` of the PR's base and head solution (`path` must name one solution, relative to the repository root). The diff reads the first parent of the checked-out commit, so it needs the default `actions/checkout` of a pull request, which checks out the merge commit, with the default `persist-credentials` (the script fetches the base commit). If the solution is new in the PR, `path` holds several solutions, or the fetch fails, the comment says that the diff could not run and which step failed.
- A PR from a fork gets no sign-in and no secrets, and its token cannot comment. For a fork PR the action skips the plan and writes the diff to the job summary only; the run passes.
- With `plan-file` set (the path of a plan file written by an earlier `deploy plan`; its folder holds the zips and settings files), the action runs `powerops deploy apply` against `target` instead of planning. `apply: 'true'` adds `--apply` and imports; any other value is a dry run that imports nothing. `target` is required, and the step fails with an error on a fork pull request or on `pull_request_target`. The log and the job summary show the report; as for the plan, only powerops' own `error:` lines are logged unless debug logging is on, and a non-zero exit fails the job with one message per case (1 refused or a flow not as expected, 2 usage error or `--target` mismatch, 3 failed, with no rollback). The report steps run first; then `fail-on` gates the apply: the action analyzes `path` again with the real `fail-on` (and `baseline`) before `deploy apply`, and a failure there fails the job before anything is imported. With `tests` set too, a failing flow test also stops the apply.
- A comment over 65,000 bytes is replaced by a short note, and the full report goes to the job summary. If the comment cannot be posted (a read-only token, for example on a Dependabot run), the action writes a warning and the job summary and does not fail the job.
- The plan Markdown always goes to the job summary. The comment goes there instead of the PR only for a fork PR, a body over the size cap, or a failed post. The comment is public in a public repository: it shows the target's friendly name, solution, file and flow names and versions, never a setting's value or a connection id.
- The scripts use `gh` and `git`, which GitHub-hosted runners have installed; a self-hosted runner needs both. `pull_request_target` is not supported.

An example workflow. The plan job runs in a GitHub environment (`test-plan`) whose environment secrets hold the target's URL, the tenant id, the app id and one secret for each value the settings file needs. The settings file is built at run time with `jq` under `$RUNNER_TEMP`, never printed, and left out of the artifact. Sign-in and plan run only for same-repository PRs. Fake values:

```yaml
name: Deploy plan
on: pull_request

permissions:
  contents: read
  pull-requests: write   # the comment
  id-token: write        # the OIDC sign-in

concurrency:
  group: powerops-plan-${{ github.event.pull_request.number }}   # two quick pushes must not both create a comment
  cancel-in-progress: true

jobs:
  plan:
    runs-on: ubuntu-latest
    environment: test-plan
    env:
      SAME_REPO: ${{ github.event.pull_request.head.repo.full_name == github.repository }}
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - uses: actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0
        with:
          dotnet-version: 10.0.x

      # Masking matches exact text only, so every form of the target's host is masked before any sign-in.
      - name: Mask the target URL's forms
        if: env.SAME_REPO == 'true'
        env:
          URL: ${{ secrets.TEST_ENVIRONMENT_URL }}
        run: |
          host="${URL#*://}"; host="${host%%/*}"
          for v in "https://$host" "https://$host/" "$host" "${host,,}" "${host^^}" "${host%%.*}"; do echo "::add-mask::$v"; done

      # The deploy folder holds the zips and, beside each, its settings file.
      - name: Build the deploy folder
        if: env.SAME_REPO == 'true'
        env:
          SITE_URL: ${{ secrets.CONTOSO_SITE_URL }}
          CONNECTION_ID: ${{ secrets.CONTOSO_SHAREPOINT_CONNECTION_ID }}
        run: |
          mkdir "$RUNNER_TEMP/deploy"
          cp exports/*.zip "$RUNNER_TEMP/deploy/"
          jq -n --arg site "$SITE_URL" --arg conn "$CONNECTION_ID" '{
            EnvironmentVariables: [{SchemaName: "contoso_SiteUrl", Value: $site}],
            ConnectionReferences: [{LogicalName: "contoso_sharepoint", ConnectionId: $conn, ConnectorId: "/providers/Microsoft.PowerApps/apis/shared_sharepointonline"}]
          }' > "$RUNNER_TEMP/deploy/ContosoApp_managed.settings.json"

      - name: Install pac
        if: env.SAME_REPO == 'true'
        run: |
          dotnet tool install --global Microsoft.PowerApps.CLI.Tool
          echo "$HOME/.dotnet/tools" >> "$GITHUB_PATH"

      - uses: azure/login@<full commit SHA> # use the latest release of azure/login
        if: env.SAME_REPO == 'true'
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          allow-no-subscriptions: true   # the app has no Azure subscription, only the environment

      - name: Sign in pac
        if: env.SAME_REPO == 'true'
        env:
          TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
          CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
          ENVIRONMENT_URL: ${{ secrets.TEST_ENVIRONMENT_URL }}
        run: |
          # pac's sign-in output stays out of the public log unless debug logging is on.
          if ! pac auth create --githubFederated --tenant "$TENANT_ID" --applicationId "$CLIENT_ID" --environment "$ENVIRONMENT_URL" > "$RUNNER_TEMP/pac-auth.log" 2>&1; then
            echo "::error::pac auth create failed; its output is printed only with debug logging on"
            if [ "${RUNNER_DEBUG:-}" = 1 ]; then cat "$RUNNER_TEMP/pac-auth.log"; fi
            exit 1
          fi

      - uses: Chrome71/PowerOps@v1
        with:
          path: solutions/ContosoApp        # the unpacked solution: analyzed, and diffed on a fork PR
          tests: flowtests                  # flow tests of that solution, also on a fork PR
          comment: 'true'
          deploy-folder: ${{ runner.temp }}/deploy
          target: ${{ env.SAME_REPO == 'true' && secrets.TEST_ENVIRONMENT_URL || '' }}

      # The zips and the plan file, for a later apply job; never the settings files.
      - uses: actions/upload-artifact@<full commit SHA> # use the latest release of actions/upload-artifact
        if: always() && env.SAME_REPO == 'true'
        with:
          name: powerops-plan
          path: |
            ${{ runner.temp }}/deploy/*.zip
            ${{ runner.temp }}/deploy/powerops-plan.json
```

A complete Dev to Test promotion (a plan on the PR, then an apply in an environment with required reviewers) is in [docs/examples/](docs/examples/README.md); read its README for the setup and the risks.

An opt-in recipe that gives each pull request its own short-lived environment, with a sweep for leftovers, is in the same folder. It was not run end to end: read [its section](docs/examples/README.md#example-an-ephemeral-environment-per-pull-request) first, which opens with the risk.

Pin every action to a full commit SHA, as above. In a public repository, anyone who can push a branch can run code as the identity the plan job signs in with, so give the app registration of the plan job a read-only role in the target, not an administrator role.

## Usage

```sh
powerops analyze                      # find solutions under . (unpacked XML folders and exported .zip files)
powerops analyze path/to/solution.zip path/to/unpacked --format json --out report.json
powerops rules                        # list every rule with its severity, title and fix (add --json for machine output)
powerops diff old.zip new.zip         # compare two versions component by component (zip or unpacked folder; --format text|json|markdown, --out <file>)
powerops test path/to/solution --tests tests/flows   # run offline flow tests (*.flowtest.json) against the cloud flows, with mocked connector actions (--format text|json|markdown, --out <file>)
powerops settings generate path/to/solution --out deploymentsettings.json   # pac deployment settings with empty values to fill in
powerops settings validate path/to/solution --settings deploymentsettings.json   # missing or extra entries, empty connection ids and values
powerops settings apply solution.zip --settings deploymentsettings.json   # dry run: the target, the pac command, each cloud flow new or existing
powerops settings apply solution.zip --settings deploymentsettings.json --apply --target "Contoso Dev"   # import through pac, then check the flows are on
powerops drift --solution MySolution --env <dev> --env <test>   # compare one solution across environments or saved snapshot files (--save <dir>, --format text|json|markdown, --out <file>)
powerops drift --solution MySolution --env <dev> --env <test> --save snapshots/   # the same, and write snapshots/MySolution.<environment name>.json for each environment
powerops drift --solution MySolution --env "snapshots/MySolution.Contoso Dev.json" --env "snapshots/MySolution.Contoso Test.json"   # compare saved snapshots, offline
powerops deploy plan exports/ --target <test>   # what importing the solution zips in exports/ would do to the target: order, install, upgrade or skip, deletions, layers, settings, flow states (reads the target only)
powerops deploy plan exports/ --target <test> --save exports/powerops-plan.json --format markdown --out plan.md   # the same, with the Markdown for a PR and the plan file from one read
powerops deploy apply exports/powerops-plan.json --target <test>   # dry run: plan again against the target, refuse if it differs from the reviewed plan, say what would be imported
powerops deploy apply exports/powerops-plan.json --target <test> --apply   # import the reviewed plan through pac, then check each cloud flow
powerops baseline                     # accept today's findings: writes powerops-baseline.json
powerops analyze --baseline powerops-baseline.json   # report only findings that are not in the baseline
```

`analyze` is offline and read-only. Options: `--format text|json|sarif|markdown` (default `text`), `--out <file>` (write the report to a file instead of stdout), `--fail-on error|warning|none` (default `error`), `--baseline <file>` (drop findings listed in a baseline file before reporting and before the exit code; the summary counts them as `baselined`). It reads unpacked XML folders (`Other/Solution.xml`), exported zips and the YAML layout that Dataverse Git integration commits: point it at the folder that holds `solutions/`, and each `solutions/<name>/solution.yml` is reported as its own solution, with its components picked by its `solutioncomponents.yml`. Canvas app `.pa.yaml` formulas are read only in the YAML layout (PO014). Every finding carries a one-line `fix` and a `helpUri` to its docs page in JSON and Markdown output, and SARIF rules carry the fix as `help`. Every JSON output carries a `schemaVersion`; the schemas are in [docs/schemas/](docs/schemas/).

`baseline [path...] [--out <file>]` runs the same analysis and writes the findings it found to a JSON file (default `powerops-baseline.json`). A finding matches the baseline on solution unique name, rule id, component and message, not on file, so a baseline made from an exported zip also works for the unpacked folder. An unreadable or invalid baseline file is a usage error (exit `2`).

Exit codes: `0` no findings at or above `--fail-on`, `1` findings at or above it, `2` usage error (bad option, missing path, no solutions found), `3` unreadable solution or internal error (set `POWEROPS_DEBUG=1` for a stack trace).

`diff <old> <new>` is offline and read-only. It loads both solutions and compares the solution unique name, version, managed flag and publisher, root components, missing dependencies, connection references, environment variables, flows and canvas apps. File paths, ordering, flow JSON key order and whitespace, and XML whitespace in canvas app metadata are not differences, so a folder and its exported zip compare equal. Classic workflows and business rules are compared by name, category and state only (their XAML is not read), and components it does not read yet (tables, views, forms and so on) are not compared. Environment variable current values are reported as changed, added or removed but never printed. A canvas app whose `AppVersion` timestamp changed is reported as republished, since an edit changes the app package and the timestamp. Some changes carry a risk label: `removed-component` (a managed upgrade deletes components that are no longer in the new version, [per Microsoft Learn](https://learn.microsoft.com/en-us/power-platform/alm/solution-concepts-alm)), `flow-state` (a cloud flow was turned on or off), `connection-reference` and `environment-variable` (added or removed). Options: `--format text|json|markdown` (default `text`) and `--out <file>`. Markdown is cut at a whole row below 60,000 characters, with a note on how many changes were left out, so it fits in a pull request comment. JSON follows [docs/schemas/diff.schema.json](docs/schemas/diff.schema.json). Exit codes: `0` no differences, `1` differences found, `2` usage error (bad option, missing path), `3` unreadable solution or internal error (set `POWEROPS_DEBUG=1` for a stack trace).
`settings generate <solution> [--out <file>]` prints a `deploymentsettings.json` with the solution's environment variables and connection references, sorted by name, with empty `Value` and `ConnectionId` to fill in (stdout, or a file with `--out`). `settings validate <solution> --settings <file> [--format text|json]` reports entries missing from the file, entries not in the solution, names listed more than once, connection references without a `ConnectionId`, environment variables with no value in the file and no default or current value in the solution, and any `"Value": ""`, which `pac solution import` rejects. To keep a variable's default or current value, leave its entry out of the file (observed with `pac` 2.13.1); `settings generate` writes every variable with an empty value, so fill each one in or remove it. Fields the solution does not need (the extras `pac` writes) are ignored. Exit codes: `0` clean, `1` problems, `2` usage error, `3` unreadable solution or invalid settings file.

`settings apply <zip> --settings <file> [--apply --target <name>] [--format text|json]` deploys an exported solution zip with a deployment settings file through `pac`. Without `--apply` it is a dry run that writes nothing: it runs `settings validate`, reads pac's current environment with `pac org who`, reads each cloud flow's state there with `pac power-automate list-cloud-flows`, and prints the target environment name, whether the zip is managed, the `pac solution import` arguments (the environment id shows as `<environment id>`) and each flow as `new` (with the state the import should give it) or `exists` (with its state, which the import keeps). `--apply --target <name>` imports into the pac profile's current environment, and only when `<name>` equals that environment's friendly name; every later `pac` call is pinned to that environment's id, which is never printed. Friendly names are not unique, so check the profile with `pac auth list` before saying yes to a target. The import is `pac solution import --environment <id> --path <zip> --settings-file <file>` with pac's defaults: unmanaged customisations are not overwritten, changes are not published and there is no upgrade, so components removed from the solution stay in the target. Afterwards each flow is reported `on`, `off`, `suspended` (for example, a data policy blocks it) or `missing`. `pac` cannot turn a flow on, so powerops does not either: a new flow comes back in its export state (on only if it was on at export and its connection references get connections), an existing flow keeps its state, and a flow stays off when the importing user does not own or have access to every connection it uses (`ConnectionAuthorizationFailed`). A solution with a flow that was off at export therefore always exits `1`; that is by design, because the command checks that the flows are on. A user who cannot list flows sees every flow as `new` before and `missing` after. The import turns the solution's flows off and on again while it runs, so a running flow is briefly interrupted. It can take up to an hour (powerops waits 60 minutes): run it in the background or with a long command timeout; stopping powerops does not stop an import already running in the environment. The flow check makes two `pac` reads per flow, and a read slower than 30 seconds ends the run with exit `3`. There is no rollback: on a failure, fix the cause and run again. When the import fails, the report holds pac's exit code and pac's own output goes to stderr. In text mode with `--apply`, the first line is the target environment, printed before the import starts; with `--format json` that line goes to stderr and the report ([docs/schemas/settings-apply.schema.json](docs/schemas/settings-apply.schema.json)) is written at the end. Exit codes: `0` dry run done or every flow on, `1` validate problems or a flow not on, `2` usage error (bad option, path not found, a folder instead of a zip, `--apply` without `--target` or the reverse, or a `--target` that does not match), `3` `pac` not found, not signed in, a failed or timed-out import, `pac` output it cannot read, or an unreadable solution or settings file.

Agents: run the dry run first and show the user the target line; run `--apply --target <name>` only after the user says yes to that environment, and never against an environment the user did not name.

`drift --solution <name> --env <env or file> --env <env or file> [--env ...] [--save <dir>] [--format text|json|markdown] [--out <file>]` compares one solution across two or more environments and reports where they differ. It compares the version, the components, unmanaged layers on a managed solution's components, environment variable values, connection reference connectors and bindings, and cloud flow states. Each environment is compared with the first. Output names an environment by its position (`1`, `2`, ...) and its friendly name, because friendly names are not unique. An `--env` is an environment (an id or URL, as `pac` takes it) or a snapshot file (a path that ends in `.json`, or an existing file), so live and saved environments can be mixed. `--save <dir>` writes `<solution>.<environment name>.json` for each live environment, after every read has succeeded. It creates the folder, overwrites a file of the same name, and replaces every character outside letters, digits, `-`, `_`, `.` and space in the name with `-`. Snapshot files you pass in are not written again. A later run can compare the saved files offline.

Each difference is an item with a kind. These kinds are drift and make the exit code `1`: `solution-missing` (installed in one environment of a pair and not the other; nothing else is compared for that pair), `version`, `component-missing`, `component-extra`, `unmanaged-layer`, `variable-missing`, `variable-value`, `reference-missing`, `reference-connector`, `reference-unbound`, `flow-missing` and `flow-state`. Two kinds are information only and are marked `(info)`: `managed` (the managed flags differ; Dev unmanaged and Test managed is the normal route) and `layers-not-read`. A flow that is off in every environment is not drift. A flow, variable or reference that is missing is reported once under its own kind, not again as a component. Tables (type 1), canvas apps (300), model-driven apps (80) with their sitemaps (62) and app settings (10087), environment variables (380 and 381) and connection references get new ids when they are imported, so they are compared by name through their own kinds or not at all, and only other components are compared by id. `unmanaged-layer` and `reference-unbound` are checked in every environment, the first one included. Connection ids are not compared, because a connection belongs to one environment. Default values of environment variables are not compared either. A variable whose value differs by design makes every run exit `1`, and there is no option to accept it; with JSON output, filter `items` by `kind`.

Values are never printed. A snapshot file holds a SHA-256 of each variable's current value, never the value. A hash hides a value from a reader, but not from someone who guesses a short or known value, so keep reports and snapshot files in private folders and private CI artifacts. Friendly names are printed and saved, which is another reason. `layers not read` appears for a managed solution whose layer reads were skipped (see the cost below).

`drift` only reads. For each live environment it runs `pac org who --environment <env>` for the friendly name and the environment URL, asks `az account get-access-token` for a token for that URL, then sends Web API GET requests with FetchXML. It does not parse `pac env fetch`, because Microsoft Learn documents no output format for it. The token is held in memory and sent only in the `Authorization` header. It is never printed or saved, and neither is the URL. powerops sends the token only to documented Dataverse hosts (`*.dynamics.com`, `*.crm.microsoftdynamics.us`, `*.crm.appsplatform.us`, `*.crm.dynamics.cn`); an environment URL on any other host exits `2` before a token is requested. Prerequisites: `pac` signed in (`pac auth create`), and the Azure CLI on `PATH` and signed in with `az login`. If `az` is installed elsewhere, add its folder to `PATH`, for example `fish_add_path <folder>` or your shell's equivalent. The two sign-ins can be different accounts: `pac org who` runs as the pac profile, and the rows are read as the `az` account. An environment in a different tenant from the `az` sign-in is not supported. The reading account needs to be able to read the solution, solution component, workflow, environment variable and connection reference tables; the minimum security role was not tested. In CI, `drift` needs an `az` sign-in beside `pac`'s.

Each live environment costs one `pac` call, one `az` call and five Web API reads, each with a 30 second limit. For a managed solution it also makes one layer read per component that is a table, flow, canvas app, environment variable definition or connection reference (the layer table takes one component per query; about 0.4 s each in the capture), to find unmanaged layers; other component types are not checked for layers. More than 50 such components skip the layer reads, and the report says `layers-not-read`. A read returns one page of at most 5,000 rows; more than that exits `3`. When a call fails, the message names the `--env` by position and the read. Text forwarded from `pac`, `az` or the Web API goes to stderr only, never to the report or a file, and may repeat an `--env` value or the environment URL. Tested with `pac` 2.13.1 and Azure CLI 2.90.0 on Linux, live on 2026-10-22 against an unmanaged developer environment and a managed install in a trial environment, with an account that holds the System Administrator role there; the minimum role for the reads was not tested, and Windows is not tested. JSON follows [docs/schemas/drift.schema.json](docs/schemas/drift.schema.json), and snapshot files follow [docs/schemas/snapshot.schema.json](docs/schemas/snapshot.schema.json). Exit codes: `0` no drift, `1` drift, `2` usage error (no `--solution` or a name that is not a solution unique name, fewer than two `--env`, an `--env` ending in `.json` with no file, `--save` naming a file or used with no live environment, a snapshot for another solution, two environments that would be saved to the same file name, a solution installed in none of the environments, or an environment URL on a host that is not a documented Dataverse host), `3` a `pac` or `az` call or a Web API read failed, timed out or returned output it cannot read, `pac` or `az` not found, or an unreadable snapshot file.

Agents: run `drift` only against environments the user named in the session.

`deploy plan <folder> --target <environment> [--save <plan file>] [--format text|json|markdown] [--out <file>]` says, before anything is imported, what importing the exported solution zips in `<folder>` into the target would do. It reads the target and writes nothing to it; its only file writes are `--out` and `--save`. Every file directly in the folder (not in subfolders) whose name ends in `.zip`, in any case, is one solution. Beside a zip, `<zip name without .zip>.settings.json` (the exact name, at most 1 MB) is its deployment settings file in `pac`'s format, as in `settings validate`. A zip or settings file name may hold only letters, digits, `.`, `_` and `-`. `--target` is an environment (an id or URL, as `pac` takes it and as `drift`'s `--env`); it is never printed, and the report names the target by its friendly name. The target is read the way `drift` reads it (one `pac org who`, one `az` token and the same Web API reads for each solution in the folder; for each other solution a zip depends on, only whether it is installed and its version), so the same limits apply: documented Dataverse hosts only, 5,000 rows per read, and text forwarded from `pac`, `az` and the Web API goes to stderr only.

The plan lists the solutions in import order: a topological sort on each zip's dependencies on the other zips in the folder (from its `<MissingDependencies>`; ties in name order). Each solution gets an action: `install` (not installed in the target), `upgrade` (the zip's version is higher) or `skip` (the same or a lower version, as `pac solution import --skip-lower-version`). Versions must have four numeric parts. For an install or upgrade it checks the settings file with `settings validate`; a zip with no settings file is checked as if the file were `{}`, so a connection reference, or a variable with no default or current value, is a problem, because `pac solution import` binds references only from a settings file. It also checks the dependencies and gives each cloud flow's expected state after the import (`settings apply`'s prediction, against the flow's state in the target). Every finding is an item with a kind and a severity. These block the plan (exit `1`): `cycle` (a dependency cycle among the zips), `missing-dependency` (a required solution that is neither installed in the target nor earlier in the order), `managed-conflict` (a managed zip over an unmanaged install, or the other way round, for any action) and `settings`. These are warnings: `dependency-not-checked` (a `<Required>` that names no solution), `deletes` and `unmanaged-layer`. These are information: `layers-not-read` and `components-not-checked`. The deletion check applies to the upgrade of a managed zip. It names the cloud flows, environment variables and connection references of the target's solution that the zip no longer has, which the upgrade deletes. Tables and canvas apps are compared by count only, because the target names them by id: when the target's solution holds more of them than the zip's root components, one item says the upgrade deletes at least that many (a deleted table loses its data). Other component types are counted and listed as not checked, so their deletion is not checked. `unmanaged-layer` names components of a managed target solution that have an unmanaged layer, which the import does not override; layers appear only when the target snapshot holds them, and the live read does not read them yet, so a live plan says `layers-not-read`. A block is a rule of the plan, not of `pac`: when one is wrong for your case, fix the input, or import with `pac` directly, outside PowerOps. The plan has no waiver.

The plan has a hash (SHA-256, lowercase hex) over its inputs: the target's environment id, each zip's bytes, each settings file's bytes (a change of line ending is a change), the target snapshot of each solution in the folder, and the installed version of each dependency read from the target. The same inputs give the same hash. The hash covers the inputs, not the findings. `--save <file>` writes the JSON report to a file whatever `--format` is, so one run gives the Markdown for the PR and the plan file. The plan file holds the tool version (`tool`), the hash, the target's friendly name, and `inputs`: for each zip the SHA-256 of the zip, of its settings file and of the target snapshot, and the dependency versions, so a later step can say which input changed. It holds no environment id or URL, no folder path, no connection id and no variable value. The Markdown report stops at whole rows before 60,000 characters and says how many rows it left out. JSON follows [docs/schemas/deploy-plan.schema.json](docs/schemas/deploy-plan.schema.json). Exit codes: `0` plan ok, `1` blocked (the report and the plan file are still written), `2` usage error (no folder, no zip, a file name outside the rule, a symbolic link, two zips with the same unique name, a settings file with no zip of that exact name, no `--target`, an `--out` or `--save` that names a folder, or a URL on a host that is not a documented Dataverse host), `3` an unreadable zip, settings file, version or flow id, a failed, timed-out or unreadable `pac`, `az` or Web API read, or `pac` or `az` not found.

Agents: run `deploy plan` only against environments the user named in the session, and show the user the plan before asking for approval.

`deploy apply <plan file> --target <environment> [--apply] [--format text|json] [--out <file>]` imports a reviewed plan. The plan file is the one `deploy plan --save` wrote; its folder is the deploy folder, so the zips and settings files are read from there. Without `--apply` it is a dry run that writes nothing. It refuses before reading the target when the plan file is blocked or was made by another powerops version (exit `1`). Then it plans again against the target with `deploy plan`'s code, and exits `2` when the target's friendly name is not the plan's, and refuses (exit `1`, nothing imported) when the new plan's hash differs from the file's, naming the first input that changed, or when the new plan is blocked. Otherwise the dry run lists what it would import, and with `--apply` it runs `pac solution import` for each `install` or `upgrade` in plan order (`--settings-file` when the zip has one, `--stage-and-upgrade` for the upgrade of a managed zip), hashing each file again just before its import. It stops at the first failed import and names the solutions already imported: there is no rollback. Afterwards it reads the target again and reports each planned cloud flow's state; exit `1` when a flow is not in the state the plan expected. The target's id, its URL, connection ids, setting values and `pac`'s own text never reach the report; `pac`'s output goes to stderr with every line prefixed `  | `. JSON follows [docs/schemas/deploy-apply.schema.json](docs/schemas/deploy-apply.schema.json). Exit codes: `0` applied (or a dry run that would apply), `1` refused or a flow not as expected, `2` usage error or a `--target` mismatch, `3` an unreadable plan file, zip or settings file, a failed `pac`, `az` or Web API call, a file changed before its import, or a failed import.

Agents: run `deploy apply` without `--apply` freely against an environment the user named; run `deploy apply --apply` only after the user said yes to that environment in the session.

| Rule | Severity | Finds |
|---|---|---|
| PO001 | error | Flow uses a connection reference that is not defined in the solution or its dependencies |
| PO002 | error | Required environment variable has neither a default nor a current value |
| PO003 | error | Missing dependency that no solution owns |
| PO004 | error | Flow calls a child flow that is not in the solution or its dependencies |
| PO005 | warning | Connection reference not used by any flow or canvas app |
| PO006 | warning | Flow contains a hard-coded environment or SharePoint URL |
| PO007 | warning | Environment variable default looks environment-specific (URL, GUID, email) |
| PO008 | warning | Environment variable current value is shipped in the solution |
| PO009 | warning | Component name uses a prefix other than the publisher's |
| PO010 | warning | Default or generated publisher |
| PO011 | warning | Cloud flow is turned off |
| PO012 | warning | Dataverse row-updated trigger has no filter columns |
| PO013 | error | Root component in the YAML layout has no source folder |
| PO014 | warning | Canvas app formula contains a hard-coded environment or SharePoint URL |

Each rule has a page with what it finds, how to fix it and how to suppress it: [docs/rules/](docs/rules/).

### Flow tests

`test [path] [--tests <dir>] [--format text|json|markdown] [--out <file>]` runs every `*.flowtest.json` file under `--tests` (default: the path) against the cloud flows of the solutions under `path` (default `.`). It is offline and read-only: it reads files, makes no network call and does not call `pac`. A test file names one flow, gives the trigger, the results of the connector actions (mocks) and any fixed values, and lists assertions about the run: its status, an action's status, inputs or outputs, a variable, the response. The simulator runs the flow's actions in order, evaluates its expressions and answers every connector action from the mocks; a connector action without a mock stops the test as `unmocked`. Environment variables take the test's value, else the solution's current value, else its default value.

```json
{
  "$schema": "https://raw.githubusercontent.com/Chrome71/PowerOps/main/docs/schemas/flow-test.schema.json",
  "flow": "Stamp work item status",
  "trigger": { "body": { "pops_workitemid": "00000000-0000-0000-0000-000000000001" } },
  "mocks": { "Update_a_row": { "status": "Succeeded" } },
  "environmentVariables": { "pops_DefaultStatus": "Active" },
  "assertions": [
    { "kind": "runStatus", "equals": "Succeeded" },
    { "kind": "inputs", "action": "Update_a_row", "path": ["parameters", "item/pops_name"], "equals": "Active" }
  ]
}
```

The file format is [docs/schemas/flow-test.schema.json](docs/schemas/flow-test.schema.json); the report (`--format json`) is [docs/schemas/test.schema.json](docs/schemas/test.schema.json). Working examples against the fixture flows are in [tests/fixtures/flowtests/](tests/fixtures/flowtests/).

In GitHub Actions, the action's `tests` input runs them and can block a merge: see [Merge gate: flow tests](#merge-gate-flow-tests).

Exit codes: `0` every test passed, `1` a test `failed`, `unmocked` or `unsupported`, `2` usage error (path or tests folder not found, the tests location is a file so a zip path needs `--tests`, no test file, no solution), `3` a test file or a solution that cannot be read (every file and solution is read before any test runs, so one bad one blocks them all; only the error is printed, and `POWEROPS_DEBUG=1` gives the stack trace). `unmocked` and `unsupported` fail on purpose: what PowerOps cannot simulate must not pass.

Limits. The simulator is written from Microsoft Learn's pages on the Workflow Definition Language, the action and function references and error handling. It has not been compared with a real run yet, so a pass here is not a guarantee about the cloud, and some behaviours Learn does not describe are the simulator's own choices: the list is "Not on Learn" in [the plan](docs/plans/m14-flowtest/plan.md).

- Not simulated: retry policies, concurrency (a Foreach runs one item at a time), timeouts and secure inputs and outputs. `TimedOut` never occurs.
- Only 37 functions and the action types listed in the plan are supported; anything else, such as `addHours`, `Select` or `Table`, is `unsupported`. Functions and action types are checked when the run reaches them, so a path the run does not take is not checked: a test of one path can pass while another path holds something unsupported.
- Every function argument is evaluated, `if`, `and` and `or` included. A guard such as `if(empty(x), '', x['a'])` fails when `x` is empty. Use `x?['a']` or a separate action.

## Planning

PowerOps ships three agent skills that turn an idea into a reviewed, signed-off plan before any code or solution change is made:

- `powerops-plan` asks short question rounds (at most three questions, recommended answer first), logs every answer in `decisions.md`, drafts `plan.md`, and signs it off with you. Asked to build without a plan, it drafts one and asks only the route.
- `powerops-review-plan` reviews an approved plan in a fresh context against a Power Platform checklist and writes `review.md`. It never asks you questions.
- `powerops-implement` builds only from a signed-off plan whose body still matches its sign-off.

Run `powerops skills install` in your repo to add them. In a workspace whose own tooling rewrites `.claude/skills/`, run it in each child project, not at the workspace root, and start the agent there: the next rewrite removes skills installed at the root. Supported: Claude Code and Codex. Gemini CLI and GitHub Copilot are provisional and untested. The skills are also in [skills/](skills/) for `npx skills add` (that CLI has telemetry on by default). Design: [docs/PLANNING-SKILLS.md](docs/PLANNING-SKILLS.md).

For your own Claude Code or Codex setup, the skills also install as a plugin from this repo, beside Microsoft's plugins. Install the `powerops` CLI first ([Install](#install)): the plugin's skills call it and need it on PATH.

```sh
claude plugin marketplace add Chrome71/PowerOps && claude plugin install powerops@powerops   # Claude Code (in a session: /plugin marketplace add Chrome71/PowerOps)
codex plugin marketplace add Chrome71/PowerOps && codex plugin add powerops@powerops         # Codex
```

For a team, `powerops skills install` stays the default: the skills then live in the repo, so every contributor and CI sees the same gate. Use one or the other in a repo; with both, each skill is listed twice.

With Microsoft's power-platform-skills or Dataverse-skills installed too, their skills build in Dev and PowerOps checks, plans and imports: the steps are in [docs/HANDOFF.md](docs/HANDOFF.md).

Plans live in `docs/plans/<name>/plan.md`, with a header between `---` lines (`status`, `review`, `signed-off`, `signed-off-hash`, `environment`).

```sh
powerops plan status                  # check every docs/plans/*/plan.md under . (offline, read-only; add --json for machine output)
powerops plan sign-off docs/plans/site-visits   # write status, date and body hash into the header
powerops plan facts                   # environment and solution names as JSON; read-only, needs pac signed in, prints names only
powerops skills install               # copy the planning skills into .claude/skills/ and/or .agents/skills/ (--agent claude|codex|gemini|copilot, --force to overwrite)
```

`plan status` checks the header, that a signed-off plan still matches its recorded hash, that the review is done (with `review.md`) or skipped, and that no file in the plan folder holds a URL, GUID or email (add `<!-- plan-status: allow -->` to a line to allow it). `plan sign-off` refuses unless `status` is `approved`, the review is done or skipped, and the scan is clean; it edits only the header. Exit codes: `0` clean, `1` violations or a refused sign-off, `2` usage error or malformed header, `3` internal error.

## Build from source

Requires the .NET 10 SDK.

```sh
dotnet build
dotnet test
```

## Documentation

- [Foundation plan and roadmap](docs/FOUNDATION.md)
- [Hand-off to Microsoft's skills](docs/HANDOFF.md)
- [Contributing](CONTRIBUTING.md)
- [License (Apache 2.0)](LICENSE)
