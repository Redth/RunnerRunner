#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# RunnerRunner — Full Stack Deploy
#
# Deploys everything in one shot:
#   1. Server + Host Silo → Docker Compose via SSH to Linux host
#   2. macOS Agent → (legacy, commented out)
#
# Usage:
#   ./deploy/deploy-all.sh            # deploy everything
#   ./deploy/deploy-all.sh linux      # deploy Linux stack only
#   ./deploy/deploy-all.sh macos      # deploy macOS agent only
#
# Configuration:
#   Copy deploy/.env.example to deploy/.env and fill in passwords.
#   Or set environment variables directly.
# ============================================================

# To enable OpenTelemetry monitoring, set OTEL_ENDPOINT in deploy/.env:
#   OTEL_ENDPOINT=http://aspire-dashboard:18889
# Then add an aspire-dashboard container to the compose stack.
DEPLOY_TARGET="${1:-all}"  # all, linux, macos

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# --- Load config from .env if it exists ---
if [[ -f "${SCRIPT_DIR}/.env" ]]; then
    set -a
    source "${SCRIPT_DIR}/.env"
    set +a
fi

# --- Configuration (override with env vars) ---
LINUX_HOST="${LINUX_HOST:-192.168.2.4}"
LINUX_USER="${LINUX_USER:-root}"
LINUX_PASSWORD="${LINUX_PASSWORD:-}"
LINUX_DEPLOY_DIR="${LINUX_DEPLOY_DIR:-/opt/stacks/runnerrunner}"
SERVER_PORT="${SERVER_PORT:-4779}"

MACOS_HOST="${MACOS_HOST:-192.168.2.134}"
MACOS_USER="${MACOS_USER:-root}"
MACOS_PASSWORD="${MACOS_PASSWORD:-}"

REGISTRY_URL="${REGISTRY_URL:-ghcr.io}"
REGISTRY_REPO="${REGISTRY_REPO:-redth/runnerrunner}"

# --- SSH wrapper: uses sshpass if password is set ---
remote_ssh() {
    local user="$1" host="$2" password="$3"
    shift 3
    if [[ -n "${password}" ]]; then
        sshpass -p "${password}" ssh -o StrictHostKeyChecking=no "${user}@${host}" "$@"
    else
        ssh "${user}@${host}" "$@"
    fi
}

remote_scp() {
    local user="$1" host="$2" password="$3" src="$4" dest="$5"
    if [[ -n "${password}" ]]; then
        sshpass -p "${password}" scp -o StrictHostKeyChecking=no -r "${src}" "${user}@${host}:${dest}"
    else
        scp -r "${src}" "${user}@${host}:${dest}"
    fi
}

# --- Helpers ---
log()     { echo ""; echo "━━━ $* ━━━"; }
step()    { echo "  ▸ $*"; }
success() { echo "  ✅ $*"; }

# --- Pre-flight checks ---
log "Pre-flight checks"
command -v dotnet >/dev/null || { echo "❌ dotnet SDK not found"; exit 1; }
command -v docker >/dev/null || { echo "❌ docker not found"; exit 1; }
command -v ssh    >/dev/null || { echo "❌ ssh not found"; exit 1; }
if [[ -n "${LINUX_PASSWORD}" || -n "${MACOS_PASSWORD}" ]]; then
    command -v sshpass >/dev/null || { echo "❌ sshpass not found (brew install hudochenkov/sshpass/sshpass)"; exit 1; }
fi
step "All tools available"

if [[ "${DEPLOY_TARGET}" == "all" || "${DEPLOY_TARGET}" == "linux" ]]; then

# ============================================================
# PHASE 1: Build container images for Linux stack
# ============================================================
log "Phase 1: Building Docker images"

SERVER_IMAGE="${REGISTRY_URL}/${REGISTRY_REPO}/server:latest"
HOST_SILO_IMAGE="${REGISTRY_URL}/${REGISTRY_REPO}/host-silo:latest"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"

step "Building server image (${DOCKER_PLATFORM})..."
docker build --platform "${DOCKER_PLATFORM}" -t "${SERVER_IMAGE}" \
    -f "${PROJECT_ROOT}/src/RunnerRunner.Server/Dockerfile" \
    "${PROJECT_ROOT}" --quiet

step "Building host-silo image (${DOCKER_PLATFORM})..."
docker build --platform "${DOCKER_PLATFORM}" -t "${HOST_SILO_IMAGE}" \
    -f "${PROJECT_ROOT}/src/RunnerRunner.HostSilo/Dockerfile" \
    "${PROJECT_ROOT}/src" --quiet

success "Images built"

# ============================================================
# PHASE 2: Push images to registry
# ============================================================
log "Phase 2: Pushing images to ${REGISTRY_URL}"

step "Pushing server image..."
docker push "${SERVER_IMAGE}" --quiet

step "Pushing host-silo image..."
docker push "${HOST_SILO_IMAGE}" --quiet
echo "${HOST_SILO_IMAGE}"

success "Images pushed to registry"

# ============================================================
# PHASE 3: Deploy Docker Compose stack to Linux host
# ============================================================
log "Phase 3: Deploying to Linux host (${LINUX_USER}@${LINUX_HOST})"

step "Creating deploy directory on remote host..."
remote_ssh "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "mkdir -p ${LINUX_DEPLOY_DIR}"

