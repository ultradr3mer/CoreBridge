#!/bin/bash
set -Eeuo pipefail

install_user="${1:-ubuntu}"
user_home="$(getent passwd "$install_user" | cut -d: -f6)"
if [[ -z "$user_home" ]]; then
    echo "Dotnet-Benutzer '$install_user' hat kein Home-Verzeichnis." >&2
    exit 1
fi

install -d -o "$install_user" -g "$install_user" "$user_home/.local/bin"

dotnet_source="/snap/dotnet-sdk-100/current/usr/bin/dotnet"
ln -sfn "$dotnet_source" "$user_home/.local/bin/dotnet"
# Auch ohne Login-Shell (multipass exec, von Claude Code gestartete Prozesse)
# im PATH, wie /usr/local/bin/claude; die SDK findet der Host über seinen
# eigenen Pfad, DOTNET_ROOT aus der .profile ist dafür nicht nötig.
install -d /usr/local/bin
ln -sfn "$dotnet_source" /usr/local/bin/dotnet

profile="$user_home/.profile"
begin_marker="# BEGIN CoreBridge dotnet"
end_marker="# END CoreBridge dotnet"
if ! grep -qF "$begin_marker" "$profile" 2>/dev/null; then
    cat >> "$profile" <<'EOF'
# BEGIN CoreBridge dotnet
export DOTNET_ROOT=/snap/dotnet-sdk-100/current/usr/lib/dotnet
export LD_LIBRARY_PATH="$HOME/.local/lib:${LD_LIBRARY_PATH:-}"
# END CoreBridge dotnet
EOF
fi
chown "$install_user:$install_user" "$profile"

if ! ldd "$dotnet_source" 2>/dev/null | grep -q "not found"; then
    su - "$install_user" -c 'command -v dotnet && dotnet --info'
    sudo -u "$install_user" -H /bin/sh -c 'command -v dotnet >/dev/null && dotnet --version'
else
    echo "Dotnet-Snap meldet fehlende Laufzeitbibliotheken." >&2
    ldd "$dotnet_source" >&2 || true
    exit 1
fi

install -d /var/lib/corebridge
touch /var/lib/corebridge/dotnet.ready
