# Déploiement

Production : un VPS, trois services systemd (`sc-api`, `sc-collector`, `sc-web`)
derrière nginx. Chaque déploiement construit une **release** immuable et bascule un
lien symbolique ; revenir en arrière consiste à rebasculer ce lien.

```
/home/ubuntu/collector-dotnet        dépôt git qui reçoit les pushes (remote « vps »)
/home/ubuntu/collector-dotnet/bin/data  données : tracker.db, api.db, logs, audio (ne bougent jamais)
/home/ubuntu/sc-tracker/releases/<sha12>/{api,collector,web,deploy,COMMIT}
/home/ubuntu/sc-tracker/current  -> release active
/home/ubuntu/sc-tracker/previous -> release précédente
/etc/sc-tracker/{api,web,collector}.env   secrets et réglages (root:ubuntu 0640)
```

## Déployer

```bash
git push vps main:release                   # depuis le poste de dev
ssh serv_ovh
~/sc-tracker/current/deploy/deploy.sh <sha>               # API + web
~/sc-tracker/current/deploy/deploy.sh <sha> --collector   # + collector (migrations tracker.db)
```

`main` est la branche extraite du dépôt du serveur (ancienne installation) : git refuse
de la mettre à jour par un push, d'où la branche `release`. Le script ne se sert que du sha.

Le script publie l'API et le collector en Release, construit le front en mode
`standalone`, bascule `current`, redémarre les services et vérifie
`/api/health/ready` et `/login`. En cas d'échec, il revient automatiquement à la
release précédente. Les builds tournent dans un scope limité à 1,6 Go de RAM
(`SC_BUILD_MEMORY`) pour ne jamais réveiller l'OOM killer. Les 5 dernières
releases sont conservées.

Redémarrer le collector interrompt la collecte des membres en cours : ne passer
`--collector` que si le collector ou le schéma de tracker.db a changé.

Avec `--collector`, le collector redémarre **en premier** : son `ExecStartPre`
(`Collector.dll --migrate`) applique les migrations de tracker.db, et
`systemctl restart` ne rend la main qu'une fois la base migrée (ou échoue avec la
migration, ce qui déclenche le retour arrière). L'API et le web redémarrent
ensuite. L'ancien code sait lire un schéma migré (les migrations sont additives),
le nouveau code ne sait pas lire l'ancien : tant qu'une migration est en attente,
`/api/health/ready` répond 503, donc une release déployée sans `--collector` alors
qu'elle en avait besoin est annulée automatiquement. Une release plus ancienne que ce
mode (sans `migrate-mode.txt` à côté de `Collector.dll`, par exemple lors d'un retour
arrière) saute l'`ExecStartPre` et migre au démarrage, comme avant.

Ce même `--migrate` décode les noms d'organisation stockés encodés en HTML par l'ancien
parser (« Les H&amp;eacute;raults 34 », environ 35 000 lignes sur la copie du 24/09,
24 s) avant la première phase 1 de la nouvelle version : seul l'encodage change, aucun
événement n'est écrit, et les démarrages suivants n'en trouvent plus. La commande
manuelle `--maintenance repair-org-names [--dry-run]` fait la même chose.

La version 2 du parser de profil ajoute `users.ParserVersion` (toutes les fiches existantes
passent à 1) : une fois la file de la phase 4 redescendue sous son seuil, le collector
relit ces fiches au rythme de `Collector__ProfileRefreshPerHour` (3 600 par heure par
défaut, soit la moitié du budget RSI ; 0 pour arrêter). Sur la copie du 24/09 : 583 351
fiches, dont 62 % sans nom affiché et 84 % sans date d'enrôlement. Le contrôle
informatif `profiles-to-read-again` de `verify` donne le nombre restant.

Après un déploiement, `Collector.dll --maintenance verify --since <heure du déploiement UTC>`
(lecture seule, collector en marche) contrôle la cohérence de ce que la nouvelle version
écrit, et `--integrity-check --sample 20 --since <même heure>` compare un échantillon
avec RSI (une centaine de requêtes).

## Revenir en arrière

```bash
~/sc-tracker/current/deploy/rollback.sh                  # vers `previous`
~/sc-tracker/current/deploy/rollback.sh --collector <dossier de release>
```

Le retour arrière ne touche jamais au schéma : l'ancienne release lit sans problème une
base migrée, puisque les migrations ne font qu'ajouter des colonnes, des tables et des
index. Ne jamais lancer `dotnet ef database update <migration précédente>` sur la
production : les méthodes `Down` suppriment des colonnes, ce qu'EF fait en
reconstruisant les tables (`ef_temp_*`), soit des heures sur 25 Go. Pour revenir à
l'état d'avant une migration, la seule voie est la sauvegarde.

