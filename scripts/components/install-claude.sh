#!/bin/bash
set -Eeuo pipefail

sudo -u ubuntu -H bash -lc \
  'curl -fsSL https://claude.ai/install.sh | bash -s stable'

sudo -u ubuntu -H bash -lc 'claude --version'
ln -sfn /home/ubuntu/.local/bin/claude /usr/local/bin/claude

mkdir -p /var/lib/corebridge
touch /var/lib/corebridge/claude.ready
