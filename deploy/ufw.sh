#!/usr/bin/env bash
# Firewall for the VPS: only SSH, HTTP and HTTPS from the Internet;
# anything on the Tailscale interface. Idempotent.
#
# Docker publishes container ports through its own iptables chains, BEFORE ufw:
# a published port stays reachable whatever ufw says. Bind such ports to 127.0.0.1
# (or the Tailscale IP) in their compose file instead — see deploy/README.md.
set -euo pipefail

sudo ufw default deny incoming
sudo ufw default allow outgoing
# Not "ufw limit": it blocks an address after 6 connections in 30 s, and remote-editor
# clients (VS Code Remote-SSH and the like) open five at once, which locked the owner out
# on 2026-09-28. Brute force is covered by key-only logins and fail2ban. "allow" replaces
# an existing limit rule on the same port.
sudo ufw allow 22/tcp comment 'ssh (keys only; fail2ban bans brute force)'
sudo ufw allow 80/tcp comment 'http -> https redirect'
sudo ufw allow 443/tcp comment 'https (nginx)'
sudo ufw allow in on tailscale0 comment 'tailscale'
sudo ufw --force enable
sudo ufw status verbose