## Migration initiale (une seule fois)

Passage de l'ancienne installation (binaires Debug dans `collector-dotnet/bin`,
front servi par `next start` depuis les sources) à la structure par releases.

1. Créer `/etc/sc-tracker/` à partir de `deploy/env/*.env.example` :
   - `api.env` : reprendre `ASPNETCORE_*` de l'unité actuelle et le jeton Discord
     du drop-in `sc-api.service.d/discord.conf` ;
   - `web.env` : reprendre `API_BASE_URL` et `NODE_TLS_REJECT_UNAUTHORIZED` de
     `src/Collector.Web/.env.local`, générer `NEXT_SERVER_ACTIONS_ENCRYPTION_KEY` ;
   - `sudo chown root:ubuntu /etc/sc-tracker/*.env && sudo chmod 0640 /etc/sc-tracker/*.env`.
2. Installer les unités : sauvegarder les actuelles (`*.service.bak`), copier
   `deploy/systemd/*.service` dans `/etc/systemd/system/`, supprimer le drop-in
   `discord.conf`, `sudo systemctl daemon-reload` (rien ne redémarre à ce stade).
3. Premier déploiement : `current` n'existant pas encore, lancer le script depuis
   un worktree temporaire :
   `git -C ~/collector-dotnet worktree add --detach /tmp/sc-deploy <sha>`
   puis `/tmp/sc-deploy/deploy/deploy.sh <sha> --collector`
   et enfin `git -C ~/collector-dotnet worktree remove /tmp/sc-deploy`.
4. Vérifier : `readlink ~/sc-tracker/current`, `curl -I http://127.0.0.1:3000/login`,
   `sudo lsof ~/collector-dotnet/bin/data/tracker.db` (les trois services ouvrent la même base).
5. Exercice de retour arrière au deuxième déploiement : `rollback.sh`, puis retour.

Retour à l'ancienne installation : restaurer les `*.service.bak`, `daemon-reload`,
redémarrer les trois services (les anciens binaires et `.next` n'ont pas été touchés).

## Durcissement du serveur (lot 2)

À appliquer **avant** de déployer le lot 6 : le front et l'API font confiance à la
première adresse de `X-Forwarded-For`, que seul le nouveau site nginx écrase.

Garder une session SSH ouverte pendant toute l'opération (et vérifier l'accès de
secours : Tailscale ou console KVM OVH).

1. **nginx** : `sudo cp deploy/nginx/sc-tracker.conf /etc/nginx/sites-available/sc-tracker`,
   `sudo nginx -t && sudo systemctl reload nginx`.
   Retour : l'ancienne version est dans l'historique git (`git show <sha>:deploy/nginx/sc-tracker.conf`).
2. **SSH** : `sudo cp deploy/ssh/00-hardening.conf /etc/ssh/sshd_config.d/`,
   `sudo sshd -t && sudo systemctl reload ssh`, puis ouvrir une **nouvelle** connexion
   par clé avant de fermer l'ancienne. `ssh -o PubkeyAuthentication=no serv_ovh` doit
   répondre « Permission denied (publickey) ». Retour : supprimer le fichier et recharger.
3. **Port 5173 (phonurgia)** : Docker contourne ufw. Dans `~/phonurgia`, publier le port
   sur la boucle locale (`"127.0.0.1:5173:80"`, ou l'IP Tailscale si l'accès passe par là),
   puis `docker compose up -d`.
4. **Pare-feu** : `bash deploy/ufw.sh`. Retour : `sudo ufw disable`.
5. **fail2ban** : `sudo apt-get install -y fail2ban`,
   `sudo cp deploy/fail2ban/sshd.local /etc/fail2ban/jail.d/`, `sudo systemctl restart fail2ban`,
   `sudo fail2ban-client status sshd`.
6. **Vérification** depuis l'extérieur : `nmap -Pn -p 22,80,443,3000,5000,5001,5173 <IP>` —
   seuls 22, 80 et 443 ouverts ; `curl -skI https://<IP>/ | grep -i x-content-type`.

## Maintenance de la base (lot 11)

À faire seulement quand les lots 9 et 10 tournent en production depuis au moins un
cycle complet : l'ancien code recréerait aussitôt les lignes purgées. Supprimer des
lignes est irréversible ; seule la sauvegarde permet de revenir en arrière.

1. **Sauvegarde vérifiée** (méthode du 24/09) : collector arrêté,
   `PRAGMA wal_checkpoint(TRUNCATE)`, copie en flux `pigz` vers le poste local, contrôle sha256.
