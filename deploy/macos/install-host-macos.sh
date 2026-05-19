#!/usr/bin/env bash
set -euo pipefail

VERSION="${RUNNERRUNNER_VERSION:-latest}"
INSTALL_ROOT="${INSTALL_ROOT:-${HOME}/.runnerrunner}"
RELEASE_BASE_URL="${RUNNERRUNNER_RELEASE_BASE_URL:-https://github.com/redth/RunnerRunner/releases/latest/download}"
SERVICE_LABEL="com.runnerrunner.hostworker"
HOST_NAME="${HOST_NAME:-$(hostname -s)}"
HOST_ID="${HOST_ID:-}"
SERVER_URL="${SERVER_URL:-}"
ENROLLMENT_TOKEN="${ENROLLMENT_TOKEN:-}"
HOSTWORKER_HTTP_PROXY="${HOSTWORKER_HTTP_PROXY:-}"
HOSTWORKER_HTTPS_PROXY="${HOSTWORKER_HTTPS_PROXY:-}"
HOSTWORKER_NO_PROXY="${HOSTWORKER_NO_PROXY:-}"
PRESERVE_CONFIG=0

usage() {
    cat <<USAGE
Usage: ./install-host-macos.sh --server-url URL --enrollment-token TOKEN [options]

Options:
  --version VERSION                    HostWorker version to install (default: latest)
  --install-root PATH                  Install root (default: ~/.runnerrunner)
  --host-id ID                         Host ID (default: hostname)
  --host-name NAME                     Display name (default: hostname)
  --server-url URL                     RunnerRunner server URL, for example https://runner.example.com
  --enrollment-token TOKEN             Enrollment token created on the server
  --http-proxy URL                     Optional HTTP proxy for HostWorker outbound connections
  --https-proxy URL                    Optional HTTPS proxy for HostWorker outbound connections
  --no-proxy LIST                      Optional comma-separated proxy bypass list
  --preserve-config                    Preserve the existing appsettings.Production.json during update

macOS hosts install as a LaunchAgent for the current interactive user so Tart,
Xcode, Keychain, and user-session resources remain available.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --install-root) INSTALL_ROOT="$2"; shift 2 ;;
        --host-id) HOST_ID="$2"; shift 2 ;;
        --host-name) HOST_NAME="$2"; shift 2 ;;
        --server-url) SERVER_URL="$2"; shift 2 ;;
        --enrollment-token) ENROLLMENT_TOKEN="$2"; shift 2 ;;
        --http-proxy) HOSTWORKER_HTTP_PROXY="$2"; shift 2 ;;
        --https-proxy) HOSTWORKER_HTTPS_PROXY="$2"; shift 2 ;;
        --no-proxy) HOSTWORKER_NO_PROXY="$2"; shift 2 ;;
        --preserve-config) PRESERVE_CONFIG=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

existing_settings="${INSTALL_ROOT}/current/appsettings.Production.json"

if [[ "${PRESERVE_CONFIG}" != "1" && -z "${SERVER_URL}" ]]; then
    echo "--server-url is required." >&2
    exit 1
fi

if [[ "${PRESERVE_CONFIG}" != "1" && -z "${ENROLLMENT_TOKEN}" ]]; then
    echo "--enrollment-token is required." >&2
    exit 1
fi

if [[ "${PRESERVE_CONFIG}" == "1" && ! -f "${existing_settings}" ]]; then
    echo "--preserve-config requires an existing ${existing_settings}." >&2
    exit 1
fi

arch="$(uname -m)"
case "${arch}" in
    arm64|aarch64) rid="osx-arm64" ;;
    x86_64) rid="osx-x64" ;;
    *) echo "Unsupported macOS architecture: ${arch}" >&2; exit 1 ;;
esac

if [[ -z "${HOST_ID}" ]]; then
    HOST_ID="${HOST_NAME}"
fi

if [[ -n "${HOSTWORKER_HTTP_PROXY}" ]]; then
    export HTTP_PROXY="${HOSTWORKER_HTTP_PROXY}"
fi
if [[ -n "${HOSTWORKER_HTTPS_PROXY}" ]]; then
    export HTTPS_PROXY="${HOSTWORKER_HTTPS_PROXY}"
