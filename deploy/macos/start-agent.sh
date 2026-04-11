#!/bin/bash
# RunnerRunner Agent start/restart script for macOS
# Called by launchd or manually to start the agent

INSTALL_DIR="/opt/runnerrunner"
LOG="/tmp/runnerrunner-agent.log"
PID_FILE="/tmp/runnerrunner-agent.pid"

# Stop existing instance
if [ -f "$PID_FILE" ]; then
    OLD_PID=$(cat "$PID_FILE")
    if kill -0 "$OLD_PID" 2>/dev/null; then
        kill -TERM "$OLD_PID" 2>/dev/null
        sleep 3
        kill -0 "$OLD_PID" 2>/dev/null && kill -9 "$OLD_PID" 2>/dev/null
    fi
    rm -f "$PID_FILE"
fi

# Start agent
cd "$INSTALL_DIR"
export DOTNET_ENVIRONMENT=Production
export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"

exec "$INSTALL_DIR/RunnerRunner.Agent" >> "$LOG" 2>&1
