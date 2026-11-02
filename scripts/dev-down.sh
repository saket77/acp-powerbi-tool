#!/usr/bin/env bash
# Stop the local dependencies. Pass --clean to also drop the data volumes.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ -d "${repo_root}/.localdev/docker" ]]; then
  export DOCKER_CONFIG="${repo_root}/.localdev/docker"
  export DOCKER_HOST="unix://${HOME}/.colima/default/docker.sock"
fi

if [[ "${1:-}" == "--clean" ]]; then
  echo "Removing containers and data volumes."
  docker compose down -v
else
  docker compose down
fi

echo
echo "colima is still running. 'colima stop' shuts the VM down as well."
