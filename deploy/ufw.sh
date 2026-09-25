#!/usr/bin/env bash
# Firewall for the VPS: only SSH (rate-limited), HTTP and HTTPS from the Internet;
# anything on the Tailscale interface. Idempotent.
#
# Docker publishes container ports through its own iptables chains, BEFORE ufw:
# a published port stays reachable whatever ufw says. Bind such ports to 127.0.0.1
# (or the Tailscale IP) in their compose file instead — see deploy/README.md.
set -euo pipefail

sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw limit 22/tcp comment 'ssh (rate-limited)'
sudo ufw allow 80/tcp comment 'http -> https redirect'
sudo ufw allow 443/tcp comment 'https (nginx)'
sudo ufw allow in on tailscale0 comment 'tailscale'
sudo ufw --force enable
sudo ufw status verbose
