#!/bin/bash
#
# Arrête proprement les 3 services (Collector, Collector.Api, Collector.Web)
# en tuant la session tmux 'collector' lancée par start.sh, puis en nettoyant
# les éventuels process orphelins (next-server, dotnet-run).
#

set -euo pipefail

SESSION="collector"

echo "↻ Arrêt de la session tmux '$SESSION'…"
if tmux has-session -t "$SESSION" 2>/dev/null; then
    # Envoie Ctrl-C dans chaque pane pour que les process enfants se terminent
    # proprement (commits DB, fermeture Kestrel…), puis tue la session.
    for p in 0.0 0.1 0.2; do
        tmux send-keys -t "$SESSION:$p" C-c 2>/dev/null || true
    done
    sleep 2
    tmux kill-session -t "$SESSION" 2>/dev/null || true
    echo "  session tmux terminée"
else
    echo "  pas de session tmux active"
fi

# Filet de sécurité : tue les process orphelins (si une session précédente
# a perdu le contrôle de ses enfants via kill -9).
orphans_next=$(pgrep -f "next-server" 2>/dev/null || true)
if [ -n "$orphans_next" ]; then
    echo "↻ Kill orphaned next-server: $orphans_next"
    kill $orphans_next 2>/dev/null || true
    sleep 1
    pgrep -f "next-server" 2>/dev/null | xargs -r kill -9 2>/dev/null || true
fi

orphans_dotnet=$(pgrep -f "bin/(collector|api)/" 2>/dev/null || true)
if [ -n "$orphans_dotnet" ]; then
    echo "↻ Kill orphaned dotnet: $orphans_dotnet"
    kill $orphans_dotnet 2>/dev/null || true
    sleep 1
    pgrep -f "bin/(collector|api)/" 2>/dev/null | xargs -r kill -9 2>/dev/null || true
fi

# Vérification finale des ports
echo
for port in 3000 5000 5001; do
    if ss -tulpn 2>/dev/null | grep -q ":$port "; then
        echo "  ⚠  port $port toujours occupé"
    else
        echo "  ✓ port $port libre"
    fi
done

echo
echo "✓ Arrêt terminé."
