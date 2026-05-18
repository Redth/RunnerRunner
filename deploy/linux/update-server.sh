#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/runnerrunner}"
BIN_PATH="${BIN_PATH:-/usr/local/bin/runnerrunner-server}"
UPDATE_REF="${RUNNERRUNNER_UPDATE_REF:-${RUNNERRUNNER_VERSION:-latest}}"
UPDATE_REPOSITORY="${RUNNERRUNNER_UPDATE_REPOSITORY:-Redth/RunnerRunner}"
SERVER_IMAGE_REPOSITORY="${RUNNERRUNNER_SERVER_IMAGE_REPOSITORY:-ghcr.io/redth/runnerrunner-server}"
HOSTWORKER_IMAGE_REPOSITORY="${RUNNERRUNNER_HOSTWORKER_IMAGE_REPOSITORY:-ghcr.io/redth/runnerrunner-hostworker}"

usage() {
    cat <<USAGE
Usage: sudo update-server.sh [ref] [options]

Arguments:
  ref                         GitHub release tag, branch, or commit SHA (default: ${UPDATE_REF})

Options:
  --ref REF                   GitHub release tag, branch, or commit SHA
  --repository OWNER/REPO     GitHub repository used to resolve branch refs (default: ${UPDATE_REPOSITORY})
  --server-image IMAGE        Server image repository (default: ${SERVER_IMAGE_REPOSITORY})
  --hostworker-image IMAGE    HostWorker image repository (default: ${HOSTWORKER_IMAGE_REPOSITORY})
  -h, --help                  Show this help
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --ref|--version) UPDATE_REF="$2"; shift 2 ;;
        --repository) UPDATE_REPOSITORY="$2"; shift 2 ;;
        --server-image) SERVER_IMAGE_REPOSITORY="$2"; shift 2 ;;
        --hostworker-image) HOSTWORKER_IMAGE_REPOSITORY="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        --*) echo "Unknown option: $1" >&2; usage; exit 1 ;;
        *) UPDATE_REF="$1"; shift ;;
    esac
done

github_api_get() {
    local url="$1"
    local args=(-fsSL -H "Accept: application/vnd.github+json" -H "User-Agent: RunnerRunner")
    if [[ -n "${RUNNERRUNNER_GITHUB_TOKEN:-${GITHUB_TOKEN:-}}" ]]; then
        args+=(-H "Authorization: Bearer ${RUNNERRUNNER_GITHUB_TOKEN:-${GITHUB_TOKEN:-}}")
    fi
    curl "${args[@]}" "${url}"
}

urlencode_ref() {
    local value="$1"
    value="${value//%/%25}"
    value="${value//\//%2F}"
    value="${value// /%20}"
    value="${value//\#/%23}"
    value="${value//\?/%3F}"
    value="${value//&/%26}"
    printf '%s' "${value}"
}

image_exists() {
    docker manifest inspect "$1" >/dev/null 2>&1
}

resolve_update_version() {
    local ref="$1"
    if image_exists "${SERVER_IMAGE_REPOSITORY}:${ref}"; then
        printf '%s' "${ref}"
        return 0
    fi

    local encoded_ref
    encoded_ref="$(urlencode_ref "${ref}")"
    local commit_json
    if ! commit_json="$(github_api_get "https://api.github.com/repos/${UPDATE_REPOSITORY}/commits/${encoded_ref}")"; then
        echo "Unable to resolve '${ref}' as an image tag or GitHub ref in ${UPDATE_REPOSITORY}." >&2
        return 1
    fi

    local sha
    sha="$(printf '%s\n' "${commit_json}" | sed -n 's/^[[:space:]]*"sha"[[:space:]]*:[[:space:]]*"\([0-9a-fA-F]\{40\}\)".*/\1/p' | head -n 1 | tr '[:upper:]' '[:lower:]')"
    if [[ -z "${sha}" ]]; then
        echo "GitHub ref '${ref}' did not resolve to a commit SHA." >&2
        return 1
    fi

    if ! image_exists "${SERVER_IMAGE_REPOSITORY}:${sha}"; then
        echo "GitHub ref '${ref}' resolved to ${sha}, but ${SERVER_IMAGE_REPOSITORY}:${sha} was not found." >&2
        return 1
    fi

    printf '%s' "${sha}"
}

