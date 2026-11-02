#!/usr/bin/env bash
# Bring up the local dependencies: SQL Server and Azurite.
#
# Everything this script works around is specific to running Docker on an Apple Silicon Mac
# without Docker Desktop. On a Linux box or with Docker Desktop installed, `docker compose up -d`
# on its own is enough.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# --- 1. The VM -------------------------------------------------------------------------------
# There is no ARM build of SQL Server, so the VM needs Rosetta to run the amd64 image.
if ! colima status >/dev/null 2>&1; then
  echo "Starting colima (Rosetta enabled, for the amd64 SQL Server image)..."
  colima start --vm-type=vz --vz-rosetta --cpu 4 --memory 8 --disk 60
else
  echo "colima is already running."
fi

# --- 2. Docker credentials -------------------------------------------------------------------
# A leftover ~/.docker/config.json from a past Docker Desktop install can name
# "credsStore": "desktop". Without Desktop that helper does not exist and every pull fails with
#   error getting credentials - err: exec: "docker-credential-desktop": executable file not found
# Rather than edit a global config that other projects may rely on, point Docker at a config of
# our own for the duration of this script. The images we pull are public, so empty auth is fine.
#
# To be rid of this permanently, delete the "credsStore" line from ~/.docker/config.json.
if grep -q '"credsStore"[[:space:]]*:[[:space:]]*"desktop"' "${HOME}/.docker/config.json" 2>/dev/null \
   && ! command -v docker-credential-desktop >/dev/null 2>&1; then
  echo "Working around a stale 'credsStore: desktop' in ~/.docker/config.json."
  export DOCKER_CONFIG="${repo_root}/.localdev/docker"
  mkdir -p "${DOCKER_CONFIG}/cli-plugins"
  printf '%s\n' '{ "auths": {} }' > "${DOCKER_CONFIG}/config.json"

  # CLI plugins are looked up under $DOCKER_CONFIG, so compose has to be linked in here too.
  for plugin in /opt/homebrew/opt/docker-compose/bin/docker-compose \
                /usr/local/opt/docker-compose/bin/docker-compose; do
    [[ -x "$plugin" ]] && ln -sfn "$plugin" "${DOCKER_CONFIG}/cli-plugins/docker-compose"
  done

  # Docker contexts live in the real config directory, which we have just stepped away from.
  # Address the colima socket directly instead.
  export DOCKER_HOST="unix://${HOME}/.colima/default/docker.sock"
fi

# --- 3. The containers -----------------------------------------------------------------------
docker compose up -d

# --- 4. Wait for SQL Server --------------------------------------------------------------------
# Under emulation the first start takes a while. The compose healthcheck is the thing to wait on.
echo -n "Waiting for SQL Server to accept connections"
for _ in $(seq 1 60); do
  status="$(docker inspect --format '{{.State.Health.Status}}' acp-portal-sql 2>/dev/null || echo starting)"
  if [[ "$status" == "healthy" ]]; then
    echo " ready."
    echo
    echo "  SQL      localhost:1433   (sa / Acp_Local_Dev_2026!)"
    echo "  Azurite  localhost:10000 blob, 10001 queue"
    echo
    echo "Next:  dotnet ef database update -p src/Acp.Portal.Infrastructure -s src/Acp.Portal.Api"
    exit 0
  fi
  echo -n "."
  sleep 2
done

echo
echo "SQL Server did not report healthy in time. Logs:" >&2
docker compose logs --tail 30 sql >&2
exit 1
