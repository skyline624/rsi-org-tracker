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
git push vps <branche>                      # depuis le poste de dev
ssh serv_ovh
~/sc-tracker/current/deploy/deploy.sh <sha>               # API + web
~/sc-tracker/current/deploy/deploy.sh <sha> --collector   # + collector (migrations tracker.db)
```

Le script publie l'API et le collector en Release, construit le front en mode
`standalone`, bascule `current`, redémarre les services et vérifie
`/api/health/ready` et `/login`. En cas d'échec, il revient automatiquement à la
release précédente. Les builds tournent dans un scope limité à 1,6 Go de RAM
(`SC_BUILD_MEMORY`) pour ne jamais réveiller l'OOM killer. Les 5 dernières
releases sont conservées.

Redémarrer le collector interrompt la collecte des membres en cours : ne passer
`--collector` que si le collector ou le schéma de tracker.db a changé.

## Revenir en arrière

```bash
~/sc-tracker/current/deploy/rollback.sh                  # vers `previous`
~/sc-tracker/current/deploy/rollback.sh --collector <dossier de release>
```

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

## Tests

`deploy/tests/test-rollback.sh` vérifie la bascule et le retour arrière avec
`sudo`, `systemctl` et `curl` simulés (Linux uniquement, lancé par la CI).