profile_enabled() {
    local profile="$1"
    local profiles
    profiles="$(grep '^COMPOSE_PROFILES=' .env 2>/dev/null | cut -d= -f2- || true)"
    case ",${profiles}," in
        *,"${profile}",*) return 0 ;;
        *) return 1 ;;
    esac
}

set_env_value() {
    local key="$1"
    local value="$2"
    local escaped
    escaped="$(printf '%s' "${value}" | sed -e 's/[\/&]/\\&/g')"
    if grep -q "^${key}=" .env; then
        sed -i "s/^${key}=.*/${key}=${escaped}/" .env
    else
        echo "${key}=${value}" >> .env
    fi
}

write_server_wrapper() {
    cat > "${BIN_PATH}" <<BIN
#!/usr/bin/env bash
set -euo pipefail
cd "${INSTALL_DIR}"

enable_linux_worker() {
  if grep -q '^COMPOSE_PROFILES=' .env; then
    profiles="\$(grep '^COMPOSE_PROFILES=' .env | cut -d= -f2-)"
    case ",\${profiles}," in
      *,linux-worker,*) ;;
      *) profiles="\${profiles:+\${profiles},}linux-worker" ;;
    esac
    sed -i "s/^COMPOSE_PROFILES=.*/COMPOSE_PROFILES=\${profiles}/" .env
  else
    echo "COMPOSE_PROFILES=linux-worker" >> .env
  fi
}

show_version() {
  if ! docker inspect runnerrunner-server >/dev/null 2>&1; then
    echo "RunnerRunner server container was not found." >&2
    exit 1
  fi

  docker inspect runnerrunner-server --format 'Image: {{.Config.Image}}
Version: {{index .Config.Labels "org.opencontainers.image.version"}}
Commit: {{index .Config.Labels "org.opencontainers.image.revision"}}
Build tag: {{index .Config.Labels "org.opencontainers.image.ref.name"}}'
}

case "\${1:-status}" in
  update) shift; exec "${INSTALL_DIR}/update-server.sh" "\$@" ;;
  version) show_version ;;
  status) exec docker compose --env-file .env -f compose.yaml ps ;;
  logs) shift; exec docker compose --env-file .env -f compose.yaml logs -f "\$@" ;;
  restart) exec docker compose --env-file .env -f compose.yaml up -d --remove-orphans ;;
  enable-linux-worker) enable_linux_worker; exec docker compose --env-file .env -f compose.yaml up -d --remove-orphans ;;
  *) echo "Usage: runnerrunner-server {status|logs|restart|update|version|enable-linux-worker}" >&2; exit 1 ;;
esac
BIN
    chmod 0755 "${BIN_PATH}"
}

if [[ "${EUID}" -ne 0 ]]; then
    echo "update-server.sh must be run as root." >&2
    exit 1
fi

cd "${INSTALL_DIR}"

if [[ ! -f .env || ! -f compose.yaml ]]; then
    echo "RunnerRunner server install is missing .env or compose.yaml in ${INSTALL_DIR}." >&2
    exit 1
fi

RESOLVED_VERSION="$(resolve_update_version "${UPDATE_REF}")"
if profile_enabled "linux-worker" && ! image_exists "${HOSTWORKER_IMAGE_REPOSITORY}:${RESOLVED_VERSION}"; then
    echo "Linux HostWorker profile is enabled, but ${HOSTWORKER_IMAGE_REPOSITORY}:${RESOLVED_VERSION} was not found." >&2
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

set_env_value RUNNERRUNNER_VERSION "${RESOLVED_VERSION}"
if grep -q '^SERVER_IMAGE=' .env; then
    set_env_value SERVER_IMAGE "${SERVER_IMAGE_REPOSITORY}:${RESOLVED_VERSION}"
fi
if grep -q '^HOSTWORKER_IMAGE=' .env; then
    set_env_value HOSTWORKER_IMAGE "${HOSTWORKER_IMAGE_REPOSITORY}:${RESOLVED_VERSION}"
fi

docker compose --env-file .env -f compose.yaml pull
docker compose --env-file .env -f compose.yaml up -d --remove-orphans
docker compose --env-file .env -f compose.yaml ps
write_server_wrapper

echo "RunnerRunner server updated to ${RESOLVED_VERSION} (requested ${UPDATE_REF})."
