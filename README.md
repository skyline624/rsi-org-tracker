# SC Org Tracker

Suivi des organisations Star Citizen : ~100 000 organisations, leurs membres, les
arrivées et départs, les changements de rang, de nom et de contenu. Le site est
**privé** : toute page demande un compte, créé par un administrateur.

## Architecture

```
Internet ──HTTPS──> nginx ──> Collector.Web (Next.js, 127.0.0.1:3000)
                                   │  appels serveur avec le JWT de l'utilisateur
                                   v
                              Collector.Api (ASP.NET Core, 127.0.0.1:5000)
                                   │                      │
                               tracker.db  <────────  Collector (service .NET)
                               api.db                     │
                                                          v
                                               robertsspaceindustries.com
```

Trois services systemd (`sc-web`, `sc-api`, `sc-collector`) sur un VPS, derrière nginx.
Deux bases SQLite en WAL dans le dossier de données (`COLLECTOR_DATA_DIR`) :

- `tracker.db` (≈ 25 Go) : organisations, membres, citoyens, événements, file
  d'enrichissement. Écrite par le collector, qui applique ses migrations EF au démarrage ;
  l'API y lit et y écrit les annotations (notes, adhésions manuelles, audio, liens).
- `api.db` : comptes, jetons de rafraîchissement, clés API, journal d'activité (API seule).

### Collector

Un cycle enchaîne trois phases, chacune dans sa propre portée DI (le `DbContext` est
libéré à la fin de la phase), et la phase 4 tourne en parallèle :

1. **Découverte** (`orgs/getOrgs`) : nouvelles organisations, et un instantané de
   l'annonce seulement quand elle a changé.
2. **Contenu** (page de l'organisation) : description, histoire, manifeste, charte,
   comparés au dernier contenu connu ; `ContentCheckedAt` date chaque lecture.
3. **Membres** (`orgs/getOrgMembers`), organisations vivantes les moins récemment lues
   d'abord : le roster n'est écrit que s'il a été lu en entier (au moins 98 % de
   `totalrows`). RSI ne sert que 400 pages (12 800 lignes) : au-delà, la fenêtre lisible
   est écrite et les autres membres gardent leur dernier état, sans départ déduit. Les
   lignes masquées (R, H) sont comptées dans `org_member_counts`.
4. **Enrichissement** (`Phase4Worker`, profils `/citizens/{handle}`) : file avec issue
   finale (enrichi, disparu, abandonné), profil « n/a » revu à 14 jours, échec revu
   après 1 h puis 4 h.

Toutes les requêtes vers RSI passent par un budget commun (`RsiRateGate`) : espacement,
concurrence maximale, pause partagée sur 403/429/503/`ErrApiThrottled`.

### Sécurité

- Jetons d'accès RS256 signés par l'API (clé privée hors dépôt) ; le front vérifie la
  signature avec la clé publique (JWKS) dans son middleware et renouvelle la session
  avec le jeton de rafraîchissement (rotation, détection de réutilisation).
- Cookies `httpOnly`, `SameSite=Lax` ; le navigateur ne voit jamais de jeton ni l'API.
- API en HTTP sur la boucle locale uniquement ; nginx termine TLS et écrase
  `X-Forwarded-For`. Rate limit par utilisateur, par IP et sur le login.
- CSP avec nonce, en mode Report-Only pour l'instant.

## Dépôt

```
src/Collector           service de collecte (.NET 10, EF Core 8, SQLite)
src/Collector.Api       API (ASP.NET Core)
src/Collector.Web       front (Next.js 15, App Router)
src/Collector.Tests     tests du collector (xUnit, captures RSI anonymisées)
src/Collector.Api.Tests tests de l'API (WebApplicationFactory)
deploy/                 déploiement par releases, unités systemd, nginx, durcissement
scripts/                scripts de développement local
tools/fixtures/         anonymisation des captures RSI
```

## Développement

Prérequis : SDK .NET 10 (`global.json`), Node 22 avec corepack (pnpm 10.27), tmux pour
le lanceur.

```bash
scripts/dev-start.sh        # build, puis collector + API + web dans une session tmux
scripts/dev-stop.sh         # arrête cette session
```

Les deux scripts refusent de tourner là où les services de production sont actifs.
Le front lit `src/Collector.Web/.env.local` (modèle : `.env.local.example`).

Le collector tourne en environnement Production par défaut (l'API, lancée par
`dotnet run`, prend le profil Development de son `launchSettings.json`). La Production
exige un `tracker.db` existant dans le dossier de données (`bin/data` sans `COLLECTOR_DATA_DIR`) :
pointer `COLLECTOR_DATA_DIR` sur une copie de la base, ou créer un fichier `tracker.db`
vide pour en démarrer une nouvelle. `DOTNET_ENVIRONMENT=Development` lève cette exigence
mais applique `appsettings.Development.json` : un cycle relancé toutes les 5 min au lieu
d'une heure, 2 requêtes RSI simultanées au plus au lieu de 5, logs en Debug.

Modes ponctuels du collector (`dotnet run --project src/Collector -- <mode>`) :

| Mode | Effet |
|---|---|
| `--single-run [--skip-phase2]` | un cycle (phases 1 à 3), puis sortie |
| `--integrity-check [--sample N]` | compare N organisations stockées avec RSI |
| `--backfill-enrichment-queue` | met en file les membres jamais identifiés |
| `--repair-corrupted-handles` | répare les handles mal lus par l'ancien parser |
| `--maintenance measure \| check \| purge <cible> [--dry-run] [--batch N]` | mesures, `quick_check`, purges par lots (voir `deploy/README.md`) |

### Tests

```bash
dotnet test Collector.sln                       # collector + API
cd src/Collector.Web && corepack pnpm typecheck && corepack pnpm test && corepack pnpm build
```

