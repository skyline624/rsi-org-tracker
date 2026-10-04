# Commandes `/tracker` dans le bot Discord Liberastra — design

Date : 2026-10-03
Statut : design validé, spécification relue ; plan : `docs/superpowers/plans/2026-10-03-discord-tracker-commands.md`

## 1. Objectif

Permettre aux membres du serveur Discord de Liberastra de consulter les données du tracker
sans ouvrir le site, pour deux usages :

- **recrutement** : vérifier un candidat (orgs actuelles et passées, anciens pseudos, date
  d'inscription) ;
- **renseignement sur les orgs** : fiche, effectif, membres, arrivées et départs récents.

L'accès dépend du **grade** (rôle Discord) et se règle **par sous-commande**, depuis Discord,
par les administrateurs du serveur.

## 2. Décisions prises

| Sujet | Décision |
|---|---|
| Où vivent les commandes | Dans le bot Liberastra existant (serveur `panda`), pas dans le tracker |
| Accès aux données | Route HTTPS dédiée sur le VPS du tracker (`/bot-api/`), filtrée par l'IP de `panda`, clé d'API obligatoire |
| Clé | Nouvelle portée `bot:read` : lecture seule, valable uniquement sur `/api/bot/` ; le reste de l'API refuse toute clé à portée (règle existante) |
| TLS | Le bot vérifie l'empreinte SHA-256 du certificat (auto-signé) du VPS ; la vérification TLS n'est jamais désactivée |
| Commandes | Groupe `/tracker` : `joueur`, `historique`, `recherche`, `org`, `membres`, `mouvements` |
| Accès | Par sous-commande et par rôle, stocké dans la base du bot, réglé par `/tracker-acces` ; fermé par défaut ; la permission Discord Administrateur passe toujours |
| Réponses | Embeds privés (éphémères), en français |
| Confidentialité | Mêmes données que le site ; une affiliation masquée n'est jamais attribuée à un joueur ; aucune donnée du tracker n'est envoyée à Gemini |
| Traçabilité | Chaque requête est inscrite dans le journal d'activité du tracker (qui, quelle commande, quelle cible) et dans les journaux du bot |
| Déploiement du bot | Le code de `panda` est d'abord mis sur GitHub ; `panda` reçoit ensuite les envois directement (branche `release`), `deploy.sh` déploie cette branche |

## 3. Hors périmètre

- Recherche par compte Discord (`@membre`) via les liens Discord ↔ RSI du tracker : possible
  plus tard, pas demandé.
- Écriture depuis Discord (notes, membres manuels, liens) : la clé est en lecture seule.
- Affiliations masquées d'un joueur : le tracker ne les connaît pas (lot 14 non fait).
- Autres serveurs Discord que celui de Liberastra (`Discord__GuildId`) : quand il est réglé,
  `/tracker`, son autocomplétion et `/tracker-acces` refusent tout autre serveur.
- Pagination par boutons : `membres` prend une option `page`.

## 4. Vue d'ensemble

```
Discord ──slash command──▶ bot Liberastra (panda, .NET 10, Discord.Net)
                              │  1. contrôle d'accès (rôle ↔ sous-commande, SQLite du bot)
                              │  2. HTTPS + x-api-key (bot:read) + empreinte du certificat
                              ▼
                 VPS : nginx  /bot-api/  (allow 185.146.193.199 ; deny all ; limit_req)
                              │
                              ▼
                 API tracker  /api/bot/*  (schéma BotRead, lecture seule)
                              │  journal d'activité : bot:<commande>, discord:<id>, <cible>
                              ▼
                 tracker.db (lecture)          api.db (journal)
```

## 5. Côté tracker

### 5.1 Portée de clé `bot:read`

- `ApiKeyScopes.BotRead = "bot:read"`, à côté de `ApiKeyScopes.DiscordIngest`.
- Nouveau schéma d'authentification `BotReadKeyAuthHandler`, sur le modèle de
  `DiscordIngestKeyAuthHandler` : en-tête `x-api-key`, clé valide, non révoquée, non expirée,
  **portée exactement `bot:read`**, compte non banni. Il pose les claims de l'utilisateur et
  `scope=bot:read`.
- `ApiKeyAuthHandler` (schéma général) continue de refuser toute clé à portée : la clé du bot
  ne vaut rien ailleurs que sur `/api/bot/`.
- Le contrôleur du bot n'accepte **que** ce schéma (schéma `BotReadKey`, politique `BotRead`,
  sur le modèle de `DiscordIngest` ; le sélecteur `Smart` envoie `/api/bot` à ce schéma) :
  ni JWT, ni clé ordinaire, ni clé d'administration.
