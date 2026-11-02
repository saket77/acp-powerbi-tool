#!/usr/bin/env bash
# Pause, resume and inspect the Fabric capacity.
#
# This is the whole cost story. A running F2 is roughly $0.36-$0.44/hour; paused, compute bills
# nothing and only storage ticks over. Resume when you sit down, pause when you get up.
#
#   scripts/capacity.sh resume
#   scripts/capacity.sh pause
#   scripts/capacity.sh status
#
# Nothing here is needed before Phase 4 — phases 0 to 3 touch no Azure at all.
set -euo pipefail

: "${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID (see scripts/tenant-setup.md)}"
: "${FABRIC_RESOURCE_GROUP:?Set FABRIC_RESOURCE_GROUP}"
: "${FABRIC_CAPACITY_NAME:?Set FABRIC_CAPACITY_NAME}"

command -v az >/dev/null 2>&1 || {
  echo "The Azure CLI is not installed. brew install azure-cli" >&2
  exit 1
}

capacity_id="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${FABRIC_RESOURCE_GROUP}/providers/Microsoft.Fabric/capacities/${FABRIC_CAPACITY_NAME}"

case "${1:-status}" in
  resume)
    echo "Resuming ${FABRIC_CAPACITY_NAME}. Billing starts now."
    az resource invoke-action --ids "$capacity_id" --action resume --output none
    echo "Resumed. Remember: scripts/capacity.sh pause when you finish."
    ;;
  pause)
    echo "Pausing ${FABRIC_CAPACITY_NAME}."
    az resource invoke-action --ids "$capacity_id" --action suspend --output none
    echo "Paused. Compute billing has stopped."
    ;;
  status)
    az resource show --ids "$capacity_id" \
      --query '{name:name, state:properties.state, sku:sku.name, region:location}' \
      --output table
    ;;
  *)
    echo "Usage: $0 {resume|pause|status}" >&2
    exit 1
    ;;
esac
