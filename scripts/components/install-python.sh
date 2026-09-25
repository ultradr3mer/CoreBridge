#!/bin/bash
set -Eeuo pipefail

# Installiert Python 3.14 im Gast: bevorzugt aus dem Ubuntu-Archiv, sonst als
# uv-verwaltetes Python für den Installationsbenutzer. Danach gibt es immer
# /usr/local/bin/python3.14, und venvs funktionieren.

python_version="3.14"
install_user="${1:-ubuntu}"
user_home="$(getent passwd "$install_user" | cut -d: -f6)"
if [[ -z "$user_home" ]]; then
    echo "Python-Benutzer '$install_user' hat kein Home-Verzeichnis." >&2
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive
if apt-cache show "python${python_version}" >/dev/null 2>&1; then
    packages=("python${python_version}")
    if apt-cache show "python${python_version}-venv" >/dev/null 2>&1; then
        packages+=("python${python_version}-venv")
    fi
    apt-get install -y --no-install-recommends "${packages[@]}"
    python_bin="$(command -v "python${python_version}")"
else
    echo "python${python_version} nicht im Archiv — Installation per uv."
    sudo -u "$install_user" -H bash -lc \
        'command -v uv >/dev/null || curl -LsSf https://astral.sh/uv/install.sh | sh'
    sudo -u "$install_user" -H bash -lc "\$HOME/.local/bin/uv python install ${python_version}"
    python_bin="$(sudo -u "$install_user" -H bash -lc "\$HOME/.local/bin/uv python find ${python_version}")"
fi

if [[ -z "${python_bin:-}" || ! -x "$python_bin" ]]; then
    echo "Python ${python_version} wurde nicht gefunden (${python_bin:-leer})." >&2
    exit 1
fi

install -d /usr/local/bin
if [[ "$python_bin" != "/usr/local/bin/python${python_version}" ]]; then
    ln -sfn "$python_bin" "/usr/local/bin/python${python_version}"
fi

# Postconditions als Installationsbenutzer: richtige Version und funktionierendes venv.
sudo -u "$install_user" -H bash -lc "
    set -e
    python${python_version} --version
    python${python_version} -c 'import sys; assert sys.version_info[:2] == (${python_version/./, })'
    check_dir=\$(mktemp -d)
    python${python_version} -m venv \"\$check_dir/venv\"
    rm -rf \"\$check_dir\"
"

install -d /var/lib/corebridge
touch /var/lib/corebridge/python.ready
