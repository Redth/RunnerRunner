#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# RunnerRunner — Full Stack Deploy
#
# Deploys everything in one shot:
#   1. Server + Host Silo → Docker Compose via SSH to Linux host
#   2. HostSilo → native binary via SSH to macOS host
#
# Usage:
#   ./deploy/deploy-all.sh            # deploy everything
#   ./deploy/deploy-all.sh linux      # deploy Linux stack only
#   ./deploy/deploy-all.sh macos      # deploy macOS HostSilo only
#   ./deploy/deploy-all.sh windows    # deploy Windows HostSilo only
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
REGISTRY_REPO="${REGISTRY_REPO:-redth/runnerrunner}"
DEFAULT_SSH_KEY="${SSH_IDENTITY_FILE:-${HOME}/.ssh/id_ed25519}"

# --- SSH wrapper: prefer explicit SSH keys, then password auth, then the default key ---
remote_ssh() {
    local user="$1" host="$2" password="$3"
    shift 3
    local port="${REMOTE_SSH_PORT:-22}"
    local key="${REMOTE_SSH_KEY:-}"
    if [[ -z "${key}" && -z "${password}" && -f "${DEFAULT_SSH_KEY}" ]]; then
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
    if [[ -z "${key}" && -z "${password}" && -f "${DEFAULT_SSH_KEY}" ]]; then
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
REMOTE_SSH_KEY="${LINUX_SSH_KEY:-${REMOTE_SSH_KEY:-}}"

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
    "${PROJECT_ROOT}" --quiet

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
    command: ["postgres", "-c", "max_connections=300"]
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

  host-silo:
    image: ${HOST_SILO_IMAGE}
    container_name: runnerrunner-host-silo
    ports:
      - "${LINUX_BIND_IP}:11112:11112"
      - "${LINUX_BIND_IP}:30001:30001"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    environment:
      - Database__ConnectionString=Host=postgres;Port=5432;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner
      - HostSilo__HostId=linux-host-${LINUX_HOST}
      - HostSilo__HostName=linux-host-${LINUX_HOST}
      - HostSilo__Platform=Linux
      - DOTNET_ENVIRONMENT=Production
      - RunnerRunner__AgentId=linux-host-${LINUX_HOST}
      - RunnerRunner__AgentName=linux-host-${LINUX_HOST}
      - RunnerRunner__ServerUrl=http://server:${SERVER_PORT}
      - Orleans__AdvertisedIPAddress=${ORLEANS_ADVERTISED_IP}
      - Orleans__SiloPort=11112
      - Orleans__GatewayPort=30001
    depends_on:
      postgres:
        condition: service_healthy
      server:
        condition: service_started
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
if ! remote_ssh "${LINUX_USER}" "${LINUX_HOST}" "${LINUX_PASSWORD}" \
    "cd ${LINUX_DEPLOY_DIR} && docker compose pull --quiet"; then
    step "Remote registry pull failed; streaming fresh app images directly to the host..."
    if [[ -n "${LINUX_PASSWORD}" ]]; then
        docker save "${SERVER_IMAGE}" "${HOST_SILO_IMAGE}" | \
            sshpass -p "${LINUX_PASSWORD}" ssh -o StrictHostKeyChecking=no "${LINUX_USER}@${LINUX_HOST}" "docker load"
    else
        docker save "${SERVER_IMAGE}" "${HOST_SILO_IMAGE}" | \
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
REMOTE_SSH_KEY="${MACOS_SSH_KEY:-${REMOTE_SSH_KEY:-}}"

# ============================================================
# PHASE 4: Deploy HostSilo to macOS host
# ============================================================
log "Phase 4: Deploying HostSilo to macOS (${MACOS_USER}@${MACOS_HOST})"

# Publish HostSilo for macOS ARM64
step "Publishing RunnerRunner.HostSilo (osx-arm64, self-contained)..."
PUBLISH_DIR="${PROJECT_ROOT}/artifacts/macos-hostsilo"
dotnet publish "${PROJECT_ROOT}/src/RunnerRunner.HostSilo/RunnerRunner.HostSilo.csproj" \
    -c Release -r osx-arm64 --self-contained \
    -o "${PUBLISH_DIR}" -v quiet

step "Generating appsettings.Production.json..."
cat > "${PUBLISH_DIR}/appsettings.Production.json" <<SETTINGS_EOF
{
  "HostSilo": {
    "HostId": "mac-host-${MACOS_HOST}",
    "HostName": "mac-host-${MACOS_HOST}",
    "Platform": "MacOS",
    "Architecture": "Arm64"
  },
  "RunnerRunner": {
    "AgentId": "mac-host-${MACOS_HOST}",
    "AgentName": "mac-host-${MACOS_HOST}",
    "ServerUrl": "http://${LINUX_HOST}:${SERVER_PORT}"
  },
  "Database": {
    "ConnectionString": "Host=${LINUX_HOST};Port=5433;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner;SSL Mode=Disable;Trust Server Certificate=true"
  },
  "Orleans": {
    "AdvertisedIPAddress": "${MACOS_HOST}"
  },
  "DOTNET_ENVIRONMENT": "Production"
}
SETTINGS_EOF

# Stop existing process(es), including any leftover legacy agent. Also unload
# the launchd plist if present so it doesn't immediately respawn.
step "Stopping existing HostSilo/legacy agent..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "launchctl unload \$HOME/Library/LaunchAgents/com.runnerrunner.hostsilo.plist 2>/dev/null || true; \
     pids=\$(ps ax -o pid= -o command= | awk '/RunnerRunner\\.(HostSilo|Agent)/ && !/awk/ {print \$1}'); \
     if [ -n \"\$pids\" ]; then \
       for pid in \$pids; do kill \"\$pid\" 2>/dev/null || true; done; \
       sleep 2; \
       for pid in \$pids; do kill -0 \"\$pid\" 2>/dev/null && kill -9 \"\$pid\" 2>/dev/null || true; done; \
     fi; \
     find /opt/runnerrunner -maxdepth 1 \\( -name 'RunnerRunner.Agent' -o -name 'RunnerRunner.Agent.*' -o -name 'start-agent.sh' \\) -exec rm -f {} + 2>/dev/null || true"
sleep 2

# Copy binary
step "Copying HostSilo to ${MACOS_HOST}:/opt/runnerrunner/..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "mkdir -p /opt/runnerrunner"
for f in "${PUBLISH_DIR}"/*; do
    remote_scp "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
        "$f" "/opt/runnerrunner/$(basename "$f")"
done

# Codesign (Gatekeeper fix)
step "Codesigning binary..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "codesign --force -s - /opt/runnerrunner/RunnerRunner.HostSilo"

# Install launchd plist with KeepAlive so the silo auto-restarts if it crashes.
# This is critical: without it, a single transient SignalR/Postgres exception
# would leave the mac silo offline indefinitely until manual intervention.
step "Installing launchd auto-restart..."
remote_ssh "${MACOS_USER}" "${MACOS_HOST}" "${MACOS_PASSWORD}" \
    "mkdir -p \$HOME/Library/LaunchAgents && cat > \$HOME/Library/LaunchAgents/com.runnerrunner.hostsilo.plist <<'PLIST_EOF'
<?xml version=\"1.0\" encoding=\"UTF-8\"?>
<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">
<plist version=\"1.0\">
<dict>
    <key>Label</key><string>com.runnerrunner.hostsilo</string>
    <key>ProgramArguments</key>
    <array>
        <string>/opt/runnerrunner/RunnerRunner.HostSilo</string>
    </array>
    <key>WorkingDirectory</key><string>/opt/runnerrunner</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>DOTNET_ENVIRONMENT</key><string>Production</string>
    </dict>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>ThrottleInterval</key><integer>10</integer>
    <!-- ProcessType=Interactive + SessionCreate=true are REQUIRED so the silo
         runs in a full Aqua user session. Without them, launchd-spawned
         processes get EHOSTUNREACH on outbound TCP to the LAN (postgres,
         peer silos) even though the same connection works fine from an
         interactive shell. The actions.runner plist uses the same flags. -->
    <key>ProcessType</key><string>Interactive</string>
    <key>SessionCreate</key><true/>
    <key>StandardOutPath</key><string>/tmp/runnerrunner-hostsilo.log</string>
    <key>StandardErrorPath</key><string>/tmp/runnerrunner-hostsilo.log</string>
</dict>
</plist>
PLIST_EOF
launchctl unload \$HOME/Library/LaunchAgents/com.runnerrunner.hostsilo.plist 2>/dev/null || true
launchctl load   \$HOME/Library/LaunchAgents/com.runnerrunner.hostsilo.plist"

success "macOS HostSilo deployed"

fi # end macos

if [[ "${DEPLOY_TARGET}" == "windows" || ( "${DEPLOY_TARGET}" == "all" && -n "${WINDOWS_HOST}" ) ]]; then
REMOTE_SSH_PORT="${WINDOWS_SSH_PORT}"
REMOTE_SSH_KEY="${WINDOWS_SSH_KEY:-${REMOTE_SSH_KEY:-}}"

# ============================================================
# PHASE 5: Deploy HostSilo to Windows host
# ============================================================
log "Phase 5: Deploying HostSilo to Windows (${WINDOWS_USER}@${WINDOWS_HOST})"

step "Publishing RunnerRunner.HostSilo (win-x64, self-contained)..."
WINDOWS_PUBLISH_DIR="${PROJECT_ROOT}/artifacts/windows-hostsilo"
dotnet publish "${PROJECT_ROOT}/src/RunnerRunner.HostSilo/RunnerRunner.HostSilo.csproj" \
    -c Release -r win-x64 --self-contained \
    -o "${WINDOWS_PUBLISH_DIR}" -v quiet

step "Generating Windows appsettings.Production.json..."
cat > "${WINDOWS_PUBLISH_DIR}/appsettings.Production.json" <<SETTINGS_EOF
{
  "HostSilo": {
    "HostId": "windows-host-${WINDOWS_HOST}",
    "HostName": "windows-host-${WINDOWS_HOST}",
    "Platform": "Windows",
    "Architecture": "X64"
  },
  "RunnerRunner": {
    "AgentId": "windows-host-${WINDOWS_HOST}",
    "AgentName": "windows-host-${WINDOWS_HOST}",
    "ServerUrl": "http://${LINUX_HOST}:${SERVER_PORT}"
  },
  "Database": {
    "ConnectionString": "Host=${LINUX_HOST};Port=5433;Database=runnerrunner;Username=runnerrunner;Password=runnerrunner;SSL Mode=Disable;Trust Server Certificate=true"
  },
  "Orleans": {
    "AdvertisedIPAddress": "${WINDOWS_HOST}"
  },
  "DOTNET_ENVIRONMENT": "Production"
}
SETTINGS_EOF

step "Preparing remote Windows deploy directory..."
remote_ssh "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "powershell -NoProfile -ExecutionPolicy Bypass -Command \"New-Item -ItemType Directory -Force -Path '${WINDOWS_DEPLOY_DIR}' | Out-Null\""

step "Copying HostSilo files to ${WINDOWS_HOST}:${WINDOWS_DEPLOY_DIR}..."
remote_scp "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "${WINDOWS_PUBLISH_DIR}/." "${WINDOWS_DEPLOY_DIR}/"
remote_scp "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "${PROJECT_ROOT}/deploy/windows/Install-HostSilo.ps1" "${WINDOWS_DEPLOY_DIR}/Install-HostSilo.ps1"
remote_scp "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "${PROJECT_ROOT}/src/RunnerRunner.HostSilo/Dockerfile.windows" "${WINDOWS_DEPLOY_DIR}/Dockerfile.windows"

step "Starting Windows HostSilo (${WINDOWS_MODE})..."
remote_ssh "${WINDOWS_USER}" "${WINDOWS_HOST}" "${WINDOWS_PASSWORD}" \
    "powershell -NoProfile -ExecutionPolicy Bypass -Command \"& '${WINDOWS_DEPLOY_DIR//\//\\}\\Install-HostSilo.ps1' -DeployDir '${WINDOWS_DEPLOY_DIR//\//\\}' -Mode '${WINDOWS_MODE}'\""

success "Windows HostSilo deployed"

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
echo "  Windows Host Silo: ${WINDOWS_MODE} on ${WINDOWS_HOST}"
fi
echo ""
echo "  Redeploy:     ./deploy/deploy-all.sh"
echo "  Linux logs:   ssh ${LINUX_USER}@${LINUX_HOST} 'cd ${LINUX_DEPLOY_DIR} && docker compose logs -f'"
if [[ -n "${MACOS_HOST}" ]]; then
echo "  macOS logs:   ssh ${MACOS_USER}@${MACOS_HOST} 'tail -f /tmp/runnerrunner-hostsilo.log'"
fi
if [[ -n "${WINDOWS_HOST}" ]]; then
echo "  Windows logs: ssh ${WINDOWS_USER}@${WINDOWS_HOST} 'powershell -Command \"Get-Content -Wait ${WINDOWS_DEPLOY_DIR}/logs/hostsilo.out.log\"'"
fi
echo ""