- Comme `discord:ingest`, une clé `bot:read` doit avoir une date d'expiration, à 365 jours au
  plus : elle se renouvelle au moins une fois par an.
- Création : le site ne gère plus les clés d'API, et le compte du bot ne se connecte jamais.
  Une route d'administration `POST /api/admin/users/{id}/api-keys` (politique `AdminOnly`)
  crée une clé **à portée** (jamais une clé complète) pour un autre compte, ici le compte
  dédié non administrateur `liberastra-bot`. La clé en clair n'est montrée qu'une fois ; elle
  n'est copiée que dans le `.env` de `panda`.

### 5.2 Routes `/api/bot/`

Toutes en GET, réponses JSON compactes, dates en UTC (`…Z`). Les identifiants sont
normalisés comme sur le site : SID en majuscules ; pseudo comparé sans casse.

| Route | Paramètres | Réponse |
|---|---|---|
| `search` | `q` : 2 à 50 caractères ; `kind` facultatif : `orgs` ou `players` (sinon les deux) | `{ orgs: [{ sid, name, membersCount }], players: [{ handle, displayName }] }`, 10 au plus de chaque |
| `players/{handle}` | — | `{ handle, displayName, citizenId, enlisted, location, profileRead, currentOrgs: [{ sid, name, rank, stars, since, lastSeen, active }], lastSeen }` |
| `players/{handle}/history` | — | `{ handle, orgs: [{ sid, name, rank, stars, since, lastSeen, active }], handles: [{ handle, firstSeen, lastSeen }], events: [{ at, type, orgSid, old, new }] }` (15 derniers événements) |
| `orgs/{sid}` | — | `{ sid, name, archetype, lang, recruiting, roleplay, membersCount, counts: { total, visible, redacted, hidden, at } \| null, trend30d: { from, to } \| null, membersReadAt }` |
| `orgs/{sid}/members` | `page` ≥ 1 | `{ sid, page, pageSize: 25, total, items: [{ handle, displayName, rank, stars, since }] }` (membres actifs) |
| `orgs/{sid}/movements` | `days` : 1 à 90 (7 par défaut) | `{ sid, days, joined: [{ handle, at }], left: [{ handle, at }], truncated }` (50 au plus de chaque) |

Sources, pour rester identiques au site :

- `search` : la recherche d'orgs du site par nom ou SID (jokers échappés), l'effectif venant du
  dernier instantané de chaque org trouvée ; pour les joueurs, une recherche propre au bot sur le
  pseudo et le nom affiché seulement (fiches et rosters), sans les notes internes ni les fiches
  manuelles du site, que Discord ne doit pas permettre de sonder ; l'autocomplétion ne demande
  que la moitié utile (`kind`) ;
- pseudo saisi : d'abord tel quel dans `users`, puis sans tenir compte de la casse dans
  `users`, puis comme ancien pseudo (`user_handle_history`, sans casse), qui mène au pseudo
  actuel du citoyen, enfin sans tenir compte de la casse dans les rosters (index `UserHandle`
  en NOCASE) pour les joueurs sans fiche. L'historique passe avant les rosters : l'ancien
  pseudo d'un citoyen renommé reste dans les anciens instantanés de roster et ne doit pas
  masquer son pseudo actuel ;
- `players/{handle}` : la fiche citoyen (`users`) ; si le joueur n'a pas de fiche mais figure
  dans des rosters, réponse partielle (`profileRead: false`), comme la fiche partielle du site ;
  404 seulement s'il n'est nulle part ;
- orgs actuelles et passées : la dernière ligne de roster par org et la date de première
  apparition, comme `GET /api/users/{handle}/organizations` ;
- `handles` : `user_handle_history` du citoyen ; `events` : `change_events` du pseudo ;
- `orgs/{sid}` : le dernier instantané d'annonce, le dernier `org_member_counts` (visibles,
  masquées R et H), et `trend30d` = effectif RSI (`TotalRows`, masqués compris) d'il y a
  30 jours (dernier comptage à cette date, sinon le plus ancien) et effectif actuel ;