2. `sudo systemctl stop sc-collector` (l'API peut rester en marche, elle ne fait que lire).
3. Commande, avec l'environnement du service :

   ```bash
   cd /home/ubuntu/sc-tracker/current/collector
   set -a; . /etc/sc-tracker/collector.env; set +a
   M="/home/ubuntu/.dotnet/dotnet Collector.dll --maintenance"
   $M measure                                   # ce que chaque purge supprimerait
   $M purge content-null-events --dry-run       # puis sans --dry-run
   $M purge member-count-repeats
   $M purge queue-enriched
   $M purge queue-terminal
   $M check                                     # doit afficher « quick_check: ok »
   $M measure                                   # comptes après, et pages libres
   ```

   Répétition sur la copie du 24/09 : 1,15 M événements en 31 s, 64 k en 5 s,
   5,3 M lignes de file en 6 min, 116 k en 8 s ; `check` 82 s.
   Chaque purge s'arrête d'elle-même si le WAL reste au-dessus de 1 Gio après un
   checkpoint, ou s'il reste moins de 3 Gio de disque : relancer la même commande
   reprend là où elle s'est arrêtée.
4. `sudo systemctl start sc-collector`.

Pas de VACUUM : les pages libérées vont dans la freelist (ligne « freelist pages » de
`measure`) et la base les réutilise en grossissant. Les événements `rank_changed` et
`roles_changed` du parser v1 et les départs en rafale des rosters tronqués sont
seulement mesurés.

## Rosters Discord (plugin Vencord, lot 14)

Le plugin Vencord envoie les membres d'un serveur Discord à une seule route publique,
`/ingest/discord/`, que nginx relaie vers `/api/ingest/discord/`. Seules les clés
`discord:ingest` y sont acceptées, et ces clés ne le sont nulle part ailleurs. C'est la
première route de l'API ouverte sur Internet : n'ajouter la location sur le serveur
qu'une fois le lot A livré **en entier** (clés limitées et leur schéma
d'authentification, verrou et limites de l'ingestion, effacements).

1. **Déployer** avec `--collector` :
   `~/sc-tracker/current/deploy/deploy.sh <sha> --collector`. La migration
   `AddDiscordRosters` crée les tables `discord_*` et trois index sur des tables
   existantes (`entity_links`, `users`, `user_handle_history`) : chronométrer d'abord
   `Collector.dll --migrate` sur une copie de la base. La colonne `Scope` d'`api.db`
   (migration `AddApiKeyScope`) s'ajoute au démarrage de l'API.
2. **Empreinte du certificat** :

   ```bash
   openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256
   ```

   La commande affiche `sha256 Fingerprint=AB:CD:…`. Garder la partie après
   `Fingerprint=` : une valeur avec une espace casserait un `set -a; . api.env`. Le plugin
   accepte aussi la ligne entière.
3. **Réglages de l'API**, dans `/etc/sc-tracker/api.env` :

   ```bash
   COLLECTOR_API_Discord__Ingest__PublicUrl=https://<IP>
   COLLECTOR_API_Discord__Ingest__CertificateSha256=AB:CD:…
   ```

   puis `sudo systemctl restart sc-api`. Le panneau Paramètres → Clé d'envoi Discord
   affiche alors l'URL et l'empreinte, chacune avec un bouton copier ; tant que l'une
   manque, il renvoie vers l'administrateur. Réglages facultatifs, commentés dans
   `deploy/env/api.env.example` : `COLLECTOR_API_Api__RateLimit__DiscordIngest__*` (20
   envois par 600 s et par émetteur) et `COLLECTOR_API_Discord__Retention__*` (journal des
   envois 365 jours, comptes non liés partis 730 jours).
4. **nginx**, à la main comme au lot 2 : la version du dépôt ajoute les zones
   `sc_discord_ingest` et `sc_discord_conn`, et la location `/ingest/discord/` avant
   `location /`.

   ```bash
   sudo cp ~/sc-tracker/current/deploy/nginx/sc-tracker.conf /etc/nginx/sites-available/sc-tracker
   sudo nginx -t && sudo systemctl reload nginx
   ```

   Retour : l'ancienne version est dans l'historique git
   (`git show <sha>:deploy/nginx/sc-tracker.conf`), même copie, puis `nginx -t` et `reload`.
5. **Vérification** depuis le poste local :

   ```bash
   U=https://<IP>/ingest/discord/guilds/123456789012345678/syncs
   curl -sk -o /dev/null -w '%{http_code}\n' -X POST "$U"   # 401 : aucune clé
   curl -sk -o /dev/null -w '%{http_code}\n' "$U"           # 403 : POST seulement
   ```

**Certificat régénéré** : recalculer l'empreinte (étape 2), remplacer
`COLLECTOR_API_Discord__Ingest__CertificateSha256` dans `api.env`, puis
`sudo systemctl restart sc-api`. Tant qu'un émetteur n'a pas recopié la nouvelle empreinte
depuis le panneau dans son plugin, ses envois échouent sans rien transmettre
(« Certificat inattendu »). C'est voulu.

