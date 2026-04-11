#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# RunnerRunner — Full Stack Deploy
#
# Deploys everything in one shot:
#   1. Server + Linux Agent → Docker Compose via SSH to Linux host
#   2. macOS Agent → Native binary via SSH to macOS host
#
# Usage:
#   ./deploy/deploy-all.sh
#
# Configuration:
#   Edit the variables below or set them as environment variables.
# ============================================================

# --- Configuration (override with env vars) ---
LINUX_HOST="${LINUX_HOST:-192.168.2.2}"
LINUX_USER="${LINUX_USER:-root}"
LINUX_DEPLOY_DIR="${LINUX_DEPLOY_DIR:-/opt/runnerrunner}"

MACOS_HOST="${MACOS_HOST:-192.168.2.134}"
MACOS_USER="${MACOS_USER:-root}"

REGISTRY_URL="${REGISTRY_URL:-ghcr.io}"
REGISTRY_REPO="${REGISTRY_REPO:-redth/runnerrunner}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# --- Helpers ---
log()     { echo ""; echo "━━━ $* ━━━"; }
step()    { echo "  ▸ $*"; }
success() { echo "  ✅ $*"; }

# --- Pre-flight checks ---
log "Pre-flight checks"
command -v dotnet >/dev/null || { echo "❌ dotnet SDK not found"; exit 1; }
command -v docker >/dev/null || { echo "❌ docker not found"; exit 1; }
command -v ssh    >/dev/null || { echo "❌ ssh not found"; exit 1; }
step "All tools available"

# ============================================================
# PHASE 1: Build container images for Linux stack
# ============================================================
log "Phase 1: Building Docker images"

SERVER_IMAGE="${REGISTRY_URL}/${REGISTRY_REPO}/server:latest"
AGENT_IMAGE="${REGISTRY_URL}/${REGISTRY_REPO}/agent:latest"

step "Building server image..."
docker build -t "${SERVER_IMAGE}" \
    -f "${PROJECT_ROOT}/src/RunnerRunner.Server/Dockerfile" \
    "${PROJECT_ROOT}" --quiet

step "Building agent image..."
docker build -t "${AGENT_IMAGE}" \
    -f "${PROJECT_ROOT}/src/RunnerRunner.Agent/Dockerfile" \
    "${PROJECT_ROOT}" --quiet

success "Images built"

# ============================================================
# PHASE 2: Push images to registry
# ============================================================
log "Phase 2: Pushing images to ${REGISTRY_URL}"

step "Pushing server image..."
docker push "${SERVER_IMAGE}" --quiet

step "Pushing agent image..."
docker push "${AGENT_IMAGE}" --quiet

success "Images pushed to registry"

# ============================================================
# PHASE 3: Deploy Docker Compose stack to Linux host
# ============================================================
log "Phase 3: Deploying to Linux host (${LINUX_USER}@${LINUX_HOST})"

step "Creating deploy directory on remote host..."
ssh "${LINUX_USER}@${LINUX_HOST}" "mkdir -p ${LINUX_DEPLOY_DIR}"

step "Generating docker-compose.yml..."
cat > /tmp/rr-compose.yml <<COMPOSE_EOF
services:
  server:
    image: ${SERVER_IMAGE}
    ports:
      - "8080:8080"
    volumes:
      - server-data:/app/data
    environment:
      - Database__Path=/app/data/runnerrunner.db
      - ASPNETCORE_URLS=http://+:8080
    restart: unless-stopped

  agent:
    image: ${AGENT_IMAGE}
    environment:
      - RunnerRunner__ServerUrl=http://server:8080
      - RunnerRunner__AgentName=linux-agent-${LINUX_HOST}
      - RunnerRunner__AgentToken=
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    depends_on:
      - server
    restart: unless-stopped

volumes:
  server-data:
COMPOSE_EOF

step "Copying docker-compose.yml to ${LINUX_HOST}..."
scp /tmp/rr-compose.yml "${LINUX_USER}@${LINUX_HOST}:${LINUX_DEPLOY_DIR}/docker-compose.yml"
rm /tmp/rr-compose.yml

step "Pulling images on remote host..."
ssh "${LINUX_USER}@${LINUX_HOST}" \
    "cd ${LINUX_DEPLOY_DIR} && docker compose pull --quiet"

step "Starting services..."
ssh "${LINUX_USER}@${LINUX_HOST}" \
    "cd ${LINUX_DEPLOY_DIR} && docker compose up -d --remove-orphans"

success "Linux stack deployed: http://${LINUX_HOST}:8080"

# ============================================================
# PHASE 4: Deploy native agent to macOS host
# ============================================================
log "Phase 4: Deploying native agent to macOS (${MACOS_USER}@${MACOS_HOST})"

# Check if the macos deploy script exists and agent.env is configured
MACOS_DEPLOY="${SCRIPT_DIR}/macos/deploy-agent.sh"
if [[ ! -x "${MACOS_DEPLOY}" ]]; then
    echo "  ⚠️  macOS deploy script not found at ${MACOS_DEPLOY}"
    echo "      Skipping macOS agent deployment."
else
    # Ensure agent.env exists with the correct server URL
    MACOS_ENV="${SCRIPT_DIR}/macos/agent.env"
    if [[ ! -f "${MACOS_ENV}" ]]; then
        step "Creating agent.env with Linux host server URL..."
        cat > "${MACOS_ENV}" <<ENV_EOF
RUNNERRUNNER_SERVER_URL=http://${LINUX_HOST}:8080
RUNNERRUNNER_AGENT_NAME=mac-agent-${MACOS_HOST}
RUNNERRUNNER_AGENT_TOKEN=
RUNNERRUNNER_AGENT_ID=
ENV_EOF
    fi

    SSH_USER="${MACOS_USER}" "${MACOS_DEPLOY}" "${MACOS_HOST}"
    success "macOS agent deployed"
fi

# ============================================================
# Summary
# ============================================================
log "Deploy complete!"
echo ""
echo "  🖥  Server:       http://${LINUX_HOST}:8080"
echo "  🐧 Linux Agent:  running as Docker container on ${LINUX_HOST}"
echo "  🍎 macOS Agent:  running as launchd service on ${MACOS_HOST}"
echo ""
echo "  To redeploy after changes:  ./deploy/deploy-all.sh"
echo "  Linux logs:   ssh ${LINUX_USER}@${LINUX_HOST} 'cd ${LINUX_DEPLOY_DIR} && docker compose logs -f'"
echo "  macOS logs:   ssh ${MACOS_USER}@${MACOS_HOST} 'tail -f /var/log/runnerrunner-agent.log'"
echo ""
