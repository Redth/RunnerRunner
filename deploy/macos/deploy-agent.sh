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
# SSH without TTY (for nohup — TTY sends SIGHUP on disconnect)
_ssh_notty() {
    if [[ -n "${SSHPASS}" ]]; then
        sshpass -p "${SSHPASS}" ssh -o StrictHostKeyChecking=no "${SSH_USER}@${HOST}" "$@"
    else
        ssh "${SSH_USER}@${HOST}" "$@"
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
_ssh_notty "mkdir -p ${INSTALL_DIR}"

# --- Step 4: Stop existing agent ---
log "Stopping existing agent..."
_ssh_notty "pgrep -f RunnerRunner.Agent | xargs kill 2>/dev/null; launchctl remove com.runnerrunner.agent 2>/dev/null; true"

# Clean up old plists (non-sudo locations only)
_ssh_notty "rm -f ~/Library/LaunchAgents/${PLIST_NAME} 2>/dev/null; true"

# --- Step 5: Copy files to remote host ---
log "Copying agent binary and config to ${HOST}:${INSTALL_DIR}..."
_scp "${PUBLISH_DIR}" "${INSTALL_DIR}"

log "Copying start script..."
_scp "${SCRIPT_DIR}/start-agent.sh" "${INSTALL_DIR}/start-agent.sh"

# --- Step 6: Set permissions and sign binary ---
log "Setting up agent binary..."
_ssh_notty "if [ -d ${INSTALL_DIR}/macos-agent ]; then mv ${INSTALL_DIR}/macos-agent/* ${INSTALL_DIR}/ && rmdir ${INSTALL_DIR}/macos-agent; fi && chmod +x ${INSTALL_DIR}/RunnerRunner.Agent ${INSTALL_DIR}/start-agent.sh && codesign --force -s - ${INSTALL_DIR}/RunnerRunner.Agent"

# --- Step 7: Start the agent via nohup (survives SSH disconnect) ---
# Note: launchd has macOS socket restrictions that prevent .NET from connecting.
# Starting via nohup from an SSH session inherits the interactive network stack.
log "Starting agent..."
# Must use -tt (force TTY) — macOS restricts .NET sockets in non-TTY SSH sessions
if [[ -n "${SSHPASS}" ]]; then
    sshpass -p "${SSHPASS}" ssh -tt -o StrictHostKeyChecking=no "${SSH_USER}@${HOST}" "cd ${INSTALL_DIR} && truncate -s 0 /tmp/runnerrunner-agent.log 2>/dev/null; nohup ${INSTALL_DIR}/start-agent.sh </dev/null >/dev/null 2>&1 & sleep 1 && exit"
else
    ssh -tt "${SSH_USER}@${HOST}" "cd ${INSTALL_DIR} && truncate -s 0 /tmp/runnerrunner-agent.log 2>/dev/null; nohup ${INSTALL_DIR}/start-agent.sh </dev/null >/dev/null 2>&1 & sleep 1 && exit"
fi

sleep 8

# Verify it connected
_ssh_notty "grep -c 'Connected to' /tmp/runnerrunner-agent.log 2>/dev/null | xargs -I{} echo 'Connected: {} time(s)'"

log ""
log "✅ RunnerRunner Agent deployed to ${HOST}"
log ""
log "Service management:"
log "  Logs:     ssh ${SSH_USER}@${HOST} 'tail -f /tmp/runnerrunner-agent.log'"
log "  Stop:     ssh ${SSH_USER}@${HOST} 'pkill -f RunnerRunner.Agent'"
log "  Restart:  ssh ${SSH_USER}@${HOST} 'pkill -f RunnerRunner.Agent; nohup /opt/runnerrunner/start-agent.sh > /dev/null 2>&1 &'"
log ""
log "Re-deploy after code changes:"
log "  ./deploy/macos/deploy-agent.sh ${HOST}"
