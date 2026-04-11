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
SSHPASS="${SSHPASS:-}"
INSTALL_DIR="/opt/runnerrunner"
PLIST_NAME="com.runnerrunner.agent.plist"
PLIST_DEST="~/Library/LaunchAgents/${PLIST_NAME}"
OLD_PLIST_DEST="/Library/LaunchDaemons/${PLIST_NAME}"
OLD_AGENT_PLIST="/Library/LaunchAgents/${PLIST_NAME}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
AGENT_PROJECT="${PROJECT_ROOT}/src/RunnerRunner.Agent/RunnerRunner.Agent.csproj"
PUBLISH_DIR="${PROJECT_ROOT}/artifacts/macos-agent"
RID="${RID:-osx-arm64}"

log() { echo "▸ $*"; }

# SSH wrapper: uses sshpass if SSHPASS is set
_ssh() {
    if [[ -n "${SSHPASS}" ]]; then
        sshpass -p "${SSHPASS}" ssh -t -o StrictHostKeyChecking=no "${SSH_USER}@${HOST}" "$@"
    else
        ssh -t "${SSH_USER}@${HOST}" "$@"
    fi
}
_scp() {
    if [[ -n "${SSHPASS}" ]]; then
        sshpass -p "${SSHPASS}" scp -o StrictHostKeyChecking=no -r "$1" "${SSH_USER}@${HOST}:$2"
    else
        scp -r "$1" "${SSH_USER}@${HOST}:$2"
    fi
}

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
    "ServerUrl": "${RUNNERRUNNER_SERVER_URL:-http://192.168.2.4:4779}",
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
_ssh "mkdir -p ${INSTALL_DIR}"

# --- Step 4: Stop existing service if running ---
log "Stopping existing agent service (if any)..."
_ssh "launchctl bootout gui/\$(id -u)/${PLIST_NAME%.plist} 2>/dev/null; true"

# Clean up old plists from previous install locations
_ssh "sudo rm -f ${OLD_PLIST_DEST} ${OLD_AGENT_PLIST} 2>/dev/null; true"

# --- Step 5: Copy files to remote host ---
log "Copying agent binary and config to ${HOST}:${INSTALL_DIR}..."
_scp "${PUBLISH_DIR}" "${INSTALL_DIR}"

log "Copying launchd plist..."
_scp "${SCRIPT_DIR}/${PLIST_NAME}" "/tmp/${PLIST_NAME}"

# --- Step 6: Set permissions and install plist ---
log "Setting up agent binary..."
_ssh "if [ -d ${INSTALL_DIR}/macos-agent ]; then mv ${INSTALL_DIR}/macos-agent/* ${INSTALL_DIR}/ && rmdir ${INSTALL_DIR}/macos-agent; fi && chmod +x ${INSTALL_DIR}/RunnerRunner.Agent"

log "Installing LaunchAgent plist..."
_ssh "mkdir -p ~/Library/LaunchAgents && cp /tmp/${PLIST_NAME} ~/Library/LaunchAgents/${PLIST_NAME}"

# --- Step 7: Load and start the service in user domain ---
log "Loading LaunchAgent service..."
_ssh "launchctl bootstrap gui/\$(id -u) ~/Library/LaunchAgents/${PLIST_NAME} 2>/dev/null || launchctl kickstart -k gui/\$(id -u)/${PLIST_NAME%.plist} 2>/dev/null || true"

log ""
log "✅ RunnerRunner Agent deployed to ${HOST}"
log ""
log "Service management:"
log "  Status:   ssh ${SSH_USER}@${HOST} 'launchctl print gui/\$(id -u)/com.runnerrunner.agent'"
log "  Logs:     ssh ${SSH_USER}@${HOST} 'tail -f /tmp/runnerrunner-agent.log'"
log "  Stop:     ssh ${SSH_USER}@${HOST} 'launchctl bootout gui/\$(id -u)/com.runnerrunner.agent'"
log "  Start:    ssh ${SSH_USER}@${HOST} 'launchctl bootstrap gui/\$(id -u) ~/Library/LaunchAgents/${PLIST_NAME}'"
log "  Restart:  ssh ${SSH_USER}@${HOST} 'launchctl kickstart -k gui/\$(id -u)/com.runnerrunner.agent'"
log ""
log "Re-deploy after code changes:"
log "  ./deploy/macos/deploy-agent.sh ${HOST}"