**Effacements administratifs** : ces opérations restent réservées à l'API, sans
contrôle dans l'interface. Un admin appelle les routes depuis le VPS, en boucle
locale, avec la clé admin statique (réponse attendue : 204) :

```bash
KEY=$(sudo sed -n 's/^COLLECTOR_API_Api__AdminApiKey=//p' /etc/sc-tracker/api.env)
A=http://127.0.0.1:5000/api/discord
curl -s -o /dev/null -w '%{http_code}\n' -X DELETE -H "x-api-key: $KEY" "$A/accounts/<id du compte>"                # effacer et exclure un compte
curl -s -o /dev/null -w '%{http_code}\n' -X DELETE -H "x-api-key: $KEY" "$A/guilds/<id du serveur>?exclude=true"     # supprimer et exclure un serveur
```

Un envoi complet enregistre aussi les départs massifs ; le tracker les signale
sans demander d'autorisation supplémentaire. Un envoi partiel ne produit aucun départ.

## Bot Discord Liberastra (`/bot-api/`)

Le bot Liberastra (serveur `panda`, 185.146.193.199) lit le tracker par `/bot-api/`, réservé à
cette IP par nginx, avec une clé à portée `bot:read` (valable sur `/api/bot/` seulement,
365 jours au plus). Mise en place :

1. Compte dédié non administrateur `liberastra-bot` (mot de passe aléatoire, jamais utilisé).
2. Clé : depuis le poste local, `deploy/create-bot-key.sh` fait les étapes 2 et 3 d'un coup (clé,
   empreinte, écriture dans le `.env` de panda, sans afficher la clé), puis
   `ssh panda systemctl restart liberastra`. À la main : `POST /api/admin/users/<id>/api-keys` avec
   `{"name":"liberastra-bot","expiresAt":"<ISO 8601 date max. 365 jours devant>","scope":"bot:read"}`
   (clé d'administration de `api.env`, depuis le serveur). La clé n'est affichée qu'une fois :
   la copier directement dans `/root/discord/Liberastra-Bot-Discord/.env` de `panda`
   (`TrackerApi__ApiKey`).
3. Empreinte du certificat pour `TrackerApi__CertSha256` :
   `openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256`.
   La commande affiche `sha256 Fingerprint=AB:CD:…` ; garder la partie après `Fingerprint=`
   (une valeur avec espace casserait un `set -a; . api.env`).
4. **Installation** : comparer d'abord la version du dépôt avec celle du serveur,
   `diff /etc/nginx/sites-available/sc-tracker ~/sc-tracker/current/deploy/nginx/sc-tracker.conf` :
   la copie écrase le fichier en place, et toute retouche faite à la main sur le serveur (absente
   du dépôt) serait perdue — la reporter d'abord dans le dépôt. Puis copier la version du dépôt,
   `sudo cp ~/sc-tracker/current/deploy/nginx/sc-tracker.conf /etc/nginx/sites-available/sc-tracker`,
   et `sudo nginx -t && sudo systemctl reload nginx`.
   Vérifications (depuis le poste local) :
   - `curl -sk -o /dev/null -w '%{http_code}' https://<IP>/bot-api/search?q=ab` depuis une IP autre que panda : réponse 403.
   - `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5000/api/bot/search?q=ab` depuis le serveur : réponse 401 (absence de clé).
5. **Renouvellement** (avant l'expiration) : relancer `deploy/create-bot-key.sh` (il propose de
   remplacer les réglages existants), ou créer une nouvelle clé avec le même appel admin et la
   mettre dans le `.env` de panda (`TrackerApi__ApiKey`), puis `systemctl restart liberastra`.
   L'ancienne clé reste valide jusqu'à son expiration (aucune route ne révoque une clé d'un autre compte).
6. **Urgence** (clé compromise) : interdire le compte par `PUT /api/admin/users/<id>` avec
   `{"isBanned":true}` (clé d'administration de `api.env`), ce qui refuse toutes ses clés immédiatement.
   Puis effacer le compte : `DELETE /api/admin/users/<id>`, recréer `liberastra-bot` et sa clé, mettre
   à jour le `.env` et redémarrer le bot (tous les appels avec la clé administrateur, depuis le serveur).
   La suppression du compte efface toutes ses clés.
7. Si l'IP de `panda` change, mettre à jour `allow` dans le bloc `/bot-api/`.

## Tests

`deploy/tests/test-rollback.sh` vérifie la bascule et le retour arrière avec
`sudo`, `systemctl` et `curl` simulés (Linux uniquement, lancé par la CI).
