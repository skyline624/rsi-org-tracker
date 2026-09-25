#!/bin/bash
#
# Arrête les 3 services de développement (Collector, Collector.Api, Collector.Web)
# lancés par scripts/dev-start.sh, en arrêtant la session tmux 'collector'.
# Ne tue aucun autre process : un « next-server » ou un dotnet qui tourne ailleurs
# n'est pas forcément le nôtre (sur le serveur, ce serait la production).
#

set -euo pipefail

SESSION="collector"

# ── Jamais à côté de la production ─────────────────────────
# Sur le serveur, les services systemd tournent déjà : un second collector sur la
# même tracker.db, ou un arrêt qui tuerait le serveur web de production, serait le
# résultat. Ce script est réservé au poste de développement.
if command -v systemctl >/dev/null 2>&1     && systemctl is-active --quiet sc-api sc-collector sc-web 2>/dev/null; then
    echo "✗ Les services de production (sc-api / sc-collector / sc-web) sont actifs ici :" >&2
    echo "  ce script est réservé au développement local. Utiliser systemctl." >&2
    exit 1
fi

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

# Vérification finale des ports
echo
for port in 3000 5000 5001; do
    if ss -tulpn 2>/dev/null | grep -q ":$port "; then
        echo "  ⚠  port $port toujours occupé (process hors de la session : à arrêter à la main)"
    else
        echo "  ✓ port $port libre"
    fi
done

echo
echo "✓ Arrêt terminé."
