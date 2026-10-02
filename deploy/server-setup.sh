#!/usr/bin/env bash
#
# One-time setup of XPilot on an Ubuntu server, as root:
#   bash server-setup.sh <public host name or IP>
#
# Safe to run again: it skips what is already done, and never overwrites /etc/xpilot/server.env once it
# exists. It only adds things of its own (a user, /opt/xpilot, two services and two UDP ports), so it can sit
# beside whatever else the machine runs.

set -euo pipefail

PUBLIC_HOST="${1:?Usage: bash server-setup.sh <public host name or IP>}"
APP_USER=xpilot
APP_DIR=/opt/xpilot
CONFIG_DIR=/etc/xpilot
GAME_PORT=15345
MASTER_PORT=15346

log() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }

if [[ $EUID -ne 0 ]]; then
	echo "Must be run as root." >&2
	exit 1
fi

log "Creating the $APP_USER user and folders"
if ! id "$APP_USER" &>/dev/null; then
	useradd --system --no-create-home --shell /usr/sbin/nologin "$APP_USER"
fi
install -d -o root -g root -m 755 "$APP_DIR" "$APP_DIR/releases"
install -d -o root -g "$APP_USER" -m 750 "$CONFIG_DIR"

log "Writing settings (kept if they already exist)"
if [[ ! -f "$CONFIG_DIR/server.env" ]]; then
	cat > "$CONFIG_DIR/server.env" <<ENV
# Settings for xpilot-server and xpilot-master. Change, then: systemctl restart xpilot-server
XPILOT_NAME=XPilot at $PUBLIC_HOST
XPILOT_PORT=$GAME_PORT
# dogfight, race or ball: the server goes through every map of that mode.
XPILOT_MODE=dogfight
XPILOT_BOTS=4
XPILOT_DIFFICULTY=normal
# The game server lists itself here. Use the public name, not localhost, so the master records the
# address players can actually reach.
XPILOT_MASTER=$PUBLIC_HOST:$MASTER_PORT
XPILOT_MASTER_PORT=$MASTER_PORT
# Anything else, e.g. --map arena,caverns or --time-limit 300
XPILOT_EXTRA_ARGS=
ENV
	chmod 640 "$CONFIG_DIR/server.env"
	chown root:"$APP_USER" "$CONFIG_DIR/server.env"
fi

log "Opening UDP $GAME_PORT (game) and $MASTER_PORT (server list) in ufw"
if command -v ufw &>/dev/null && ufw status | grep -q "Status: active"; then
	ufw allow "$GAME_PORT/udp" comment 'XPilot game'
	ufw allow "$MASTER_PORT/udp" comment 'XPilot master server'
else
	echo "ufw is not active; nothing to open here."
fi

log "Installing the services"
install -m 644 /tmp/xpilot-server.service /etc/systemd/system/xpilot-server.service
install -m 644 /tmp/xpilot-master.service /etc/systemd/system/xpilot-master.service
rm -f /tmp/xpilot-server.service /tmp/xpilot-master.service
systemctl daemon-reload
systemctl enable xpilot-server xpilot-master

log "Done"
echo "Deploy a release with deploy\\deploy.ps1; the services start once one is in place."
echo "Remember the cloud firewall too: allow UDP $GAME_PORT and $MASTER_PORT from anywhere."
