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

## Tests

`deploy/tests/test-rollback.sh` vérifie la bascule et le retour arrière avec
`sudo`, `systemctl` et `curl` simulés (Linux uniquement, lancé par la CI).
