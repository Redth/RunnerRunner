#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# RunnerRunner Agent — macOS Native Deploy Script
#
# Publishes the agent as a self-contained binary, copies it to
# a macOS host over SSH, and installs it as a launchd service.
#
# Usage:
#   ./deploy/macos/deploy-agent.sh <host>
#   SSH_USER=admin ./deploy/macos/deploy-agent.sh 192.168.2.134
#
# Prerequisites on the Mac host:
#   - SSH access
#   - tart installed (brew install cirruslabs/cli/tart) for VM runners
# ============================================================

HOST="${1:?Usage: $0 <host-ip-or-hostname>}"
SSH_USER="${SSH_USER:-root}"
INSTALL_DIR="/opt/runnerrunner"
PLIST_NAME="com.runnerrunner.agent.plist"
PLIST_DEST="/Library/LaunchDaemons/${PLIST_NAME}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
AGENT_PROJECT="${PROJECT_ROOT}/src/RunnerRunner.Agent/RunnerRunner.Agent.csproj"
PUBLISH_DIR="${PROJECT_ROOT}/artifacts/macos-agent"
RID="${RID:-osx-arm64}"

log() { echo "▸ $*"; }

# --- Step 1: Publish self-contained binary ---
log "Publishing RunnerRunner.Agent (${RID}, self-contained)..."
dotnet publish "${AGENT_PROJECT}" \
    -c Release \
    -r "${RID}" \
    --self-contained \
    -o "${PUBLISH_DIR}" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    /p:DebugType=None \
    /p:DebugSymbols=false \
    --nologo -v quiet

log "Published to ${PUBLISH_DIR}"

# --- Step 2: Prepare appsettings if env file exists ---
ENV_FILE="${SCRIPT_DIR}/agent.env"
SETTINGS_FILE="${PUBLISH_DIR}/appsettings.Production.json"

if [[ -f "${ENV_FILE}" ]]; then
    log "Generating appsettings.Production.json from agent.env..."
    # Source the env file
    source "${ENV_FILE}"
    cat > "${SETTINGS_FILE}" <<SETTINGS_EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "RunnerRunner": {
    "ServerUrl": "${RUNNERRUNNER_SERVER_URL:-http://192.168.2.2:8080}",
    "AgentName": "${RUNNERRUNNER_AGENT_NAME:-mac-agent}",
    "AgentToken": "${RUNNERRUNNER_AGENT_TOKEN:-}",
    "AgentId": "${RUNNERRUNNER_AGENT_ID:-}"
  }
}
SETTINGS_EOF
else
    log "No agent.env found — using default appsettings.json"
    log "  Create deploy/macos/agent.env from agent.env.example to configure"
fi

# --- Step 3: Create install dir on remote host ---
log "Connecting to ${SSH_USER}@${HOST}..."
ssh "${SSH_USER}@${HOST}" "mkdir -p ${INSTALL_DIR}"

# --- Step 4: Stop existing service if running ---
log "Stopping existing agent service (if any)..."
ssh "${SSH_USER}@${HOST}" \
    "launchctl bootout system/${PLIST_NAME%.plist} 2>/dev/null || true"

# --- Step 5: Copy files to remote host ---
log "Copying agent binary and config to ${HOST}:${INSTALL_DIR}..."
scp -r "${PUBLISH_DIR}/"* "${SSH_USER}@${HOST}:${INSTALL_DIR}/"

log "Copying launchd plist..."
scp "${SCRIPT_DIR}/${PLIST_NAME}" "${SSH_USER}@${HOST}:${PLIST_DEST}"

# --- Step 6: Set permissions ---
ssh "${SSH_USER}@${HOST}" bash <<'REMOTE_EOF'
chmod +x /opt/runnerrunner/RunnerRunner.Agent
chown root:wheel /Library/LaunchDaemons/com.runnerrunner.agent.plist
chmod 644 /Library/LaunchDaemons/com.runnerrunner.agent.plist
REMOTE_EOF

# --- Step 7: Load and start the service ---
log "Loading launchd service..."
ssh "${SSH_USER}@${HOST}" \
    "launchctl bootstrap system ${PLIST_DEST}"

log ""
log "✅ RunnerRunner Agent deployed to ${HOST}"
log ""
log "Service management:"
log "  Status:   ssh ${SSH_USER}@${HOST} 'launchctl print system/com.runnerrunner.agent'"
log "  Logs:     ssh ${SSH_USER}@${HOST} 'tail -f /var/log/runnerrunner-agent.log'"
log "  Stop:     ssh ${SSH_USER}@${HOST} 'launchctl bootout system/com.runnerrunner.agent'"
log "  Start:    ssh ${SSH_USER}@${HOST} 'launchctl bootstrap system ${PLIST_DEST}'"
log "  Restart:  ssh ${SSH_USER}@${HOST} 'launchctl kickstart -k system/com.runnerrunner.agent'"
log ""
log "Re-deploy after code changes:"
log "  ./deploy/macos/deploy-agent.sh ${HOST}"
