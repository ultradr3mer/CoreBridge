#!/bin/bash
set -Eeuo pipefail

# Richtet git und den SSH-Zugang vom Host ein: der öffentliche CoreBridge-Host-
# Schlüssel (von cloud-init bzw. vm-install nach /etc/corebridge/host_authorized_keys
# gelegt) wird in die authorized_keys des Installationsbenutzers eingetragen, damit
# der Host per git über ssh://<user>@<vm>.mshome.net pushen und fetchen kann.

install_user="${1:-ubuntu}"
key_file="/etc/corebridge/host_authorized_keys"
user_home="$(getent passwd "$install_user" | cut -d: -f6)"
if [[ -z "$user_home" ]]; then
    echo "git-Benutzer '$install_user' hat kein Home-Verzeichnis." >&2
    exit 1
fi

if [[ ! -s "$key_file" ]]; then
    echo "Host-Schlüssel fehlt: $key_file" >&2
    exit 1
fi

user_group="$(id -gn "$install_user")"
install -d -m 0700 -o "$install_user" -g "$user_group" "$user_home/.ssh"
authorized="$user_home/.ssh/authorized_keys"
touch "$authorized"
while IFS= read -r key || [[ -n "$key" ]]; do
    [[ -z "$key" ]] && continue
    grep -qxF "$key" "$authorized" || printf '%s\n' "$key" >> "$authorized"
done < "$key_file"
chown "$install_user:$user_group" "$authorized"
chmod 0600 "$authorized"

# sshd: Ubuntu startet ihn per Socket-Aktivierung; läuft keiner, wird er aktiviert.
if ! systemctl is-active --quiet ssh.socket && ! systemctl is-active --quiet ssh; then
    systemctl enable --now ssh
fi

# Postconditions: git für den Benutzer, alle Host-Schlüssel eingetragen, Port 22 offen.
sudo -u "$install_user" -H git --version
while IFS= read -r key || [[ -n "$key" ]]; do
    [[ -z "$key" ]] && continue
    grep -qxF "$key" "$authorized"
done < "$key_file"
ss -ltn | grep -q ':22 '

install -d /var/lib/corebridge
touch /var/lib/corebridge/git.ready
