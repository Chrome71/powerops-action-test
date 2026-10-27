#!/usr/bin/env bash
# Runs `powerops test` for the action. Inputs come only from the environment:
# PO_DLL, PO_PATH, PO_TESTS, GITHUB_OUTPUT, GITHUB_STEP_SUMMARY and GITHUB_WORKSPACE.
# It writes the report to the job summary and, on exit 1, one error annotation per test file that did not pass.
# It records the exit code in the step output and always exits 0; the last step of the action fails the job.
# powerops test is offline, so its stderr names only local paths and committed files, and goes to the log as it is.
set -euo pipefail

report="$(mktemp)"
json="$(mktemp)"
trap 'rm -f "$report" "$json"' EXIT

code=0
dotnet "$PO_DLL" test "$PO_PATH" --tests "$PO_TESTS" --format markdown --out "$report" || code=$?
echo "exit-code=$code" >> "$GITHUB_OUTPUT"

case "$code" in
  0 | 1)
    if [ -s "$report" ]; then
      cat "$report" >> "$GITHUB_STEP_SUMMARY"
    else
      echo "::warning::powerops test wrote no report (exit code $code)"
    fi
    ;;
  *) printf '## Flow tests\n\nThe tests could not run (exit code %s). See the job log.\n' "$code" >> "$GITHUB_STEP_SUMMARY" ;;
esac

if [ "$code" = 1 ]; then
  # Annotations name the test file from the repository root: the tests folder, then the file's path inside it.
  dir="${PO_TESTS//\\//}"
  workspace="${GITHUB_WORKSPACE//\\//}"
  if [ "$dir" = "$workspace" ]; then dir=""; else dir="${dir#"$workspace"/}"; fi
  dir="${dir#./}"
  dir="${dir%/}"
  if [ "$dir" = "." ] || [ -z "$dir" ]; then dir=""; else dir="$dir/"; fi
  # The same tests give the same exit code, so 1 is expected here; anything else means there is no report to read.
  json_code=0
  dotnet "$PO_DLL" test "$PO_PATH" --tests "$PO_TESTS" --format json --out "$json" || json_code=$?
  if [ "$json_code" != 1 ] || [ ! -s "$json" ] || ! jq -r --arg dir "$dir" '
      def data: gsub("%"; "%25") | gsub("\r"; "%0D") | gsub("\n"; "%0A");
      def prop: data | gsub(":"; "%3A") | gsub(","; "%2C");
      def value: tojson | if length > 300 then .[0:300] + "…" else . end;
      .tests[] | select(.result != "passed")
      | ([.message // empty]
         + [.assertions[] | select(.passed | not)
            | "assertion \(.index), \(.description): "
              + if has("actual") then "expected \(.expected | value), got \(.actual | value)" + (if .message then " (\(.message))" else "" end)
                else .message // "expected \(.expected | value)" end]) as $detail
      | "::error file=\($dir + .file | prop),title=\("Flow test " + .result | prop)::\(.flow + ": " + ($detail | join("; ")) | data)"' "$json" | tr -d '\r'; then
    echo "::warning::the annotations could not be written; the job summary has the report"
  fi
fi
