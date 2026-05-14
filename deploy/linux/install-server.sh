#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/runnerrunner}"
BIN_PATH="${BIN_PATH:-/usr/local/bin/runnerrunner-server}"
VERSION="${RUNNERRUNNER_VERSION:-latest}"
RELEASE_BASE_URL="${RUNNERRUNNER_RELEASE_BASE_URL:-https://github.com/redth/RunnerRunner/releases/latest/download}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

usage() {
    cat <<USAGE
Usage: sudo ./install-server.sh [options]

Options:
  --version VERSION              Image/artifact version to install (default: latest)
  --install-dir PATH             Install directory (default: /opt/runnerrunner)
  --bind-ip IP                   IP used for published Docker ports (default: 0.0.0.0)
  --server-port PORT             Server HTTP port (default: 4779)
  --orleans-ip IP                IP advertised by the server Orleans silo
  --enrollment-token TOKEN       Bootstrap enrollment token for bundled HostWorker
  --with-linux-worker            Also run a Linux HostWorker in this compose stack
  --postgres-password PASSWORD   PostgreSQL password (generated if omitted)
USAGE
}

generate_password() {
    if command -v openssl >/dev/null; then
        openssl rand -hex 16
    elif command -v uuidgen >/dev/null; then
        uuidgen | tr -d '-'
    else
        date +%s%N
    fi
}

enable_linux_worker_profile() {
    local env_file="$1"
    local profiles

    if grep -q '^COMPOSE_PROFILES=' "${env_file}"; then
        profiles="$(grep '^COMPOSE_PROFILES=' "${env_file}" | cut -d= -f2-)"
        case ",${profiles}," in
            *,linux-worker,*) ;;
            *) profiles="${profiles:+${profiles},}linux-worker" ;;
        esac
        sed -i "s/^COMPOSE_PROFILES=.*/COMPOSE_PROFILES=${profiles}/" "${env_file}"
    else
        echo "COMPOSE_PROFILES=linux-worker" >> "${env_file}"
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        --bind-ip) LINUX_BIND_IP="$2"; shift 2 ;;
        --server-port) SERVER_PORT="$2"; shift 2 ;;
        --orleans-ip) ORLEANS_ADVERTISED_IP="$2"; shift 2 ;;
        --enrollment-token) HOSTWORKER_ENROLLMENT_TOKEN="$2"; shift 2 ;;
        --with-linux-worker) RUNNERRUNNER_ENABLE_LINUX_WORKER=1; shift ;;
        --postgres-password) POSTGRES_PASSWORD="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

if [[ "${EUID}" -ne 0 ]]; then
    echo "install-server.sh must be run as root." >&2
    exit 1
fi

command -v docker >/dev/null || { echo "docker is required." >&2; exit 1; }
docker compose version >/dev/null || { echo "docker compose plugin is required." >&2; exit 1; }

install -d -m 0755 "${INSTALL_DIR}"
install -d -m 0755 "${INSTALL_DIR}/postgres-init"

if [[ -f "${SCRIPT_DIR}/compose.yaml" ]]; then
    install -m 0644 "${SCRIPT_DIR}/compose.yaml" "${INSTALL_DIR}/compose.yaml"
else
    curl -fsSL "${RELEASE_BASE_URL}/linux-compose.yaml" -o "${INSTALL_DIR}/compose.yaml"
fi

if compgen -G "${SCRIPT_DIR}/../postgres-init/*.sql" >/dev/null; then
    install -m 0644 "${SCRIPT_DIR}/../postgres-init/"*.sql "${INSTALL_DIR}/postgres-init/"
elif compgen -G "${SCRIPT_DIR}/postgres-init/*.sql" >/dev/null; then
    install -m 0644 "${SCRIPT_DIR}/postgres-init/"*.sql "${INSTALL_DIR}/postgres-init/"
fi

if [[ ! -f "${INSTALL_DIR}/.env" ]]; then
    generated_password="${POSTGRES_PASSWORD:-$(generate_password)}"
    generated_enrollment_token="${HOSTWORKER_ENROLLMENT_TOKEN:-$(generate_password)}"
    compose_profiles=""
    if [[ "${RUNNERRUNNER_ENABLE_LINUX_WORKER:-0}" == "1" ]]; then
        compose_profiles="linux-worker"
    fi
    cat > "${INSTALL_DIR}/.env" <<ENV
RUNNERRUNNER_VERSION=${VERSION}
POSTGRES_DB=runnerrunner
POSTGRES_USER=runnerrunner
POSTGRES_PASSWORD=${generated_password}
LINUX_BIND_IP=${LINUX_BIND_IP:-0.0.0.0}
SERVER_PORT=${SERVER_PORT:-4779}
POSTGRES_PORT=${POSTGRES_PORT:-5433}
ORLEANS_ADVERTISED_IP=${ORLEANS_ADVERTISED_IP:-}
HOSTWORKER_ENROLLMENT_TOKEN=${generated_enrollment_token}
HOSTWORKER_HOST_ID=${HOSTWORKER_HOST_ID:-local-docker-host}
HOSTWORKER_HOST_NAME=${HOSTWORKER_HOST_NAME:-local-docker-host}
COMPOSE_PROFILES=${compose_profiles}
ENV
    chmod 0600 "${INSTALL_DIR}/.env"
else
    if grep -q '^RUNNERRUNNER_VERSION=' "${INSTALL_DIR}/.env"; then
        sed -i "s/^RUNNERRUNNER_VERSION=.*/RUNNERRUNNER_VERSION=${VERSION}/" "${INSTALL_DIR}/.env"
    else
        echo "RUNNERRUNNER_VERSION=${VERSION}" >> "${INSTALL_DIR}/.env"
    fi
    if [[ "${RUNNERRUNNER_ENABLE_LINUX_WORKER:-0}" == "1" ]]; then
        enable_linux_worker_profile "${INSTALL_DIR}/.env"
    fi
fi

if [[ -f "${SCRIPT_DIR}/update-server.sh" ]]; then
    install -m 0755 "${SCRIPT_DIR}/update-server.sh" "${INSTALL_DIR}/update-server.sh"
else
    curl -fsSL "${RELEASE_BASE_URL}/update-server.sh" -o "${INSTALL_DIR}/update-server.sh"
    chmod 0755 "${INSTALL_DIR}/update-server.sh"
fi

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

case "\${1:-status}" in
  update) shift; exec "${INSTALL_DIR}/update-server.sh" "\$@" ;;
  status) exec docker compose --env-file .env -f compose.yaml ps ;;
  logs) shift; exec docker compose --env-file .env -f compose.yaml logs -f "\$@" ;;
  restart) exec docker compose --env-file .env -f compose.yaml up -d --remove-orphans ;;
  enable-linux-worker) enable_linux_worker; exec docker compose --env-file .env -f compose.yaml up -d --remove-orphans ;;
  *) echo "Usage: runnerrunner-server {status|logs|restart|update|enable-linux-worker}" >&2; exit 1 ;;
esac
BIN
chmod 0755 "${BIN_PATH}"

cd "${INSTALL_DIR}"
docker compose --env-file .env -f compose.yaml pull
docker compose --env-file .env -f compose.yaml up -d --remove-orphans

echo "RunnerRunner server installed in ${INSTALL_DIR}."
echo "Use: runnerrunner-server status | logs | update"
