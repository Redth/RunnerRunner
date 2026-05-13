#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/runnerrunner}"
VERSION="${1:-${RUNNERRUNNER_VERSION:-latest}}"

if [[ "${EUID}" -ne 0 ]]; then
    echo "update-server.sh must be run as root." >&2
    exit 1
fi

cd "${INSTALL_DIR}"

if [[ ! -f .env || ! -f compose.yaml ]]; then
    echo "RunnerRunner server install is missing .env or compose.yaml in ${INSTALL_DIR}." >&2
    exit 1
fi

backup_dir="${INSTALL_DIR}/backups/$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 0700 "${backup_dir}"
cp .env compose.yaml "${backup_dir}/"

if docker compose --env-file .env -f compose.yaml exec -T postgres pg_dump -U "${POSTGRES_USER:-runnerrunner}" "${POSTGRES_DB:-runnerrunner}" > "${backup_dir}/postgres.sql"; then
    echo "Database backup written to ${backup_dir}/postgres.sql"
else
    echo "Warning: database backup failed; continuing with compose update." >&2
fi

if grep -q '^RUNNERRUNNER_VERSION=' .env; then
    sed -i "s/^RUNNERRUNNER_VERSION=.*/RUNNERRUNNER_VERSION=${VERSION}/" .env
else
    echo "RUNNERRUNNER_VERSION=${VERSION}" >> .env
fi

docker compose --env-file .env -f compose.yaml pull
docker compose --env-file .env -f compose.yaml up -d --remove-orphans
docker compose --env-file .env -f compose.yaml ps

echo "RunnerRunner server updated to ${VERSION}."