- `members` : `GetLatestPageAsync(sid, active: true, page, 25)` ;
- `movements` : `change_events` de l'org, types `member_joined` et `member_left`,
  `Timestamp >= now - days`, triés du plus récent ; une requête dédiée, servie par un index
  (contrôlé par un test de plan de requête).

### 5.3 Traçabilité

Le bot envoie à chaque requête :

- `X-Discord-User` : l'identifiant Discord de la personne (chiffres, 17 à 20) ;
- `X-Bot-Command` : la sous-commande (une des six), ou `autocomplete` pour les suggestions de
  saisie. Les requêtes `autocomplete` ne sont pas inscrites au journal (une par frappe).

L'API inscrit une ligne dans `activity_logs`, sans changer son schéma :

| Colonne | Valeur |
|---|---|
| `ApiUserId` | le compte `liberastra-bot` |
| `Action` | `bot:<sous-commande>`, par exemple `bot:historique` |
| `EntityType` | `discord:<identifiant Discord>` |
| `EntityId` | la cible : pseudo, SID ou texte cherché (100 caractères au plus) |
| `IpAddress` | l'IP vue par l'API (celle de `panda`, relayée par nginx) |

En-têtes absents ou invalides : 400, rien n'est lu.

### 5.4 nginx

Nouveau bloc dans `deploy/nginx/sc-tracker.conf`, sur le modèle de `/ingest/discord/` :

```nginx
limit_req_zone $binary_remote_addr zone=sc_bot_api:1m rate=30r/m;

location /bot-api/ {
    allow 185.146.193.199;   # panda (bot Liberastra)
    deny all;
    limit_req zone=sc_bot_api burst=10 nodelay;
    limit_req_status 429;
    proxy_pass http://127.0.0.1:5000/api/bot/;
}
```

Les en-têtes du serveur (`X-Forwarded-For` écrasé, etc.) s'appliquent comme ailleurs.

### 5.5 Confidentialité

- Le bot ne montre que ce que le site montre déjà à un compte connecté.
- Une affiliation masquée (lignes R et H d'un roster) est seulement comptée dans la fiche d'org.
  Elle n'est jamais attribuée à un joueur, et le tracker ne sait d'ailleurs pas qui elle cache.
- La fiche joueur précise : « le tracker ne voit que les orgs où le joueur est visible ».
- Aucune réponse de `/api/bot/` n'est transmise à Gemini, ni à `/ask`, `/logs-ask` ou `/psyche`.
  Le code du bot ne fait qu'afficher ces réponses.

## 6. Côté bot (Liberastra, `panda`)

### 6.1 Réglages

Nouvelle section `TrackerApi` dans `BotConfig` et `.env` :

| Variable | Exemple |
|---|---|
| `TrackerApi__BaseUrl` | `https://141.95.51.193/bot-api/` |
| `TrackerApi__ApiKey` | la clé `bot:read` |
| `TrackerApi__CertSha256` | empreinte SHA-256 (hexadécimal) du certificat servi par le VPS |

Une valeur manquante désactive le groupe `/tracker` (message « commande non configurée »),
sans empêcher le bot de démarrer.

### 6.2 `TrackerApiClient`

- Client HTTP typé (`AddHttpClient<TrackerApiClient>`), comme `UexApiClient`.
- Gestionnaire principal avec `ServerCertificateCustomValidationCallback` : accepte le
  certificat **seulement** si son empreinte SHA-256 est celle configurée (le nom d'hôte
  n'est pas vérifié, le certificat étant auto-signé et l'accès se faisant par IP).
- En-têtes : `x-api-key`, `X-Discord-User`, `X-Bot-Command`. Délai maximal : 10 s.
- Résultats typés : `Ok(dto)`, `NotFound`, `RateLimited`, `Unavailable(reason)`. Aucune
  exception ne remonte jusqu'à la commande.

### 6.3 Groupe `/tracker`

Module `TrackerCommands` : `[Group("tracker", "Recherches dans le tracker d'organisations")]`.

| Sous-commande | Options | Embed |
|---|---|---|
| `joueur` | `pseudo` (autocomplétion) | pseudo, nom affiché, n° citoyen, inscription, lieu, orgs actuelles (rang, étoiles, depuis), dernière apparition ; mention des affiliations invisibles au tracker |
| `historique` | `pseudo` (autocomplétion) | orgs passées et actuelles (dates), anciens pseudos, 15 derniers événements |
| `recherche` | `texte` | orgs et joueurs trouvés, 10 de chaque |
| `org` | `sid` (autocomplétion) | nom, SID, type, langue, recrutement, effectif RSI (visibles / masqués), tendance sur 30 jours, date de la dernière lecture |
| `membres` | `sid` (autocomplétion), `page` (défaut 1) | 25 membres par page (pseudo, rang, étoiles, depuis), « page x / y » |
| `mouvements` | `sid` (autocomplétion), `jours` (1 à 90, défaut 7) | arrivées et départs avec leurs dates, « tronqué » au-delà de 50 |

- Toutes les réponses sont éphémères, différées (`DeferAsync(ephemeral: true)`) puis complétées,
  pour tenir dans le délai de 3 s de Discord.
- L'autocomplétion appelle `search` (au moins 2 caractères, `kind=orgs` ou `kind=players`, 2,5 s
  au plus). Elle ne renvoie rien à quelqu'un qui n'a pas accès à la sous-commande concernée.