fi
if [[ -n "${HOSTWORKER_NO_PROXY}" ]]; then
    export NO_PROXY="${HOSTWORKER_NO_PROXY}"
fi

version_dir="${INSTALL_ROOT}/versions/${VERSION}"
archive="runnerrunner-hostworker-${rid}.tar.gz"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT

mkdir -p "${version_dir}" "${INSTALL_ROOT}/logs"
curl -fsSL "${RELEASE_BASE_URL}/${archive}" -o "${tmp_dir}/${archive}"
tar -xzf "${tmp_dir}/${archive}" -C "${version_dir}"
chmod +x "${version_dir}/RunnerRunner.HostWorker"
codesign --force -s - "${version_dir}/RunnerRunner.HostWorker" >/dev/null 2>&1 || true

if [[ "${PRESERVE_CONFIG}" == "1" ]]; then
    cp "${existing_settings}" "${version_dir}/appsettings.Production.json"
else
    cat > "${version_dir}/appsettings.Production.json" <<JSON
{
  "HostWorker": {
    "ServerUrl": "${SERVER_URL}",
    "EnrollmentToken": "${ENROLLMENT_TOKEN}",
    "HostId": "${HOST_ID}",
    "HostName": "${HOST_NAME}",
    "Platform": "MacOS",
    "DataRoot": "${INSTALL_ROOT}",
    "LogRoot": "${INSTALL_ROOT}/logs",
    "HttpProxy": "${HOSTWORKER_HTTP_PROXY}",
    "HttpsProxy": "${HOSTWORKER_HTTPS_PROXY}",
    "NoProxy": "${HOSTWORKER_NO_PROXY}"
  }
}
JSON
fi
chmod 0600 "${version_dir}/appsettings.Production.json"

ln -sfn "${version_dir}" "${INSTALL_ROOT}/current"

launch_agents_dir="${HOME}/Library/LaunchAgents"
plist_path="${launch_agents_dir}/${SERVICE_LABEL}.plist"
mkdir -p "${launch_agents_dir}"

cat > "${plist_path}" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${SERVICE_LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${INSTALL_ROOT}/current/RunnerRunner.HostWorker</string>
  </array>
  <key>WorkingDirectory</key>
  <string>${INSTALL_ROOT}/current</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>EnvironmentVariables</key>
  <dict>
    <key>DOTNET_ENVIRONMENT</key>
    <string>Production</string>
    <key>PATH</key>
    <string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>
    <key>HTTP_PROXY</key>
    <string>${HOSTWORKER_HTTP_PROXY}</string>
    <key>HTTPS_PROXY</key>
    <string>${HOSTWORKER_HTTPS_PROXY}</string>
    <key>NO_PROXY</key>
    <string>${HOSTWORKER_NO_PROXY}</string>
  </dict>
  <key>StandardOutPath</key>
  <string>${INSTALL_ROOT}/logs/hostworker.out.log</string>
  <key>StandardErrorPath</key>
  <string>${INSTALL_ROOT}/logs/hostworker.err.log</string>
</dict>
</plist>
PLIST

launchctl bootout "gui/$(id -u)/${SERVICE_LABEL}" >/dev/null 2>&1 || true
launchctl bootstrap "gui/$(id -u)" "${plist_path}"
launchctl kickstart -k "gui/$(id -u)/${SERVICE_LABEL}"

cat > "${INSTALL_ROOT}/runnerrunner-host" <<HOSTCTL
#!/usr/bin/env bash
set -euo pipefail
case "\${1:-status}" in
  status) exec launchctl print "gui/$(id -u)/${SERVICE_LABEL}" ;;
  logs) exec tail -f "${INSTALL_ROOT}/logs/hostworker.out.log" ;;
  restart) exec launchctl kickstart -k "gui/$(id -u)/${SERVICE_LABEL}" ;;
  *) echo "Usage: runnerrunner-host {status|logs|restart}" >&2; exit 1 ;;
esac
HOSTCTL
chmod 0755 "${INSTALL_ROOT}/runnerrunner-host"

echo "RunnerRunner HostWorker ${VERSION} installed for ${HOST_NAME}."
echo "Use: ${INSTALL_ROOT}/runnerrunner-host status | logs | restart"
