#!/bin/bash
#
# Lance les 3 services de l'intel Star Citizen dans une session tmux unique :
#
#   ┌─────────────────────┬──────────────────────┐
#   │  Collector (main)   │   Collector.Api      │
#   │  src/Collector      │   :5001 HTTPS        │
#   ├─────────────────────┴──────────────────────┤
#   │         Collector.Web (Next.js)            │
#   │              :3000 HTTP                    │
#   └────────────────────────────────────────────┘
#
# Utilisation :
#   ./start.sh          — build + lance les 3 services + attache la session
#   ./start.sh --no-attach  — ne fait pas `tmux attach` à la fin (utile en CI)
#   ./start.sh --skip-build — ne rebuild pas (assume que bin/ est à jour)
#

set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
SESSION="collector"
WEB_DIR="$PROJECT_DIR/src/Collector.Web"

ATTACH=1
BUILD=1
for arg in "$@"; do
    case "$arg" in
        --no-attach) ATTACH=0 ;;
        --skip-build) BUILD=0 ;;
        -h|--help)
            sed -n '2,20p' "$0" | sed 's/^# *//'
            exit 0
            ;;
        *) echo "unknown flag: $arg" >&2; exit 1 ;;
    esac
done

# ── Vérifications ──────────────────────────────────────────
command -v dotnet >/dev/null 2>&1 || { echo "✗ dotnet manquant" >&2; exit 1; }
command -v tmux   >/dev/null 2>&1 || { echo "✗ tmux manquant"   >&2; exit 1; }
command -v pnpm   >/dev/null 2>&1 || { echo "✗ pnpm manquant (needed for Collector.Web)" >&2; exit 1; }

if [ ! -f "$WEB_DIR/package.json" ]; then
    echo "✗ src/Collector.Web introuvable — le frontend n'est pas installé" >&2
    exit 1
fi

if [ ! -d "$WEB_DIR/node_modules" ]; then
    echo "↻ Installation des dépendances frontend (pnpm install)…"
    (cd "$WEB_DIR" && pnpm install --prefer-offline)
fi

if [ ! -f "$WEB_DIR/.env.local" ]; then
    echo "⚠  $WEB_DIR/.env.local absent — le frontend ne pourra pas joindre l'API." >&2
    echo "   Copie .env.local.example et renseigne API_INTERNAL_KEY + SESSION_SECRET." >&2
fi

# ── Build .NET solution (optionnel) ────────────────────────
if [ "$BUILD" -eq 1 ]; then
    echo "↻ dotnet build…"
    (cd "$PROJECT_DIR" && dotnet build Collector.sln -v minimal | tail -5)
fi

# ── (Re)création de la session tmux ────────────────────────
tmux kill-session -t "$SESSION" 2>/dev/null || true

# Pane 0 : Collector main (top-left une fois les splits terminés)
tmux new-session -d -s "$SESSION" -c "$PROJECT_DIR" \
    "dotnet run --project src/Collector; exec bash"

# Split vertical → crée un pane en dessous, full width : Collector.Web
tmux split-window -v -l 40% -t "$SESSION:0.0" -c "$WEB_DIR" \
    "NODE_TLS_REJECT_UNAUTHORIZED=0 pnpm start; exec bash"

# Split horizontal du pane collector (index 0) → pane api à droite.
# Note : tmux renumérote après ce split. L'ordre final devient :
#   0 = collector (top-left), 1 = api (top-right), 2 = web (bottom).
tmux split-window -h -t "$SESSION:0.0" -c "$PROJECT_DIR" \
    "dotnet run --project src/Collector.Api; exec bash"

# Titres posés en dernier, après toutes les renumérations.
tmux select-pane -t "$SESSION:0.0" -T "collector"
tmux select-pane -t "$SESSION:0.1" -T "api"
tmux select-pane -t "$SESSION:0.2" -T "web"
tmux select-pane -t "$SESSION:0.0"

# Active la bordure de titre (affiche "collector" / "api" / "web" en haut)
tmux set-option -t "$SESSION" pane-border-status top 2>/dev/null || true
tmux set-option -t "$SESSION" pane-border-format " #T " 2>/dev/null || true

echo
echo "✓ Session tmux '$SESSION' créée avec 3 panes :"
echo "    • collector  (src/Collector)"
echo "    • api        (src/Collector.Api → https://localhost:5001)"
echo "    • web        (src/Collector.Web → http://localhost:3000)"
echo
echo "Commandes utiles :"
echo "    tmux attach -t $SESSION          # (re)voir la session"
echo "    Ctrl-B puis D                    # détacher sans arrêter"
echo "    Ctrl-B puis flèches              # changer de pane"
echo "    ./stop.sh                        # arrêter les 3 services"
echo

if [ "$ATTACH" -eq 1 ]; then
    tmux attach -t "$SESSION"
fi
