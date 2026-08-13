#!/usr/bin/env bash
# CardVault installer for Linux (Debian/Ubuntu, Proxmox containers, a Pi).
#
#   sudo ./linux/install.sh
#
# Builds from this source tree into /opt/card-vault, installs a systemd unit
# and starts it. Needs the .NET SDK and Node.js on the machine.
#
# Your collection lives in /var/lib/card-vault, outside the install folder,
# so updates replace the app without touching your cards.

set -euo pipefail

APP_DIR=/opt/card-vault
DATA_DIR=/var/lib/card-vault
SERVICE=card-vault
RUN_USER=cardvault

step() { printf '\n==> %s\n' "$1"; }

if [[ $EUID -ne 0 ]]; then
  echo "Run this with sudo." >&2
  exit 1
fi

SRC_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ ! -f "$SRC_ROOT/server/CardVault.csproj" ]]; then
  echo "Can't find server/CardVault.csproj — run this from inside the repo." >&2
  exit 1
fi
step "Using source tree: $SRC_ROOT"

for tool in dotnet npm; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool not found on PATH." >&2; exit 1; }
done

# Stop before replacing the binary; systemd holds it open otherwise.
if systemctl is-active --quiet "$SERVICE"; then
  step "Stopping $SERVICE"
  systemctl stop "$SERVICE"
fi

step "Building the web UI"
(cd "$SRC_ROOT/client" && { [[ -d node_modules ]] || npm install --no-fund --no-audit; } && npm run build)

step "Building the server"
(cd "$SRC_ROOT/server" && dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained true -o "$APP_DIR")

[[ -x "$APP_DIR/CardVault" ]] || { echo "Build finished but $APP_DIR/CardVault is missing." >&2; exit 1; }
[[ -f "$APP_DIR/wwwroot/index.html" ]] || { echo "Build finished but wwwroot is missing — the app would not load." >&2; exit 1; }

# Service account, created once and left alone on later runs.
if ! id -u "$RUN_USER" >/dev/null 2>&1; then
  step "Creating service user $RUN_USER"
  useradd --system --no-create-home --shell /usr/sbin/nologin "$RUN_USER"
fi

step "Preparing $DATA_DIR"
mkdir -p "$DATA_DIR"
chown -R "$RUN_USER:$RUN_USER" "$DATA_DIR"
chown -R root:root "$APP_DIR"

step "Installing systemd unit"
install -m 0644 "$SRC_ROOT/linux/$SERVICE.service" "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload
systemctl enable "$SERVICE"
systemctl restart "$SERVICE"

# Report what's actually running rather than assuming it came up.
sleep 2
if systemctl is-active --quiet "$SERVICE"; then
  ip=$(hostname -I 2>/dev/null | awk '{print $1}')
  printf '\nCardVault installed and running.\n'
  printf 'Collection: %s\n' "$DATA_DIR"
  printf 'Open:       http://%s:5188\n' "${ip:-localhost}"
  printf '\nIt listens on your network with no password by default —\n'
  printf 'set one under Settings if that network is shared.\n'
else
  printf '\nThe service failed to start. Logs:\n  journalctl -u %s -n 50 --no-pager\n' "$SERVICE" >&2
  exit 1
fi
