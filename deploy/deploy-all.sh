#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# RunnerRunner development/debug full stack deploy
#
# Deploys local builds in one shot:
#   1. Server + HostWorker → Docker Compose via SSH to Linux host
#   2. HostWorker → native binary via SSH to macOS/Windows hosts
#
# This is not the recommended public install/update path. Consumer installs should
# use GitHub Release artifacts, runnerrunner-server update, and HostWorker updates
# from the Hosts page. Keep this script for pushing local builds to lab machines.
#
# Usage:
#   ./deploy/deploy-all.sh            # deploy everything
#   ./deploy/deploy-all.sh linux      # deploy Linux stack only
#   ./deploy/deploy-all.sh macos      # deploy macOS HostWorker only
#   ./deploy/deploy-all.sh windows    # deploy Windows HostWorker only
#
# Configuration:
#   Copy deploy/.env.example to deploy/.env and fill in passwords.
#   Or set environment variables directly.
# ============================================================

# To enable OpenTelemetry monitoring, set OTEL_ENDPOINT in deploy/.env:
#   OTEL_ENDPOINT=http://aspire-dashboard:18889
# Then add an aspire-dashboard container to the compose stack.
DEPLOY_TARGET="${1:-all}"  # all, linux, macos, windows

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# --- Load config from .env if it exists ---
if [[ -f "${SCRIPT_DIR}/.env" ]]; then
    set -a
    source "${SCRIPT_DIR}/.env"
    set +a
fi

# --- Configuration (override with env vars) ---
LINUX_HOST="${LINUX_HOST:-}"
LINUX_SSH_PORT="${LINUX_SSH_PORT:-22}"
LINUX_SSH_KEY="${LINUX_SSH_KEY:-}"
LINUX_BIND_IP="${LINUX_BIND_IP:-0.0.0.0}"
ORLEANS_ADVERTISED_IP="${ORLEANS_ADVERTISED_IP:-${LINUX_HOST}}"
LINUX_USER="${LINUX_USER:-root}"
LINUX_PASSWORD="${LINUX_PASSWORD:-}"
LINUX_DEPLOY_DIR="${LINUX_DEPLOY_DIR:-/opt/stacks/runnerrunner}"
SERVER_PORT="${SERVER_PORT:-4779}"
HOSTWORKER_ENROLLMENT_TOKEN="${HOSTWORKER_ENROLLMENT_TOKEN:-dev-hostworker-token}"
MACOS_HOSTWORKER_ENROLLMENT_TOKEN="${MACOS_HOSTWORKER_ENROLLMENT_TOKEN:-${HOSTWORKER_ENROLLMENT_TOKEN}}"
WINDOWS_HOSTWORKER_ENROLLMENT_TOKEN="${WINDOWS_HOSTWORKER_ENROLLMENT_TOKEN:-${HOSTWORKER_ENROLLMENT_TOKEN}}"

MACOS_HOST="${MACOS_HOST:-}"
MACOS_SSH_PORT="${MACOS_SSH_PORT:-22}"
MACOS_SSH_KEY="${MACOS_SSH_KEY:-}"
MACOS_USER="${MACOS_USER:-root}"
MACOS_PASSWORD="${MACOS_PASSWORD:-}"

WINDOWS_HOST="${WINDOWS_HOST:-}"
WINDOWS_SSH_PORT="${WINDOWS_SSH_PORT:-22}"
WINDOWS_SSH_KEY="${WINDOWS_SSH_KEY:-}"
WINDOWS_USER="${WINDOWS_USER:-Administrator}"
WINDOWS_PASSWORD="${WINDOWS_PASSWORD:-}"
WINDOWS_DEPLOY_DIR="${WINDOWS_DEPLOY_DIR:-C:/RunnerRunner}"
WINDOWS_MODE="${WINDOWS_MODE:-native}"

REGISTRY_URL="${REGISTRY_URL:-ghcr.io}"
REGISTRY_NAMESPACE="${REGISTRY_NAMESPACE:-redth}"
DEFAULT_SSH_KEY="${SSH_IDENTITY_FILE:-${HOME}/.ssh/id_ed25519}"