step "Generating docker-compose.yml..."
COMPOSE_FILE=$(mktemp)
cat > "${COMPOSE_FILE}" <<COMPOSE_EOF
services:
  postgres:
    image: postgres:17
    container_name: runnerrunner-postgres
    environment:
      - POSTGRES_DB=runnerrunner
      - POSTGRES_USER=runnerrunner
      - POSTGRES_PASSWORD=runnerrunner
    volumes:
      - postgres-data:/var/lib/postgresql/data
      - ./postgres-init:/docker-entrypoint-initdb.d:ro
    restart: unless-stopped
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U runnerrunner"]
      interval: 5s
      timeout: 5s
      retries: 5

  server:
    image: ${SERVER_IMAGE}
    container_name: runnerrunner-server
    ports:
      - "${SERVER_PORT}:${SERVER_PORT}"
    volumes:
      - server-data:/app/data
    environment:
      - Database__ConnectionString=Host=postgres;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner
      - ASPNETCORE_URLS=http://+:${SERVER_PORT}
      - OTEL_SERVICE_NAME=runnerrunner-server
      - OTEL_EXPORTER_OTLP_ENDPOINT=\${OTEL_ENDPOINT:-}
    labels:
      - "npm.proxy.domain=r2.jjagd.net"
      - "npm.proxy.port=${SERVER_PORT}"
      - "npm.proxy.ssl.force=true"
    depends_on:
      postgres:
        condition: service_healthy
    restart: unless-stopped

  host-silo:
    image: ${HOST_SILO_IMAGE}
    container_name: runnerrunner-host-silo
    environment:
      - Database__ConnectionString=Host=postgres;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner
      - HostSilo__HostId=linux-host-${LINUX_HOST}
      - HostSilo__HostName=linux-host-${LINUX_HOST}
      - HostSilo__Platform=Linux
      - DOTNET_ENVIRONMENT=Production
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    depends_on:
      postgres:
        condition: service_healthy
    restart: unless-stopped

volumes:
  server-data:
  postgres-data:
COMPOSE_EOF

step "Copying docker-compose.yml to ${LINUX_HOST}..."
remote_scp "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "${COMPOSE_FILE}" "${LINUX_DEPLOY_DIR}/docker-compose.yml"
rm "${COMPOSE_FILE}"

step "Copying PostgreSQL init scripts..."
remote_ssh "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "mkdir -p ${LINUX_DEPLOY_DIR}/postgres-init"
for sqlfile in "${SCRIPT_DIR}/postgres-init/"*.sql; do
    remote_scp "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
        "${sqlfile}" "${LINUX_DEPLOY_DIR}/postgres-init/$(basename "${sqlfile}")"
done

step "Pulling images on remote host..."
remote_ssh "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "cd ${LINUX_DEPLOY_DIR} && docker compose pull --quiet"

step "Starting services..."
remote_ssh "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "cd ${LINUX_DEPLOY_DIR} && docker compose up -d --force-recreate --remove-orphans"

success "Linux stack deployed: http://${LINUX_HOST}:${SERVER_PORT}"

fi # end linux

if [[ "${DEPLOY_TARGET}" == "all" || "${DEPLOY_TARGET}" == "macos" ]]; then

# === LEGACY: macOS native agent deploy ===
# Replaced by HostSilo. To deploy macOS HostSilo, build and deploy
# RunnerRunner.HostSilo as a self-contained binary instead.
#
# # ============================================================
# # PHASE 4: Deploy native agent to macOS host
# # ============================================================
# log "Phase 4: Deploying native agent to macOS (${MACOS_USER}@${MACOS_HOST})"
#
# MACOS_DEPLOY="${SCRIPT_DIR}/macos/deploy-agent.sh"
# if [[ ! -x "${MACOS_DEPLOY}" ]]; then
#     echo "  ⚠️  macOS deploy script not found at ${MACOS_DEPLOY}"
#     echo "      Skipping macOS agent deployment."
# else
#     # Ensure agent.env exists with the correct server URL
#     MACOS_ENV="${SCRIPT_DIR}/macos/agent.env"
#     step "Writing agent.env with server URL http://${LINUX_HOST}:${SERVER_PORT}..."
#     cat > "${MACOS_ENV}" <<ENV_EOF
# RUNNERRUNNER_SERVER_URL=http://${LINUX_HOST}:${SERVER_PORT}
# RUNNERRUNNER_AGENT_NAME=mac-agent-${MACOS_HOST}
# RUNNERRUNNER_AGENT_TOKEN=
# RUNNERRUNNER_AGENT_ID=
# ENV_EOF
#
#     # Pass password through to the macOS deploy script
#     export SSH_USER="${MACOS_USER}"
#     export SSHPASS="${MACOS_PASSWORD}"
#     "${MACOS_DEPLOY}" "${MACOS_HOST}"
#     success "macOS agent deployed"
# fi

log "Phase 4: macOS agent deploy (skipped — legacy, replaced by HostSilo)"

fi # end macos

# ============================================================
# Summary
# ============================================================
log "Deploy complete!"
echo ""
echo "  Server:       http://${LINUX_HOST}:${SERVER_PORT}"
echo "  Server:       https://r2.jjagd.net (via NPM)"
echo "  Host Silo:    Docker container on ${LINUX_HOST}"
echo "  macOS Agent:  launchd service on ${MACOS_HOST} (legacy)"
echo ""
echo "  Redeploy:     ./deploy/deploy-all.sh"
echo "  Linux logs:   ssh ${LINUX_USER}@${LINUX_HOST} 'cd ${LINUX_DEPLOY_DIR} && docker compose logs -f'"
echo "  macOS logs:   ssh ${MACOS_USER}@${MACOS_HOST} 'tail -f /var/log/runnerrunner-agent.log'"
echo ""
