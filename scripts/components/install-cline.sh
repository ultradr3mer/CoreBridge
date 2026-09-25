#!/bin/bash
set -Eeuo pipefail

# Installiert die Cline-CLI (Coding-Agent, npm-Paket "cline") global im Gast.
# Benötigt Node.js >= 20; ist das Archiv-Node zu alt oder fehlt es, wird das
# aktuelle LTS von NodeSource installiert.

min_node_major=20
install_user="${1:-ubuntu}"
user_home="$(getent passwd "$install_user" | cut -d: -f6)"
if [[ -z "$user_home" ]]; then
    echo "Cline-Benutzer '$install_user' hat kein Home-Verzeichnis." >&2
    exit 1
fi

node_major() {
    command -v node >/dev/null 2>&1 || { echo 0; return; }
    node --version | sed -E 's/^v([0-9]+).*/\1/'
}

export DEBIAN_FRONTEND=noninteractive
if (( $(node_major) < min_node_major )); then
    echo "Node.js fehlt oder ist älter als ${min_node_major} — Installation des LTS von NodeSource."
    curl -fsSL https://deb.nodesource.com/setup_lts.x | bash -
    apt-get install -y nodejs
fi

if (( $(node_major) < min_node_major )); then
    echo "Node.js $(node --version 2>/dev/null || echo fehlt) ist zu alt für Cline (>= ${min_node_major})." >&2
    exit 1
fi

if ! command -v npm >/dev/null 2>&1; then
    apt-get install -y npm
fi

# Neuere npm-Versionen führen postinstall-Skripte nur noch auf ausdrückliche
# Erlaubnis aus; ältere ignorieren die unbekannte Option.
npm install -g --allow-scripts=cline,protobufjs cline

# Postcondition als Installationsbenutzer: Kommando ist im PATH und startet.
sudo -u "$install_user" -H bash -lc 'command -v cline && cline --version'

install -d /var/lib/corebridge
touch /var/lib/corebridge/cline.ready