# --- SSH wrapper: prefer explicit/default SSH keys over password auth ---
remote_ssh() {
    local user="$1" host="$2" password="$3"
    shift 3
    local port="${REMOTE_SSH_PORT:-22}"
    local key="${REMOTE_SSH_KEY:-}"
    if [[ -z "${key}" && -f "${DEFAULT_SSH_KEY}" ]]; then
        key="${DEFAULT_SSH_KEY}"
    fi
    if [[ -n "${key}" ]]; then
        ssh -p "${port}" \
            -i "${key}" \
            -o StrictHostKeyChecking=no \
            -o IdentitiesOnly=yes \
            -o BatchMode=yes \
            "${user}@${host}" "$@"
    elif [[ -n "${password}" ]]; then
        sshpass -p "${password}" ssh -p "${port}" \
            -o StrictHostKeyChecking=no \
            -o PreferredAuthentications=password,keyboard-interactive \
            -o PubkeyAuthentication=no \
            "${user}@${host}" "$@"
    else
        ssh -p "${port}" \
            -o StrictHostKeyChecking=no \
            "${user}@${host}" "$@"
    fi
}

remote_scp() {
    local user="$1" host="$2" password="$3" src="$4" dest="$5"
    local port="${REMOTE_SSH_PORT:-22}"
    local key="${REMOTE_SSH_KEY:-}"
    if [[ -z "${key}" && -f "${DEFAULT_SSH_KEY}" ]]; then
        key="${DEFAULT_SSH_KEY}"
    fi
    if [[ -n "${key}" ]]; then
        scp -P "${port}" \
            -i "${key}" \
            -o StrictHostKeyChecking=no \
            -o IdentitiesOnly=yes \
            -o BatchMode=yes \
            -r "${src}" "${user}@${host}:${dest}"
    elif [[ -n "${password}" ]]; then
        sshpass -p "${password}" scp -P "${port}" \
            -o StrictHostKeyChecking=no \
            -o PreferredAuthentications=password,keyboard-interactive \
            -o PubkeyAuthentication=no \
            -r "${src}" "${user}@${host}:${dest}"
    else
        scp -P "${port}" \
            -o StrictHostKeyChecking=no \
            -r "${src}" "${user}@${host}:${dest}"
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
if [[ -n "${LINUX_PASSWORD}" || -n "${MACOS_PASSWORD}" || -n "${WINDOWS_PASSWORD}" ]]; then
    command -v sshpass >/dev/null || { echo "❌ sshpass not found (brew install hudochenkov/sshpass/sshpass)"; exit 1; }
fi
step "All tools available"

if [[ "${DEPLOY_TARGET}" == "all" || "${DEPLOY_TARGET}" == "linux" || "${DEPLOY_TARGET}" == "macos" || "${DEPLOY_TARGET}" == "windows" ]]; then
    [[ -n "${LINUX_HOST}" ]] || { echo "❌ LINUX_HOST must be set in deploy/.env or environment"; exit 1; }
fi

if [[ "${DEPLOY_TARGET}" == "all" || "${DEPLOY_TARGET}" == "macos" ]]; then
    [[ -n "${MACOS_HOST}" ]] || { echo "❌ MACOS_HOST must be set in deploy/.env or environment"; exit 1; }
fi

if [[ "${DEPLOY_TARGET}" == "windows" ]]; then
    [[ -n "${WINDOWS_HOST}" ]] || { echo "❌ WINDOWS_HOST must be set in deploy/.env or environment"; exit 1; }
fi

if [[ "${DEPLOY_TARGET}" == "all" || "${DEPLOY_TARGET}" == "linux" ]]; then
REMOTE_SSH_PORT="${LINUX_SSH_PORT}"

# ============================================================
# PHASE 1: Build container images for Linux stack
# ============================================================
log "Phase 1: Building Docker images"

SERVER_IMAGE="${REGISTRY_URL}/${REGISTRY_NAMESPACE}/runnerrunner-server:latest"
HOST_WORKER_IMAGE="${REGISTRY_URL}/${REGISTRY_NAMESPACE}/runnerrunner-hostworker:latest"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"

step "Building server image (${DOCKER_PLATFORM})..."
docker build --platform "${DOCKER_PLATFORM}" -t "${SERVER_IMAGE}" \
    -f "${PROJECT_ROOT}/src/RunnerRunner.Server/Dockerfile" \
    "${PROJECT_ROOT}" --quiet

step "Building host-worker image (${DOCKER_PLATFORM})..."
docker build --platform "${DOCKER_PLATFORM}" -t "${HOST_WORKER_IMAGE}" \
    -f "${PROJECT_ROOT}/src/RunnerRunner.HostWorker/Dockerfile" \
    "${PROJECT_ROOT}" --quiet

success "Images built"

# ============================================================
# PHASE 2: Push images to registry
# ============================================================
log "Phase 2: Pushing images to ${REGISTRY_URL}"

step "Pushing server image..."
docker push "${SERVER_IMAGE}" --quiet

step "Pushing host-worker image..."
docker push "${HOST_WORKER_IMAGE}" --quiet
echo "${HOST_WORKER_IMAGE}"

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
    ports:
      - "${LINUX_BIND_IP}:5433:5432"
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
      - "${LINUX_BIND_IP}:${SERVER_PORT}:${SERVER_PORT}"
      - "${LINUX_BIND_IP}:11111:11111"
      - "${LINUX_BIND_IP}:30000:30000"
    volumes:
      - server-data:/app/data
      - /var/run/docker.sock:/var/run/docker.sock
    environment:
      - Database__ConnectionString=Host=postgres;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner
      - HostWorker__EnrollmentToken=${HOSTWORKER_ENROLLMENT_TOKEN}
      - ASPNETCORE_URLS=http://+:${SERVER_PORT}
      - OTEL_SERVICE_NAME=runnerrunner-server
      - DOTNET_ENVIRONMENT=Production
      - Orleans__AdvertisedIPAddress=${ORLEANS_ADVERTISED_IP}
    labels:
      - "npm.proxy.domain=r2.jjagd.net"
      - "npm.proxy.port=${SERVER_PORT}"
      - "npm.proxy.ssl.force=true"
    depends_on:
      postgres:
        condition: service_healthy
    restart: unless-stopped

  host-worker:
    image: ${HOST_WORKER_IMAGE}
    container_name: runnerrunner-host-worker
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - hostworker-data:/var/lib/runnerrunner
    environment:
      - HostWorker__ServerUrl=http://server:${SERVER_PORT}
      - HostWorker__EnrollmentToken=${HOSTWORKER_ENROLLMENT_TOKEN}
      - HostWorker__HostId=linux-host-${LINUX_HOST}
      - HostWorker__HostName=linux-host-${LINUX_HOST}
      - HostWorker__Platform=Linux
      - HostWorker__DataRoot=/var/lib/runnerrunner
      - DOTNET_ENVIRONMENT=Production
    depends_on:
      server:
        condition: service_started
    restart: unless-stopped

volumes:
  server-data:
  postgres-data:
  hostworker-data:
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
if ! remote_ssh "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "cd ${LINUX_DEPLOY_DIR} && docker compose pull --quiet"; then
    step "Remote registry pull failed; streaming fresh app images directly to the host..."
    if [[ -n "${LINUX_PASSWORD}" ]]; then
        docker save "${SERVER_IMAGE}" "${HOST_WORKER_IMAGE}" | \
            sshpass -p "${LINUX_PASSWORD}" ssh -o StrictHostKeyChecking=no "${LINUX_USER}@${LINUX_HOST}" "docker load"
    else
        docker save "${SERVER_IMAGE}" "${HOST_WORKER_IMAGE}" | \
            ssh "${LINUX_USER}@${LINUX_HOST}" "docker load"
    fi
fi

step "Starting services..."
remote_ssh "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "cd ${LINUX_DEPLOY_DIR} && docker compose up -d --force-recreate --remove-orphans"

success "Linux stack deployed: http://${LINUX_HOST}:${SERVER_PORT}"

fi # end linux

if [[ "${DEPLOY_TARGET}" == "all" || "${DEPLOY_TARGET}" == "macos" ]]; then
REMOTE_SSH_PORT="${MACOS_SSH_PORT}"

# ============================================================
# PHASE 4: Deploy HostWorker to macOS host
# ============================================================
log "Phase 4: Deploying HostWorker to macOS (${MACOS_USER}@${MACOS_HOST})"

# Publish HostWorker for macOS ARM64
step "Publishing RunnerRunner.HostWorker (osx-arm64, self-contained)..."
PUBLISH_DIR="${PROJECT_ROOT}/artifacts/macos-hostworker"
dotnet publish "${PROJECT_ROOT}/src/RunnerRunner.HostWorker/RunnerRunner.HostWorker.csproj" \
    -c Release -r osx-arm64 --self-contained \
    -o "${PUBLISH_DIR}" -v quiet

step "Generating appsettings.Production.json..."
cat > "${PUBLISH_DIR}/appsettings.Production.json" <<SETTINGS_EOF
{
  "HostWorker": {
    "ServerUrl": "http://${LINUX_HOST}:${SERVER_PORT}",
    "EnrollmentToken": "${MACOS_HOSTWORKER_ENROLLMENT_TOKEN}",
    "HostId": "mac-host-${MACOS_HOST}",
    "HostName": "mac-host-${MACOS_HOST}",
    "Platform": "MacOS",
    "Architecture": "Arm64"
  },
  "DOTNET_ENVIRONMENT": "Production"
}
SETTINGS_EOF

# Stop existing process(es), including any leftover legacy agent
step "Stopping existing HostWorker/legacy agent..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "pids=\$(ps ax -o pid= -o command= | awk '/RunnerRunner\\.(HostWorker|Agent)/ && !/awk/ {print \$1}'); \
     if [ -n \"\$pids\" ]; then \
       for pid in \$pids; do kill \"\$pid\" 2>/dev/null || true; done; \
       sleep 2; \
       for pid in \$pids; do kill -0 \"\$pid\" 2>/dev/null && kill -9 \"\$pid\" 2>/dev/null || true; done; \
     fi; \
     find /opt/runnerrunner -maxdepth 1 \\( -name 'RunnerRunner.Agent' -o -name 'RunnerRunner.Agent.*' -o -name 'start-agent.sh' \\) -exec rm -f {} + 2>/dev/null || true"
sleep 2

# Copy binary
step "Copying HostWorker to ${MACOS_HOST}:/opt/runnerrunner/..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "mkdir -p /opt/runnerrunner"
for f in "${PUBLISH_DIR}"/*; do
    remote_scp "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
        "$f" "/opt/runnerrunner/$(basename "$f")"
done

# Codesign (Gatekeeper fix)
step "Codesigning binary..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "codesign --force -s - /opt/runnerrunner/RunnerRunner.HostWorker"

# Start via nohup
step "Starting HostWorker..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "cd /opt/runnerrunner && DOTNET_ENVIRONMENT=Production nohup ./RunnerRunner.HostWorker > /tmp/runnerrunner-hostworker.log 2>&1 & sleep 1"

success "macOS HostWorker deployed"

fi # end macos

if [[ "${DEPLOY_TARGET}" == "windows" || ( "${DEPLOY_TARGET}" == "all" && -n "${WINDOWS_HOST}" ) ]]; then
REMOTE_SSH_PORT="${WINDOWS_SSH_PORT}"

# ============================================================
# PHASE 5: Deploy HostWorker to Windows host
# ============================================================
log "Phase 5: Deploying HostWorker to Windows (${WINDOWS_USER}@${WINDOWS_HOST})"

step "Publishing RunnerRunner.HostWorker (win-x64, self-contained)..."
WINDOWS_PUBLISH_DIR="${PROJECT_ROOT}/artifacts/windows-hostworker"
dotnet publish "${PROJECT_ROOT}/src/RunnerRunner.HostWorker/RunnerRunner.HostWorker.csproj" \
    -c Release -r win-x64 --self-contained \
    -o "${WINDOWS_PUBLISH_DIR}" -v quiet

step "Generating Windows appsettings.Production.json..."
cat > "${WINDOWS_PUBLISH_DIR}/appsettings.Production.json" <<SETTINGS_EOF
{
  "HostWorker": {
    "ServerUrl": "http://${LINUX_HOST}:${SERVER_PORT}",
    "EnrollmentToken": "${WINDOWS_HOSTWORKER_ENROLLMENT_TOKEN}",
    "HostId": "windows-host-${WINDOWS_HOST}",
    "HostName": "windows-host-${WINDOWS_HOST}",
    "Platform": "Windows",
    "Architecture": "X64"
  },
  "DOTNET_ENVIRONMENT": "Production"
}
SETTINGS_EOF

step "Preparing remote Windows deploy directory..."
remote_ssh "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "powershell -NoProfile -ExecutionPolicy Bypass -Command \"New-Item -ItemType Directory -Force -Path '${WINDOWS_DEPLOY_DIR}' | Out-Null\""

step "Copying HostWorker files to ${WINDOWS_HOST}:${WINDOWS_DEPLOY_DIR}..."
remote_scp "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "${WINDOWS_PUBLISH_DIR}/." "${WINDOWS_DEPLOY_DIR}/"
remote_scp "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "${PROJECT_ROOT}/deploy/windows/Install-HostWorker.ps1" "${WINDOWS_DEPLOY_DIR}/Install-HostWorker.ps1"

step "Installing Windows HostWorker service..."
remote_ssh "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "powershell -NoProfile -ExecutionPolicy Bypass -Command \"& '${WINDOWS_DEPLOY_DIR//\//\\}\\Install-HostWorker.ps1' -DeployDir '${WINDOWS_DEPLOY_DIR//\//\\}' -HostId 'windows-host-${WINDOWS_HOST}' -HostName 'windows-host-${WINDOWS_HOST}' -ServerUrl 'http://${LINUX_HOST}:${SERVER_PORT}' -EnrollmentToken '${WINDOWS_HOSTWORKER_ENROLLMENT_TOKEN}'\""

success "Windows HostWorker deployed"

fi # end windows

# ============================================================
# Summary
# ============================================================
log "Deploy complete!"
echo ""
echo "  Server:       http://${LINUX_HOST}:${SERVER_PORT}"
echo "  Server:       https://r2.jjagd.net (via NPM)"
echo "  Linux server/silo: Docker container on ${LINUX_HOST}"
if [[ -n "${MACOS_HOST}" ]]; then
echo "  macOS Host Silo: Native binary on ${MACOS_HOST}"
fi
if [[ -n "${WINDOWS_HOST}" ]]; then
echo "  Windows Host Silo: Windows Service on ${WINDOWS_HOST}"
fi
echo ""
echo "  Redeploy:     ./deploy/deploy-all.sh"
echo "  Linux logs:   ssh ${LINUX_USER}@${LINUX_HOST} 'cd ${LINUX_DEPLOY_DIR} && docker compose logs -f'"
if [[ -n "${MACOS_HOST}" ]]; then
echo "  macOS logs:   ssh ${MACOS_USER}@${MACOS_HOST} 'tail -f /tmp/runnerrunner-hostworker.log'"
fi
if [[ -n "${WINDOWS_HOST}" ]]; then
echo "  Windows logs: ssh ${WINDOWS_USER}@${WINDOWS_HOST} 'powershell -Command \"Get-Content -Wait ${WINDOWS_DEPLOY_DIR}/logs/hostworker.out.log\"'"
fi
echo ""