- Pseudo et SID saisis sont vérifiés (`[A-Za-z0-9_-]`, 60 caractères au plus) : sinon
  « Introuvable », sans requête au tracker.
- La mise en forme vit dans un `TrackerEmbeds` à part (tronquée aux limites de Discord :
  25 champs, 1 024 caractères par champ, 6 000 par embed).

### 6.4 Accès par grade

Table `TrackerCommandAccess` (migration EF du bot) :

| Colonne | Rôle |
|---|---|
| `Id` | clé |
| `GuildId` | serveur Discord |
| `Command` | une des six sous-commandes |
| `RoleId` | rôle autorisé |
| `GrantedBy`, `GrantedAt` | qui a ouvert, quand |

Index unique (`GuildId`, `Command`, `RoleId`).

Règle d'accès (service `TrackerAccessService`, testable sans Discord) :

1. la permission Discord **Administrateur** passe toujours ;
2. sinon, accès si l'un des rôles de la personne est autorisé pour cette sous-commande sur ce serveur ;
3. sinon, refus. Une sous-commande sans aucun rôle est fermée à tous ; le message de refus
   le dit et indique qu'un administrateur peut l'ouvrir.

Appliquée par une précondition `[RequireTrackerAccess("<sous-commande>")]` sur chaque
sous-commande, et par les gestionnaires d'autocomplétion.

Groupe `/tracker-acces`, réservé à la permission Administrateur (masqué aux autres par
`DefaultMemberPermissions`) :

- `ouvrir <sous-commande> <rôle>` ;
- `fermer <sous-commande> <rôle>` ;
- `voir` : les rôles autorisés pour chacune des six sous-commandes.

### 6.5 Journal du bot

Chaque utilisation est inscrite dans les journaux du bot : sous-commande, identifiant Discord,
cible, résultat (`ok`, `introuvable`, `refusé`, `indisponible`). Les refus d'accès aussi.

## 7. Erreurs

| Situation | Message (privé) | Journaux |
|---|---|---|
| Accès refusé | « Tu n'as pas accès à /tracker <cmd>. Un administrateur peut l'ouvrir avec /tracker-acces. » | refus |
| Joueur ou org inconnus | « Introuvable dans le tracker. » + pour un joueur : « le tracker ne connaît que les joueurs vus dans un roster visible » | info |
| Recherche trop courte | « Au moins 2 caractères. » | — |
| 429 (nginx ou API) | « Trop de requêtes, réessaie dans une minute. » | info |
| Délai dépassé, réseau, 5xx | « Le tracker ne répond pas, réessaie plus tard. » | avertissement |
| Empreinte du certificat différente | « Tracker indisponible. » | **erreur** |
| 401 / 403 (clé révoquée, IP refusée) | « Tracker indisponible. » | **erreur** |
| Réglages manquants | « Commande non configurée. » | avertissement au démarrage |

## 8. Tests

### 8.1 Tracker (`Collector.Api.Tests`)

