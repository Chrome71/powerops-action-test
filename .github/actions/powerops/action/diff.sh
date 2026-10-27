#!/usr/bin/env bash
# Compares the first parent of HEAD (the base branch tip, on a pull_request merge checkout) with the work tree
# and writes the Markdown to PO_COMMENT. Inputs come only from the environment: PO_DLL, PO_PATH, PO_COMMENT, RUNNER_TEMP.
# It always exits 0; a failure becomes a fixed text in PO_COMMENT, without the tool's own text.
set -euo pipefail

fail() {
  printf '## PowerOps diff\n\nThe diff of the base and head solution could not run (%s, exit code %s). See the job log.\n' "$1" "$2" > "$PO_COMMENT"
  exit 0
}

# git cat-file works in a shallow clone, where the parent commit itself is not present.
commit="$(git cat-file -p HEAD)" || fail "no base commit" 1
parent="$(printf '%s\n' "$commit" | awk '/^parent / { print $2; exit } /^$/ { exit }')"
if ! [[ "$parent" =~ ^[0-9a-f]{40,64}$ ]]; then
  fail "no base commit" 1
fi

code=0
git fetch --no-tags --depth=1 origin "$parent" || code=$?
if [ "$code" -ne 0 ]; then fail "fetch" "$code"; fi

# A fresh folder each run: a self-hosted runner may keep $RUNNER_TEMP between runs.
base="$(mktemp -d "$RUNNER_TEMP/powerops-base.XXXXXX")"
git worktree add --detach "$base" "$parent" || code=$?
if [ "$code" -ne 0 ]; then fail "worktree" "$code"; fi

dotnet "$PO_DLL" diff "$base/$PO_PATH" "$PO_PATH" --format markdown --out "$PO_COMMENT" || code=$?
# Exit 0 (same) and 1 (different) are results; anything else is a failure.
if [ "$code" -ne 0 ] && [ "$code" -ne 1 ]; then fail "diff" "$code"; fi
