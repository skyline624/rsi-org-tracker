#!/bin/bash

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
SESSION="collector"

# Arrêter la session existante si elle tourne
tmux kill-session -t "$SESSION" 2>/dev/null

# Créer la session avec le premier panneau : collecte
tmux new-session -d -s "$SESSION" -c "$PROJECT_DIR" \
    "dotnet run --project src/Collector; exec bash"

# Diviser horizontalement et lancer l'API
tmux split-window -h -t "$SESSION" -c "$PROJECT_DIR" \
    "dotnet run --project src/Collector.Api; exec bash"

# Attacher la session
tmux attach -t "$SESSION"
