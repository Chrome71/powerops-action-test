#!/usr/bin/env bash
# Posts the Markdown in PO_COMMENT as one pull request comment, or updates the one from an earlier run.
# Inputs come only from the environment: PO_COMMENT, PO_FORK (true or false), PO_PR, GITHUB_REPOSITORY,
# GITHUB_STEP_SUMMARY and GH_TOKEN (read by gh). It always exits 0: a comment that cannot be posted is a warning.
set -euo pipefail

if [ ! -f "$PO_COMMENT" ]; then
  exit 0
fi

marker='<!-- powerops -->'
body="$(mktemp)"
trap 'rm -f "$body"' EXIT
{ printf '%s\n' "$marker"; cat "$PO_COMMENT"; } > "$body"

in_summary=0
to_summary() {
  if [ "$in_summary" -eq 0 ]; then
    cat "$PO_COMMENT" >> "$GITHUB_STEP_SUMMARY"
    in_summary=1
  fi
}

# A fork's token is read-only.
if [ "$PO_FORK" = "true" ]; then
  to_summary
  exit 0
fi

# shortcut: GitHub documents no comment size limit, so 65000 bytes is a cautious guess; raise it if GitHub documents one.
if [ "$(wc -c < "$body")" -gt 65000 ]; then
  to_summary
  printf '%s\nThe PowerOps report is too long for a comment; it is in the job summary of this run.\n' "$marker" > "$body"
fi

# The first comment by the bot that starts with the marker (gh applies the filter to each page).
filter='.[] | select(.user.login == "github-actions[bot]" and ((.body // "") | startswith("<!-- powerops -->"))) | .id'

post() {
  local id
  id="$(gh api --paginate "repos/$GITHUB_REPOSITORY/issues/$PO_PR/comments" --jq "$filter" | sed -n 1p)" || return 1
  if [ -n "$id" ]; then
    gh api -X PATCH "repos/$GITHUB_REPOSITORY/issues/comments/$id" -F "body=@$body" > /dev/null
  else
    gh api "repos/$GITHUB_REPOSITORY/issues/$PO_PR/comments" -F "body=@$body" > /dev/null
  fi
}

if ! post; then
  echo "::warning::The PowerOps comment could not be posted (the token may be read-only); the report is in the job summary."
  to_summary
fi
