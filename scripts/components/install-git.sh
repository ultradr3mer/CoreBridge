#!/bin/bash
set -Eeuo pipefail

# Richtet git für den Installationsbenutzer ein. Bare-Repo und Arbeitskopie legt
# `cb repo-send` an; Commits wandern als git bundle per multipass transfer.

install_user="${1:-ubuntu}"
if [[ -z "$(getent passwd "$install_user" | cut -d: -f6)" ]]; then
    echo "git-Benutzer '$install_user' hat kein Home-Verzeichnis." >&2
    exit 1
fi

# Postcondition: git für den Benutzer.
sudo -u "$install_user" -H git --version

install -d /var/lib/corebridge
touch /var/lib/corebridge/git.ready
