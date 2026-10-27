#!/usr/bin/env bash
# Runs `powerops deploy plan` for the action. Inputs come only from the environment:
# PO_DLL, PO_FOLDER, PO_TARGET, PO_COMMENT, GITHUB_OUTPUT, GITHUB_STEP_SUMMARY and, optionally, RUNNER_DEBUG.
# It records the exit code in the step output and always exits 0; the last step of the action fails the job.
set -euo pipefail

code=0
if [ -z "${PO_FOLDER:-}" ]; then
  echo "::error::deploy-folder is required when target is set"
  code=2
else
  stderr="$(mktemp)"
  trap 'rm -f "$stderr"' EXIT
  dotnet "$PO_DLL" deploy plan "$PO_FOLDER" --target "$PO_TARGET" --save "$PO_FOLDER/powerops-plan.json" --format markdown --out "$PO_COMMENT" 2>"$stderr" || code=$?
  # Text forwarded from pac, az and the Web API can name tenant ids and the environment URL, and the log may be public.
  if [ "${RUNNER_DEBUG:-}" = "1" ]; then
    cat "$stderr"
  else
    grep '^error:' "$stderr" || true
  fi
fi

echo "exit-code=$code" >> "$GITHUB_OUTPUT"

case "$code" in
  0 | 1)
    if [ -f "$PO_COMMENT" ]; then
      cat "$PO_COMMENT" >> "$GITHUB_STEP_SUMMARY"
    fi
    if [ -f "$PO_FOLDER/powerops-plan.json" ]; then
      echo "plan-file=$PO_FOLDER/powerops-plan.json" >> "$GITHUB_OUTPUT"
    fi
    ;;
  *)
    printf '## PowerOps deploy plan\n\nThe plan could not run (exit code %s). See the job log.\n' "$code" > "$PO_COMMENT"
    cat "$PO_COMMENT" >> "$GITHUB_STEP_SUMMARY"
    ;;
esac
