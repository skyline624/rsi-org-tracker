# Runs ON THE VPS, fed on stdin by deploy/create-bot-key.bat:
#   ssh serv_ovh "tr -d '\r' | bash -s -- <account> <days> <base url>" < create-bot-key.remote.sh
# Creates the account's bot:read key through the admin route and prints the three
# TrackerApi__* settings on stdout (piped straight to panda, never displayed). Messages go
# to stderr.
set -euo pipefail
account=$1; days=$2; base_url=$3

key=$(sudo sed -n 's/^COLLECTOR_API_Api__AdminApiKey=//p' /etc/sc-tracker/api.env)
[ -n "$key" ] || { echo "ECHEC : cle d'administration introuvable dans /etc/sc-tracker/api.env" >&2; exit 1; }
api=http://127.0.0.1:5000/api/admin

id=$(curl -fsS -H "x-api-key: $key" "$api/users?pageSize=200" \
  | jq -r --arg u "$account" '.items[] | select(.username == $u) | .id')
[ -n "$id" ] || { echo "ECHEC : compte $account introuvable, le creer d'abord sur le site (page Comptes)" >&2; exit 1; }

expires=$(date -u -d "+$days days" +%Y-%m-%dT00:00:00Z)
raw=$(curl -fsS -H "x-api-key: $key" -H "Content-Type: application/json" \
  -d "{\"name\":\"$account\",\"expiresAt\":\"$expires\",\"scope\":\"bot:read\"}" \
  "$api/users/$id/api-keys" | jq -r '.rawKey')
[ -n "$raw" ] && [ "$raw" != null ] || { echo "ECHEC : la creation de la cle n'a pas renvoye de cle" >&2; exit 1; }

fingerprint=$(openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256 | sed 's/.*Fingerprint=//')
printf 'TrackerApi__BaseUrl=%s\nTrackerApi__ApiKey=%s\nTrackerApi__CertSha256=%s\n' "$base_url" "$raw" "$fingerprint"
echo "Cle creee pour le compte $account (id $id), expire le $expires" >&2
