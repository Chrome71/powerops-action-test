#!/usr/bin/env bash
# Writes the Test deployment settings file of PowerOpsSeed to the path in $1, from one GitHub secret per value.
# The filter is fixed, so equal values give equal bytes in the plan job and the apply job. The file is never printed.
set -euo pipefail

: "${1:?usage: build-settings.sh <settings file>}"
: "${TEST_SEED_CONNECTION_ID:?TEST_SEED_CONNECTION_ID is not set}"
: "${TEST_SPARE_CONNECTION_ID:?TEST_SPARE_CONNECTION_ID is not set}"
: "${TEST_DEFAULT_STATUS:?TEST_DEFAULT_STATUS is not set}"

jq -n --arg seed "$TEST_SEED_CONNECTION_ID" --arg spare "$TEST_SPARE_CONNECTION_ID" --arg status "$TEST_DEFAULT_STATUS" '{
  EnvironmentVariables: [{SchemaName: "pops_DefaultStatus", Value: $status}],
  ConnectionReferences: [
    {LogicalName: "pops_SeedDataverse", ConnectionId: $seed, ConnectorId: "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"},
    {LogicalName: "pops_SpareDataverse", ConnectionId: $spare, ConnectorId: "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"}
  ]
}' > "$1"
