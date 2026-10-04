#!/usr/bin/env bash
# Crée la clé bot:read du compte liberastra-bot et écrit les réglages TrackerApi__* dans le
# .env du bot Liberastra sur panda. Sert à la première mise en place et au renouvellement
# annuel (la clé expire au bout de 364 jours). La clé ne s'affiche jamais : elle passe du
# VPS à panda par ce poste, dans une variable.
#
# Prérequis :
#   - le compte liberastra-bot (non administrateur) existe dans l'administration du site ;
#   - accès ssh par clé à serv_ovh (sudo sans mot de passe) et à panda.
#
# Usage (Git Bash, depuis la racine du dépôt) :
#   deploy/create-bot-key.sh
# Variables facultatives : VPS, BOT_HOST, BOT_DIR, ACCOUNT, BASE_URL, DAYS.
#
# Ensuite, redémarrer le bot pour qu'il lise les réglages : ssh panda systemctl restart liberastra
set -euo pipefail

VPS=${VPS:-serv_ovh}
BOT_HOST=${BOT_HOST:-panda}
BOT_DIR=${BOT_DIR:-/root/discord/Liberastra-Bot-Discord}
ACCOUNT=${ACCOUNT:-liberastra-bot}
BASE_URL=${BASE_URL:-https://141.95.51.193/bot-api/}
DAYS=${DAYS:-364}

echo "→ Vérification du .env du bot sur $BOT_HOST…"
existing=$(ssh "$BOT_HOST" "[ -f '$BOT_DIR/.env' ] || { echo absent; exit 0; }; grep -c '^TrackerApi__' '$BOT_DIR/.env' || true")
if [ "$existing" = absent ]; then
  echo "ÉCHEC : $BOT_DIR/.env introuvable sur $BOT_HOST." >&2
  exit 1
fi
if [ "$existing" != 0 ]; then
  echo "Des réglages TrackerApi existent déjà dans le .env ($existing lignes)."
  read -r -p "Les remplacer par une nouvelle clé (renouvellement) ? [o/N] " answer
  case "$answer" in
    o|O|oui|OUI) ;;
    *) echo "Rien n'a été modifié."; exit 0 ;;
  esac
fi

echo "→ Création de la clé bot:read de $ACCOUNT sur $VPS (valable $DAYS jours)…"
settings=$(ssh "$VPS" 'bash -s' -- "$ACCOUNT" "$DAYS" "$BASE_URL" <<'REMOTE'
set -euo pipefail
account=$1; days=$2; base_url=$3
key=$(sudo sed -n 's/^COLLECTOR_API_Api__AdminApiKey=//p' /etc/sc-tracker/api.env)
[ -n "$key" ] || { echo "clé d'administration introuvable dans /etc/sc-tracker/api.env" >&2; exit 1; }
api=http://127.0.0.1:5000/api/admin
id=$(curl -fsS -H "x-api-key: $key" "$api/users?pageSize=200" \
  | jq -r --arg u "$account" '.items[] | select(.username == $u) | .id')
[ -n "$id" ] || { echo "compte $account introuvable : le créer d'abord dans l'administration du site" >&2; exit 1; }
expires=$(date -u -d "+$days days" +%Y-%m-%dT00:00:00Z)
raw=$(curl -fsS -H "x-api-key: $key" -H "Content-Type: application/json" \
  -d "{\"name\":\"$account\",\"expiresAt\":\"$expires\",\"scope\":\"bot:read\"}" \
  "$api/users/$id/api-keys" | jq -r '.rawKey')
[ -n "$raw" ] && [ "$raw" != null ] || { echo "la création de la clé n'a pas renvoyé de clé" >&2; exit 1; }
fingerprint=$(openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256 | sed 's/.*Fingerprint=//')
printf 'TrackerApi__BaseUrl=%s\nTrackerApi__ApiKey=%s\nTrackerApi__CertSha256=%s\n' "$base_url" "$raw" "$fingerprint"
echo "clé créée pour le compte $id, expire le $expires" >&2
REMOTE
)

echo "→ Écriture des réglages dans le .env sur $BOT_HOST (copie : .env.bak-tracker)…"
printf '%s\n' "$settings" | ssh "$BOT_HOST" "set -e; cd '$BOT_DIR'
  cp .env .env.bak-tracker
  sed -i '/^TrackerApi__/d' .env
  [ -z \"\$(tail -c1 .env)\" ] || echo >> .env
  cat >> .env
  echo \"OK : \$(grep -c '^TrackerApi__' .env) réglages TrackerApi dans le .env\""
unset settings

echo "Terminé. Redémarrer le bot pour qu'il les lise : ssh $BOT_HOST systemctl restart liberastra"