Smoke test de bout en bout (Playwright), contre un site qui tourne :

```bash
cd src/Collector.Web
E2E_BASE_URL=https://<site> E2E_USERNAME=<compte> E2E_PASSWORD=<mot de passe> corepack pnpm test:e2e
```

Sans `E2E_BASE_URL`, il lance `pnpm dev` ; sans compte, seuls les contrôles anonymes
tournent. Il ne fait que lire.

### Captures RSI

Les tests des parsers utilisent de vraies réponses RSI, anonymisées :

```bash
node tools/fixtures/anonymize.mjs members <brut.json> src/Collector.Tests/Fixtures/rsi/<nom>.json
node tools/fixtures/anonymize.mjs profile <brut.html> <sortie.html> <handle> [nom affiché]
```

Capturer depuis un poste de développement, à faible cadence ; ne jamais committer
les captures brutes.

## CI

`.github/workflows/ci.yml`, à chaque push et pull request :

- **dotnet** : restauration verrouillée, build Release, tests, absence de migration EF
  manquante pour les deux contextes ;
- **web** : installation figée, typecheck, vitest, build, démarrage du serveur standalone ;
- **deploy-scripts** : syntaxe des scripts, exercice de retour arrière, garde des
  scripts de développement.

## Déploiement et retour arrière

Voir [`deploy/README.md`](deploy/README.md) : chaque déploiement construit une release
immuable et bascule un lien symbolique ; revenir en arrière rebascule le lien. Le
collector applique les migrations de `tracker.db` : le déployer avant l'API.

Les migrations de `tracker.db` n'ajoutent que des colonnes (nullables ou avec défaut),
des tables et des index : jamais de reconstruction de table (`ef_temp_*`), qui
prendrait des heures sur 25 Go. Chronométrer d'abord sur une copie.

## Sauvegarde manuelle

À faire avant toute opération destructive (purge, migration délicate). Depuis le poste
local :

```bash
ssh serv_ovh 'sudo systemctl stop sc-collector'
ssh serv_ovh 'cd ~/collector-dotnet/bin/data && sqlite3 tracker.db "PRAGMA busy_timeout=30000; PRAGMA wal_checkpoint(TRUNCATE);"'

# Une transaction de lecture ouverte empêche tout checkpoint de réécrire le fichier
# pendant la copie (l'API peut continuer à écrire dans le WAL). setsid en fait un
# groupe de processus à part : le PID noté est celui du groupe (sh, sqlite3, sleep).
ssh serv_ovh 'cd ~/collector-dotnet/bin/data && nohup setsid sh -c "(echo \"BEGIN; SELECT count(*) FROM sqlite_master;\"; sleep 7200) | sqlite3 tracker.db" >/dev/null 2>&1 & echo $! > /tmp/sc-backup-holder.pid'

ssh serv_ovh 'cd ~/collector-dotnet/bin/data && cat tracker.db | tee >(sha256sum > /tmp/tracker.db.sha256) | pigz -6 -p 3' \
  | gunzip > backup/tracker.db
sha256sum backup/tracker.db; ssh serv_ovh cat /tmp/tracker.db.sha256   # les deux doivent être identiques
scp serv_ovh:collector-dotnet/bin/data/api.db backup/

# "-" devant le PID : tout le groupe. Tuer le seul sh laisserait sqlite3 tenir sa
# transaction, et le WAL grossirait jusqu'à 2 h après le redémarrage du collector.
ssh serv_ovh 'kill -- -$(cat /tmp/sc-backup-holder.pid); sudo systemctl start sc-collector'
ssh serv_ovh 'pgrep -a sqlite3 || echo "plus de porteur de verrou"'
```

Compter environ 1 h pour 25 Go selon le débit. Tuer le porteur de verrou par son
groupe enregistré, jamais par motif (`pkill -f` peut viser la session SSH elle-même).

## Variables d'environnement

Fichiers `/etc/sc-tracker/{api,web,collector}.env` (modèles dans `deploy/env/`).

| Service | Variable | Rôle |
|---|---|---|
| tous (.NET) | `COLLECTOR_DATA_DIR` | dossier de `tracker.db`, `api.db`, `logs/`, `audio/` ; en Production, le collector et l'API refusent de démarrer s'il ne contient pas `tracker.db` (jamais de base vide créée par erreur) |
| API | `ASPNETCORE_URLS` | `http://127.0.0.1:5000` |
| API | `ASPNETCORE_ENVIRONMENT` | `Production` (pas de Swagger ni de détail d'erreur) |
| API | `COLLECTOR_API_Api__Jwt__PrivateKeyPath` | clé RSA ≥ 2048 bits, mode 0600 |
| API | `COLLECTOR_API_Api__AdminApiKey` | clé d'administration pour les scripts (≥ 24 caractères) |
| API | `COLLECTOR_API_Api__RateLimit__*` | limites par utilisateur, par IP et du login |
| API | `Discord__BotToken` | intégration Discord |
| collector | `Collector__*` | réglages de `appsettings.json` (`RateLimitDelaySeconds`, `MaxConcurrentRequests`, `ThrottlePauseSeconds`…) |
| web | `API_BASE_URL` | `http://127.0.0.1:5000` |
| web | `HOSTNAME`, `PORT` | écoute du serveur Next (`127.0.0.1:3000`) |
| web | `NEXT_SERVER_ACTIONS_ENCRYPTION_KEY` | stable d'un build à l'autre |

## Journaux

- collector : `$COLLECTOR_DATA_DIR/logs/collector-<date>.log` et journald (`sc-collector`) ;
- API et web : journald (`journalctl -u sc-api`, `-u sc-web`).