- La clé `bot:read` est acceptée sur chaque route `/api/bot/*`, refusée partout ailleurs (401).
- Une clé ordinaire, la clé d'administration, un JWT : refusés sur `/api/bot/*`.
- Une clé `discord-ingest` : refusée sur `/api/bot/*`, et inversement.
- En-têtes `X-Discord-User` / `X-Bot-Command` absents ou invalides : 400.
- Chaque route : contenu attendu sur des données de test ; joueur sans fiche → réponse
  partielle ; inconnu → 404 ; bornes de `q`, `page` et `days`.
- Une affiliation masquée d'une org n'apparaît que dans les compteurs, jamais dans une liste.
- Une ligne `activity_logs` par requête, avec les valeurs du § 5.3.
- Plan de requête de `movements` : index utilisé, pas de parcours complet.

### 8.2 Bot (nouveau projet `BotLiberastra.Tests`, xUnit)

- `TrackerAccessService` : administrateur, rôle autorisé, rôle non autorisé, sous-commande
  fermée, autre serveur.
- Validation du certificat : bonne empreinte acceptée, autre refusée, configuration vide refusée.
- `TrackerApiClient` avec un `HttpMessageHandler` simulé : en-têtes envoyés, lecture des
  réponses, 404 / 429 / 401 / délai → résultat typé.
- `TrackerEmbeds` : contenu et troncature aux limites de Discord.

### 8.3 Recette

Dans un salon de test du serveur Liberastra : chaque sous-commande avec un rôle autorisé, un
rôle non autorisé et un administrateur ; `/tracker-acces ouvrir/fermer/voir` ; autocomplétion
refusée sans accès ; une requête visible dans le journal d'activité du tracker.

## 9. Déploiement

Chaque étape qui touche un serveur attend l'accord explicite de l'utilisateur.

1. **Tracker** : code et tests ; CI verte ; déploiement (`deploy.sh <sha>`, sans `--collector`,
   pas de migration de `tracker.db`) ; installation du nouveau bloc nginx (`nginx -t` puis
   rechargement).
2. **Compte et clé** : création du compte `liberastra-bot` (non administrateur) et de sa clé
   `bot:read` ; relevé de l'empreinte SHA-256 du certificat du VPS ; les trois valeurs sont
   écrites dans le `.env` de `panda`, jamais dans un dépôt.
3. **Mise à jour du dépôt du bot** :
   - branche `server-sync` partie de `13ec14a` (`main` de GitHub au 2026-10-03) avec les
     fichiers propres à `panda` : `Services/AuditLogGeminiService.cs`,
     `Services/AuditLogFunctionTool.cs`, les versions de `panda` de `Commands/SlashCommands.cs`
     et de `Program.cs`, et `deploy.sh` ; poussée sur GitHub (droits d'écriture de `skyline624`
     vérifiés), puis fusionnée dans `main`. GitHub correspond alors exactement au code en service.
4. **Déploiement du bot par envoi direct** : `panda` ne peut plus lire GitHub (dépôt privé, pas
   d'identifiants, `git fetch` échoue). Le poste de développement pousse sur `panda`
   (`git push panda main:release`) ; `deploy.sh` déploie la branche locale `release` au lieu
   de `origin/main` (même compilation dans `publish_new/`, même bascule, ancienne version
   gardée dans `publish_old/`).
5. **Bot** : développement de `/tracker` sur `main` du bot (branche dédiée, tests), envoi sur
   `panda`, `./deploy.sh` ; les commandes sont enregistrées sur le serveur Discord au
   démarrage. Recette (§ 8.3), puis ouverture des sous-commandes aux rôles voulus avec
   `/tracker-acces`.

Retour arrière : bot — `publish_old/` ; tracker — `rollback.sh` ; nginx — retrait du bloc.

## 10. Risques et points ouverts

- **IP de `panda`** : si elle change, le bot reçoit des 403 jusqu'à la mise à jour de
  l'`allow` nginx. Le message d'erreur dans les journaux du bot l'indique.
- **Certificat du VPS** : s'il est renouvelé, l'empreinte change ; il faut mettre à jour
  `TrackerApi__CertSha256` en même temps. Les journaux du bot signalent l'écart.
- **Débit** : 30 requêtes par minute pour tout le bot ; l'autocomplétion en consomme. Si c'est
  trop juste à l'usage, relever la limite nginx.
- **Commandes du bot hors de ce périmètre** : `/psyche` (profilage psychologique de membres
  par Gemini) existe déjà ; ce design garantit seulement qu'aucune donnée du tracker n'y
  parvient.
