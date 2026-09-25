#!/bin/bash
set -Eeuo pipefail

install_user="${1:-ubuntu}"
user_home="$(getent passwd "$install_user" | cut -d: -f6)"
if [[ -z "$user_home" ]]; then
    echo "Claude-Benutzer '$install_user' hat kein Home-Verzeichnis." >&2
    exit 1
fi

sudo -u "$install_user" -H bash -lc \
    'curl -fsSL https://claude.ai/install.sh | bash -s stable'

sudo -u "$install_user" -H bash -lc 'claude --version'
install -d /usr/local/bin
ln -sfn "$user_home/.local/bin/claude" /usr/local/bin/claude

install -d /var/lib/corebridge
touch /var/lib/corebridge/claude.ready
