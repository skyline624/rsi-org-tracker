# Rosters Discord des corpos via un plugin Vencord — design

Date : 2026-09-30
Statut : proposé, en attente de relecture (v2, après revue en 5 angles)

## 1. Objectif

Compléter l'historique RSI par les Discord des corpos. Un plugin Vencord, déclenché à la main,
lit la liste des membres d'un serveur Discord suivi (comptes, pseudos, rôles, dates d'arrivée) et
l'envoie au tracker. Le tracker en tire :

- un onglet **DISCORD** qui liste les serveurs suivis, la corpo RSI de chacun, ses membres et
  leurs rangs ;
- un **historique** par serveur : arrivées, départs, retours, changements de rôles et de pseudo ;
- des **liens** entre comptes Discord et citoyens RSI, proposés automatiquement puis validés à
  la main ;
- des **recoupements** avec l'historique RSI : écarts de présence, écarts de rang, profils
  croisés, multi-appartenance.

## 2. Décisions prises

| Sujet | Décision |
|---|---|
| Serveur ↔ corpo | Un serveur Discord est relié à une seule corpo (un SID RSI). Une corpo peut avoir plusieurs serveurs (principal, recrutement…). Les rôles Discord servent de rangs. |
| Usages | Suivre les mouvements, recouper avec RSI, profils croisés, multi-appartenance. |
| Liaison Discord ↔ RSI | Suggestions automatiques, puis validation manuelle. Seuls les liens validés comptent. |
| Visibilité | Utilisateurs connectés du site (déjà le cas pour tout le site). |
| Droits sur les serveurs | Mixtes : le plugin choisit la méthode serveur par serveur. |
| Déclenchement | Uniquement sur clic, sur un serveur coché « suivi ». Aucune synchronisation automatique. |
| Client Discord | Discord desktop + Vencord uniquement. |
| Transport | HTTPS public sur l'IP du VPS, certificat auto-signé épinglé dans le plugin. |
| Émetteurs | Tout utilisateur connecté peut créer une clé « envoi Discord », limitée à l'envoi et à durée de vie bornée. |
| Stockage de l'historique | État courant (une ligne par serveur et par compte) + journal d'événements. Les séjours passés se reconstruisent depuis le journal. |

## 3. Hors périmètre

- Collecte par un bot Discord, même si les admins d'un serveur l'acceptent.
- Vesktop, l'extension navigateur, le client web.
- Synchronisation automatique ou périodique.
- Avatars, Nitro, invitations, présence, messages, permissions.
- **Départs sur les serveurs sans accès à la recherche de membres** : sur ces serveurs, aucun
  envoi n'est complet, donc aucun départ n'est jamais déduit (§ 5.4, § 9).
- **Annulation d'un envoi.** Le garde-fou contre les départs massifs (§ 9.3) et la suppression
  d'un serveur (§ 13.2) suffisent. Une annulation qui restaure l'état précédent n'est pas prévue.
- Événements Discord dans le flux `/changes` et dans les statistiques RSI.
- Fusion de deux `tracked_entities` qui s'avèrent être la même personne.
- Gestion par un admin des clés des autres utilisateurs : bannir le propriétaire désactive déjà
  ses clés.

## 4. Vue d'ensemble

```
Discord desktop + Vencord                      VPS
┌──────────────────────────────┐              ┌──────────────────────────────────────┐
│ ScTracker (renderer)         │              │ nginx :443 (certificat auto-signé)   │
│  menu « envoyer les membres »│              │  /ingest/discord/  ──► API :5000     │
│  collecte (API Discord)      │              │  /                 ──► Next.js :3000 │
│        │ IPC                 │   HTTPS      │                                      │
│ native.ts (Node) ────────────┼─────────────►│ API : schéma DiscordIngestKey        │
│  épinglage SHA-256           │  x-api-key   │   → DiscordIngestController          │
└──────────────────────────────┘  (scope      │   → tracker.db (tables discord_*)    │
                                   discord:   │ API : lectures api/discord/…         │
                                   ingest)    │   ◄── Next.js (JWT) ◄── navigateur   │
                                              └──────────────────────────────────────┘
```

Aujourd'hui l'API n'écoute que sur 127.0.0.1:5000 et nginx n'expose que Next.js. Cette feature
ajoute **un seul** chemin public vers l'API. Il est réservé à l'ingestion, et seule une clé
`discord:ingest` y est acceptée.

## 5. Plugin Vencord

### 5.1 Emplacement, build, installation

- Nouveau dossier racine `vencord/` :

```
vencord/
  README.md               installation, configuration, recette manuelle, avis RGPD (français)
  package.json            vitest + typescript, champ packageManager (pnpm) ; lockfile commité
  tsconfig.json           n'inclut que scTracker.desktop/lib/** et tests/**
  VENCORD_REF             commit Vencord de référence (CI Vencord verte)
  install.mjs             copie scTracker.desktop dans <Vencord>/src/userplugins/
  tests/                  tests vitest et fixtures (certificat et clé de test auto-signés)
  scTracker.desktop/      le plugin (suffixe .desktop : exclu du build web, où native.ts n'existe pas)
    index.tsx             definePlugin, menu contextuel, orchestration
    settings.tsx          panneau de réglages (OptionType.COMPONENT)
    native.ts             point d'entrée IPC : envoi HTTPS épinglé
    collect/
      strategy.ts         choix de la méthode selon les droits
      memberSearch.ts     recherche de membres paginée
      refresh.ts          rafraîchissement par GUILD_MEMBERS_REQUEST
      roleMembers.ts      IDs par rôle
    lib/                  modules purs : aucun import de collect/, @webpack/*, @api/*, @utils/*, electron
      payload.ts          construction et bornes du message
      coverage.ts         complétude
      pacing.ts           espacement, plafond d'appels, annulation
      pinnedPost.ts       POST HTTPS avec vérification d'empreinte (node:https)
      fingerprint.ts      normalizeFingerprint
      errors.ts           traduction des réponses en messages français
```

- Les tests vivent dans `vencord/tests/`, **jamais** dans le dossier du plugin, qui est copié
  tel quel dans Vencord.
- Installation : `node vencord/install.mjs <chemin de Vencord>`, à relancer à chaque mise à jour.
  Le script **copie** le dossier ; un lien symbolique casserait la résolution des alias Vencord
  par esbuild. Ensuite `pnpm build` puis `pnpm inject` dans Vencord. Toute modification de
  `native.ts` demande un redémarrage complet de Discord.
- `index.tsx` commence par `definePlugin({ name: "ScTracker", …`. `name` doit être la première
  propriété et une chaîne littérale : c'est elle qui donne `VencordNative.pluginHelpers.ScTracker`.

### 5.2 Réglages

| Réglage | Type | Stockage |
|---|---|---|
| URL du tracker (`https://<IP>`) | texte | réglages Vencord |
| Empreinte SHA-256 du certificat | texte, normalisé par `normalizeFingerprint` | réglages Vencord |
| Clé d'API | texte masqué | `@api/DataStore` (IndexedDB), clé `ScTracker_apiKey` |
| Serveurs suivis | `string[]` | réglage `CUSTOM` |
| Résultat du dernier envoi par serveur | objet | réglage `CUSTOM` |

- La clé ne va jamais dans les réglages : `settings.json` est en clair et Cloud Sync l'envoie.
- L'URL et l'empreinte à saisir sont affichées sur le site, dans le panneau « Clé d'envoi
  Discord » (§ 6.4).
- `normalizeFingerprint` retire un éventuel préfixe `sha256 Fingerprint=`, les `:` et les
  espaces, passe en majuscules, puis exige `^[0-9A-F]{64}$`. La ligne affichée par `openssl` peut
  donc être collée telle quelle.

Le panneau (`COMPONENT`) affiche :

- les champs URL, empreinte et clé ;
- la liste des serveurs (`SortedGuildStore.getFlattenedGuildIds()`), avec pour chacun une case
  « suivi », un bouton « Envoyer » (serveurs cochés seulement) et le statut du dernier envoi
  (date, complet ou partiel, méthode, nombre, corpo reliée ou non, erreur) ;
- un bouton **« Envoyer les serveurs cochés »** et un bouton **« Arrêter »**.

### 5.3 Déclenchement

- Menu contextuel du serveur (`contextMenus['guild-context']`) :
  - sur un serveur **non coché** : « SC Tracker : suivre ce serveur », qui coche la case sans
    rien envoyer ;
  - sur un serveur **coché** : « SC Tracker : envoyer les membres » et « SC Tracker : ne plus
    suivre ».
- Le panneau de réglages : envoi d'un serveur, ou envoi des serveurs cochés l'un après l'autre,
  avec 5 s de pause entre deux.
  - Le lot **s'arrête** au premier 401, 403, 429, 503, `guild_excluded` ou empreinte différente.
    Les serveurs restants sont marqués « non envoyé ».
  - Il **continue** après un 400, un 413 ou un 409 `stale_sync`.
- Rien d'autre : aucune minuterie, aucun déclenchement à l'ouverture d'un serveur.
- Pendant la collecte, un toast montre la progression. À la fin, il indique le nombre de membres,
  complet ou partiel, la méthode, les événements créés et, si le serveur n'est relié à aucune
  corpo, « Serveur non relié à une corpo : relie-le sur <URL>/discord ».
- « Arrêter » annule la collecte et **n'envoie rien**.

### 5.4 Collecte

Un membre n'est **jamais** envoyé tel qu'il est lu dans le cache du client. Pour un compte
utilisateur, `GuildMemberStore` garde des membres partis et des rôles ou pseudos périmés, et
`GuildMemberCountStore` n'est pas tenu à jour. Chaque membre envoyé vient donc soit d'une réponse
de recherche de membres, soit d'un chunk gateway reçu pendant cette collecte.

`strategy.ts` choisit la méthode pour chaque serveur :

1. **`member-search`**, si `PermissionStore.canAccessMemberSafetyPage(guild)` est vrai.
   - Requête : `RestAPI.post({ url: Constants.Endpoints.GUILD_MEMBER_SEARCH(guildId), body: {
     limit: 1000, sort: 2, after } })`.
     - `sort: 2` = JOINED_AT_ASC : les arrivées pendant le parcours tombent en fin de liste.
     - `after` est absent à la première page, puis vaut `{ guild_joined_at:
       Date.parse(dernier.member.joined_at), user_id: dernier.member.user.id }`, un entier en
       millisecondes.
   - Dédoublonnage par `user.id`.
   - Arrêt dès qu'une page renvoie moins de `limit` membres. Une page qui n'apporte aucun ID
     nouveau arrête aussi la collecte, et l'envoi est alors partiel.
   - Données lues **dans la réponse** : `member.user.id`, `member.user.username`,
     `member.user.global_name`, `member.user.bot === true`, `member.nick`, `member.roles`,
     `member.joined_at`.
   - Attendu : `total_result_count` de la dernière page.
   - Réponse 202 (code 110000, index pas prêt) : attente de `retry_after`, 3 essais au plus, puis
     bascule sur la méthode 2.
   - Réponse 403 : bascule sur la méthode 2.
   - **Seule méthode qui peut être complète** : `complete = (IDs collectés === total_result_count)`.
2. **`role-members`** :
   - IDs candidats : `GET GUILD_ROLE_MEMBER_IDS(guildId, roleId)` pour chaque rôle non géré, hors
     `@everyone` et non vide selon `GET GUILD_ROLE_MEMBER_COUNTS(guildId)` (100 IDs au plus par
     rôle), plus `GuildMemberStore.getMemberIds(guildId)`.
   - Tous les IDs sont **redemandés** par `refresh.ts` (voir plus bas), même ceux déjà en cache.
   - Toujours `complete: false`. Attendu : `null`.
3. **`cache`**, si les appels par rôle échouent : IDs candidats = `GuildMemberStore.getMemberIds`
   seul, redemandés de la même façon. Toujours `complete: false`.

**Rafraîchissement (`refresh.ts`)** :

- Envoi de `FluxDispatcher.dispatch({ type: 'GUILD_MEMBERS_REQUEST', guildIds: [guildId],
  userIds })`, par lots de 100 IDs au plus.
- Pendant le lot, le plugin écoute `GUILD_MEMBERS_CHUNK_BATCH` et ne garde que les chunks du
  bon `guildId`, reçus après l'envoi.
- Un ID est résolu quand il apparaît :
  - dans `chunk.members[].user.id` : il est présent, avec les données du chunk ;
  - dans `chunk.notFound` : il est parti, et on le retire.
- La simple présence dans `GuildMemberStore` ne suffit pas.
- Le lot se termine quand tous ses IDs sont résolus, ou au bout de 10 s. Les IDs non résolus sont
  alors retirés et l'envoi reste partiel. Le lot suivant attend 30 s, au cas où la gateway
  limiterait les requêtes.

**Rôles** : lus **après** la collecte des membres (`GuildRoleStore.getSortedRoles`, hors
`@everyone`) et toujours envoyés en entier.

**Règles communes** :

- **Rythme** : au moins 1,2 s entre deux appels REST Discord, et 1 s entre deux lots gateway.
  Une réponse 429 impose d'attendre `retry_after`.
- **Plafond** : 200 appels par serveur. Chaque requête REST et chaque lot gateway compte pour un.
  Au-delà, on envoie ce qui a été collecté, marqué partiel.
- **Durée** : le plugin mesure `collectionDurationMs` avec `performance.now()`, du début à la fin
  de la collecte.

### 5.5 Envoi

- Le renderer construit le message (`lib/payload.ts`, § 7.1) : IDs en texte, aucune permission
  (ce sont des `bigint`, que `JSON.stringify` refuse). Il refuse un corps de plus de 25 Mio
  avant tout envoi, avec le message du 413.
- Il appelle `(VencordNative.pluginHelpers.ScTracker as PluginNative<typeof import("./native")>)
  .postSync({ url, fingerprint, apiKey, guildId, body })`.
- `native.ts` exporte `postSync(_e: IpcMainInvokeEvent, args)`. Il valide ses entrées :
  - URL `https:` sans chemin ni paramètres ;
  - empreinte normalisée ;
  - `guildId` au format snowflake ;
  - corps de 25 Mio au plus.
- Il construit lui-même le chemin `/ingest/discord/guilds/{guildId}/syncs`, puis délègue à
  `lib/pinnedPost.ts` :
  - `https.request` avec `agent: false`, pour un socket neuf, et `rejectUnauthorized: false`,
    **sur cette requête uniquement** ;
  - à `secureConnect`, comparaison de `normalizeFingerprint(socket.getPeerCertificate()
    .fingerprint256)` avec l'empreinte configurée ;
  - si elle diffère : `req.destroy(Object.assign(new Error('pin'), { code: 'PIN_MISMATCH' }))`
    avant tout `write`/`end`. À `secureConnect`, rien n'a été écrit sur le socket : ni l'en-tête
    `x-api-key` ni le corps ne partent ;
  - si elle correspond : `req.end(body)` ;
  - `req.setTimeout(30 000)` est un délai **d'inactivité** du socket, pas un délai total.
- Jamais de `NODE_TLS_REJECT_UNAUTHORIZED` ni de réglage TLS global dans le processus principal
  de Discord.
- Retour vers le renderer : `{ status, body, headers: { retryAfter? }, error? }`, avec `error`
  parmi `pin_mismatch`, `network` et `no_response_after_upload`. `lib/errors.ts` le traduit
  (§ 14.1).

## 6. Transport, authentification, sécurité

### 6.1 nginx

Ajout à `deploy/nginx/sc-tracker.conf`, avant `location /` :

```nginx
limit_req_zone  $binary_remote_addr zone=sc_discord_ingest:1m rate=10r/m;
limit_conn_zone $binary_remote_addr zone=sc_discord_conn:1m;

location /ingest/discord/ {
    limit_except POST { deny all; }
    limit_req zone=sc_discord_ingest burst=10 nodelay;
    limit_req_status 429;
    limit_conn sc_discord_conn 2;
    client_max_body_size 25m;
    proxy_pass http://127.0.0.1:5000/api/ingest/discord/;
}
```

- `proxy_request_buffering` reste activé (valeur par défaut) : l'API ne reçoit qu'un corps
  complet, donc un client lent ne retient jamais le verrou d'ingestion.
- Les en-têtes `X-Forwarded-*` du bloc `server` s'appliquent. L'API fait déjà confiance au
  `X-Forwarded-For` venant du loopback.
- Le commentaire de `src/Collector.Api/Program.cs` (« browsers never talk to it ») est mis à jour
  pour mentionner cette unique route.
- `deploy/README.md` documente l'ajout (à la main, comme les autres changements nginx), puis
  `nginx -t` et `reload`, ainsi que le calcul de l'empreinte :
  `openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256`.
- La location n'est ajoutée en production qu'une fois le lot A livré en entier (§ 17).

### 6.2 Clés d'API à portée limitée : refus par défaut

**Modèle et création**

- `ApiKey` (api.db) reçoit une colonne `Scope` (texte, nullable). Migration côté API, appliquée
  au démarrage.
  - `null` : accès complet, comme les clés existantes.
  - `discord:ingest` : envoi Discord uniquement.
- `CreateApiKeyRequest` accepte `Scope` (`null` ou `discord:ingest`, sinon 400). Pour
  `discord:ingest`, `ExpiresAt` est **obligatoire**, dans le futur et à 365 jours au plus (sinon
  400). Le panneau propose 180 jours par défaut.
- `ApiKeyService.ValidateAsync` renvoie le propriétaire **et** la clé (portée, expiration).
- `ApiKeyDto` et le DTO de création exposent `Scope` et `ExpiresAt`.

**Authentification** : la séparation se fait au niveau du schéma, pas seulement des politiques.

- Le `ForwardDefaultSelector` du schéma `Smart` envoie toute requête dont le chemin commence par
  `/api/ingest/discord` vers un nouveau schéma `DiscordIngestKey`, quels que soient les en-têtes
  présents.
- `DiscordIngestKey` lit `x-api-key` et n'accepte **que** des clés `Scope = discord:ingest` :
  jamais la clé admin statique, jamais un JWT, jamais une clé complète. Il émet `NameIdentifier`,
  `Name` et le claim `scope=discord:ingest`, **sans** rôle Admin, même si le propriétaire est
  admin.
- Partout ailleurs, le gestionnaire `ApiKey` existant renvoie `Fail` pour une clé limitée.
- Une clé limitée n'est donc authentifiée nulle part hors de l'ingestion (401), quelle que soit
  la politique d'une route future.
- Le routage passe par le sélecteur de `Smart`, et pas seulement par la politique, parce que
  `UseRateLimiter` s'exécute entre `UseAuthentication` et `UseAuthorization`. Le limiteur lit
  donc l'identité posée par l'authentification.
- Nouvelle politique `DiscordIngest` : schéma `DiscordIngestKey`, claim `scope=discord:ingest`.

**Correctif au passage** : `ApiKeyService` tire le préfixe de 8 caractères base64 dont il retire
`+` et `/`, puis le tronque à 6. Il lève une exception quand plus de 2 caractères ont été retirés,
soit environ 0,15 % des créations (erreur 500). Le préfixe est désormais tiré d'un alphabet sans
`+` ni `/`.

### 6.3 Limites

- **Débit** : politique nommée `discord-ingest` (`RateLimitingExtensions`), réglée par
  `RateLimitSettings.DiscordIngest { PermitLimit = 20, WindowSeconds = 600 }` et lue sous
  `Api:RateLimit:DiscordIngest:*`.
  - Partition `discord-ingest:user:{NameIdentifier}` si la requête est authentifiée, sinon
    `discord-ingest:ip:{ClientIp}`.
  - Elle s'ajoute au limiteur global.
  - `options.OnRejected` écrit `Retry-After` en secondes, tiré de `MetadataName.RetryAfter` du
    lease. Cela vaut pour toutes les politiques.
  - `ApiFactory` relève ce budget par `COLLECTOR_API_Api__RateLimit__DiscordIngest__PermitLimit`.
- **Taille** :
  - `[RequestSizeLimit(25 * 1024 * 1024)]` sur l'action ;
  - `client_max_body_size 25m` sur la location nginx ;
  - vérification préalable dans le plugin (§ 5.5).
  - Au pire (461 octets par membre, 10 rôles, noms de 32 caractères), 50 000 membres font environ
    22 Mio.
- **413 propre** : `ExceptionHandlingMiddleware` traduit
  `Microsoft.AspNetCore.Http.BadHttpRequestException` en ProblemDetails portant son `StatusCode`
  (413 pour un corps trop gros), journalisé en Information, sans trace.

### 6.4 Web : panneau « Clé d'envoi Discord »

Il remplace le panneau « v2 » de `(user)/settings/page.tsx` et contient :

- **Configuration du plugin** : URL publique et empreinte du certificat, chacune avec un bouton
  copier.
  - Elles viennent de `GET api/discord/ingest-config` (`[Authorize]`), qui renvoie les réglages
    `Discord:Ingest:PublicUrl` et `Discord:Ingest:CertificateSha256`, posés dans `api.env` par
    l'étape du `deploy/README.md`.
  - S'ils ne sont pas réglés, le panneau affiche « Demande l'URL et l'empreinte à
    l'administrateur ».
- **Créer une clé** `discord:ingest` : nom (défaut « Vencord <date> ») et expiration (180 jours
  par défaut, 365 au plus). La clé est affichée une seule fois, avec un bouton copier.
- **Mes clés** : nom, portée, créée le, expire le, dernière utilisation, bouton révoquer.
- **Rotation** : créer une clé, la coller dans le plugin, révoquer l'ancienne. Le panneau
  l'explique en une phrase.

Toutes ces actions sont des server actions validées par zod. Aucun autre type de clé n'est
créable depuis l'interface.

## 7. Contrat d'ingestion

### 7.1 Requête

`POST /ingest/discord/guilds/{guildId}/syncs`, relayé vers
`POST /api/ingest/discord/guilds/{guildId}/syncs`, avec les en-têtes `x-api-key` et
`Content-Type: application/json`.

```json
{
  "pluginVersion": "1.0.0",
  "collectedAt": "2026-09-30T12:00:00Z",
  "collectionDurationMs": 84000,
  "guild": { "id": "123456789012345678", "name": "Ma Corpo", "icon": "a_1b2c…", "memberCount": 1234 },
  "coverage": { "method": "member-search", "complete": true, "expectedCount": 1234, "collectedCount": 1234 },
  "roles": [
    { "id": "…", "name": "Officier", "position": 12, "color": "#e67e22", "hoist": true, "managed": false }
  ],
  "members": [
    { "userId": "…", "username": "pilote42", "globalName": "Pilote", "nick": "[CORP] Pilote42",
      "roleIds": ["…"], "joinedAt": "2025-03-14T20:11:05.123+00:00", "bot": false }
  ]
}
```

`collectedAt` est informatif (horloge du PC). Le serveur date l'envoi lui-même :
**`CollectedAt = ReceivedAt − collectionDurationMs`**, c'est-à-dire le début de la collecte.

### 7.2 Réponse 200

```json
{
  "syncId": 42, "isBaseline": false, "isComplete": true, "departureGuardTripped": false,
  "orgSid": "CORP",
  "membersReceived": 1234, "membersOptedOut": 2, "unknownRoleRefs": 0,
  "events": { "joined": 3, "left": 1, "rejoined": 0, "rolesChanged": 5, "nickChanged": 2, "nameChanged": 0 }
}
```

`orgSid` vaut `null` si le serveur n'est relié à aucune corpo.

### 7.3 Validation (400 ProblemDetails, rien n'est écrit)

Les longueurs sont vérifiées dans le code : SQLite n'applique pas `TEXT(n)`.

- **IDs** : snowflakes `^[0-9]{17,20}$`, transportés en texte. `guild.id` = `{guildId}` de l'URL.
- **Textes** : nom de serveur ≤ 100, nom de rôle ≤ 100, `username` ≤ 32, `globalName` ≤ 32,
  `nick` ≤ 32, `pluginVersion` ≤ 20.
- **Icône** : `null` ou `^(a_)?[0-9a-f]{32}$`.
- **Nombres** :
  - `memberCount` et `expectedCount` entre 0 et 1 000 000 ;
  - `position` entre 0 et 1 000 ;
  - `collectionDurationMs` entre 0 et 1 800 000 (30 min).
- **Tailles** : `roles` ≤ 250, `members` ≤ 50 000, `roleIds` d'un membre ≤ 250.
- **Valeurs** :
  - `method` ∈ `member-search`, `role-members`, `cache` ;
  - `color` : `#rrggbb` ou `null` ;
  - dates en ISO 8601 ;
  - pas de `userId` en double.
- **Envoi complet vide** : `coverage.complete = true` avec `members` vide → 400
  `empty_complete_sync`.
- **Rôles inconnus** : un `roleIds[i]` absent de `roles` n'est **pas** une erreur. Ce rôle a été
  supprimé pendant la collecte : il est retiré du membre et compté dans `unknownRoleRefs`.

**Normalisation avant le diff** :

- toutes les dates sont lues en `DateTimeOffset`, converties par `.UtcDateTime`, et `JoinedAt`
  est tronqué à la seconde ;
- `nick` et `globalName` sont nettoyés de leurs espaces de début et de fin, et `""` devient
  `null` ;
- `roleIds` est trié et dédoublonné.

**Complétude recalculée par le serveur**, sur le message **brut**, avant le filtrage des
opt-outs : `isComplete = coverage.complete && method == "member-search" && members.length ==
coverage.expectedCount`. Le serveur ne fait jamais confiance au seul drapeau du plugin.

## 8. Modèle de données (tracker.db)

Tables ajoutées par **une** migration du collecteur (`AddDiscordRosters`), uniquement par ajouts,
en snake_case, sans clés étrangères (convention de tracker.db). Les dates sont en UTC via
`UtcDateTimeConverter`, les snowflakes en `TEXT`, les SID en majuscules. Seule l'API écrit dans
ces tables.

**`discord_guilds`**

| Colonne | Type | Notes |
|---|---|---|
| Id | INTEGER PK | |
| GuildId | TEXT(20) | unique |
| Name | TEXT(100) | |
| IconHash | TEXT(34) null | |
| OrgSid | TEXT(50) null | corpo reliée, index non unique (une corpo peut avoir plusieurs serveurs) |
| OrgMappedByApiUserId, OrgMappedByUsername, OrgMappedAt | null | « responsable » du serveur (§ 11) |
| MemberCount | INTEGER null | déclaré par Discord au dernier envoi |
| FirstSyncAt | DATETIME | `CollectedAt` de l'envoi de base |
| LastSyncAt, LastCollectedAt | DATETIME | réception et `CollectedAt` du dernier envoi accepté |
| LastCompleteSyncAt | DATETIME null | `CollectedAt` du dernier envoi complet |
| AllowMassDepartureOnce | BOOL | levée ponctuelle du garde-fou (§ 9.3) |
| CreatedAt, UpdatedAt | DATETIME | |

**`discord_roles`** : unique (GuildId, RoleId)

| Colonne | Type | Notes |
|---|---|---|
| Id | INTEGER PK | |
| GuildId, RoleId | TEXT(20) | |
| Name | TEXT(100) | |
| Position | INTEGER | |
| Color | TEXT(7) null | |
| Hoist, Managed | BOOL | |
| IsRank | BOOL | à la création : `Hoist && !Managed`. Ensuite modifiable sur le site seulement. |
| RankOrder | INTEGER null | non nul pour tout rôle-rang : initialisé à `Position` quand le rôle devient un rang, puis modifiable sur le site |
| RsiRankLabel | TEXT(100) null | rang RSI équivalent, pour les recoupements |
| FirstSeenAt, LastSeenAt | DATETIME | |
| DeletedAt | DATETIME null | rôle absent d'un envoi (la liste des rôles est toujours complète) ; effacé s'il réapparaît |

**`discord_accounts`** : un compte Discord, tous serveurs confondus

| Colonne | Type | Notes |
|---|---|---|
| Id | INTEGER PK | |
| DiscordUserId | TEXT(20) | unique |
| Username | TEXT(32) | |
| GlobalName | TEXT(32) null | |
| IsBot | BOOL | |
| FirstSeenAt, LastSeenAt | DATETIME | |

**`discord_members`** : unique (GuildId, DiscordUserId) ; index DiscordUserId ; index (GuildId, LeftAt)

| Colonne | Type | Notes |
|---|---|---|
| Id | INTEGER PK | |
| GuildId, DiscordUserId | TEXT(20) | |
| Nick | TEXT(32) null | |
| RoleIdsJson | TEXT | tableau JSON trié |
| JoinedAt | DATETIME null | date d'arrivée Discord, à la seconde |
| FirstSeenAt, LastSeenAt | DATETIME | `CollectedAt` des envois |
| LeftAt | DATETIME null | `null` = présent |

**`discord_member_events`** : index (GuildId, Id) et (DiscordUserId, Id)

| Colonne | Type | Notes |
|---|---|---|
| Id | INTEGER PK | ordre du flux (Id décroissant, comme `change_events`) |
| GuildId | TEXT(20) null | `null` pour un changement de compte |
| DiscordUserId | TEXT(20) | |
| SyncId | INTEGER | envoi source, donc auteur de l'envoi |
| Type | TEXT(30) | voir ci-dessous |
| OldValue, NewValue | TEXT null | voir ci-dessous |
| OccurredAt | DATETIME null | date exacte si connue |
| NotBefore | DATETIME null | date la plus tôt possible (observation précédente) |
| ObservedAt | DATETIME | `CollectedAt` de l'envoi |

Contenu de `OldValue` et `NewValue` selon `Type` :

| Type | OldValue | NewValue |
|---|---|---|
| `joined` | — | `JoinedAt` (ISO 8601 UTC) |
| `left` | `JoinedAt` du séjour qui se termine | — |
| `rejoined` | ancien `JoinedAt` | nouveau `JoinedAt` |
| `roles_changed` | JSON `[{id,name}]` avec les **noms de l'époque** | idem |
| `nick_changed`, `username_changed`, `global_name_changed` | ancienne valeur | nouvelle valeur |

**`discord_syncs`** : index (GuildId, Id)

| Colonne | Type |
|---|---|
| Id | INTEGER PK |
| GuildId | TEXT(20) |
| SubmittedByApiUserId, SubmittedByUsername | |
| ReceivedAt, CollectedAt, DeclaredCollectedAt | DATETIME |
| Method | TEXT(20) |
| DeclaredComplete, IsComplete, IsBaseline, DepartureGuardTripped | BOOL |
| ExpectedCount | INTEGER null |
| CollectedCount, OptedOutCount, UnknownRoleRefCount, EventCount | INTEGER |
| PluginVersion | TEXT(20) |

**Exclusions et rejets**

- **`discord_optouts`** : DiscordUserId (unique), CreatedAt, ByApiUserId, ByUsername, Reason
  (null).
- **`discord_guild_optouts`** : GuildId (unique), CreatedAt, ByApiUserId, ByUsername, Reason
  (null).
- **`discord_link_rejections`** : Id, DiscordUserId, CitizenKey (CitizenId, ou `h:` suivi du
  handle en minuscules), unique (DiscordUserId, CitizenKey), ByApiUserId, ByUsername, CreatedAt.

**Index ajoutés aux tables existantes**, en SQL brut dans la même migration comme `IndexCleanup` :

- `IX_entity_links_Provider_Value ON entity_links (Provider, Value)`, pour retrouver les
  personnes liées à un ID Discord ;
- `IX_users_UserHandle_NoCase ON users (UserHandle COLLATE NOCASE)` ;
- `IX_user_handle_history_UserHandle_NoCase ON user_handle_history (UserHandle COLLATE NOCASE)`.
- Le `Down` fait `DROP INDEX IF EXISTS`.
- Les créations sont chronométrées sur la copie de production, sur le modèle de `deploy/README.md`.

**Liens `discord`** : à partir de maintenant, un nouveau lien `discord` (`LinksController` et
§ 10.1) doit être un snowflake. Les anciennes valeurs ne sont pas touchées. Un même ID Discord
peut être lié à plusieurs personnes (comptes RSI secondaires), et une personne à plusieurs IDs.

Les entités, configurations, repositories (`IDiscord…Repository`) et leur enregistrement suivent
les conventions existantes : `src/Collector/Models`, `Data/Configurations`,
`Data/Repositories`, `AddCollectorDataServices`.

## 9. Ingestion et historique

### 9.1 Déroulement

1. **Filtre de ressource `DiscordIngestGateAttribute`** (`IAsyncResourceFilter`). Il s'exécute
   après l'authentification, le limiteur et l'autorisation, et **avant** la liaison du corps.
   - Il valide `{guildId}` (snowflake).
   - Il refuse un serveur exclu (`discord_guild_optouts`) : 409 `guild_excluded`.
   - Il prend le **verrou d'écriture Discord** (§ 9.2), avec une attente de 10 s au plus, sinon
     503 et `Retry-After: 30`.
   - Il libère le verrou dans un `finally`, après l'action.
2. Liaison et validation du corps (§ 7.3), complétude sur le message brut.
3. **Ordre des envois.** On calcule `CollectedAt = ReceivedAt − collectionDurationMs`. Si un
   envoi déjà accepté pour ce serveur a un `ReceivedAt` postérieur à ce `CollectedAt`, la requête
   est refusée : 409 `stale_sync` (« Un envoi plus récent a été reçu pendant ta collecte »), rien
   n'est écrit.
4. Lectures : serveur, rôles, membres (actifs et partis), comptes concernés, opt-outs.
5. Filtrage des opt-outs. Ils sont comptés dans `OptedOutCount`, jamais stockés.
6. Calcul du diff en mémoire (§ 9.3), puis écritures en transactions bornées (§ 9.4).

### 9.2 Verrou d'écriture Discord

`DiscordWriteGate` est un singleton qui porte un `SemaphoreSlim(1)`. Il sérialise **toutes** les
écritures dans les tables `discord_*` :

- l'ingestion, étapes 3 à 6 ;
- `POST api/discord/links` et `POST api/discord/link-rejections` ;
- les effacements et les suppressions d'exclusions (§ 13.2) ;
- chaque lot de `DiscordRetentionService`, le verrou étant repris lot par lot.

`discord_accounts` est partagé entre serveurs : sans ce verrou, deux envois simultanés insèreraient
deux fois le même compte. L'API tourne en une seule instance et reçoit quelques envois par heure :
la sérialisation globale ne coûte rien. Le verrou ne remplace pas SQLite, qui reste le seul
arbitre face au collecteur.

### 9.3 Règles

**Base**

- Le premier envoi d'un serveur (aucune ligne `discord_guilds`) est une base.
- Le serveur, ses rôles et ses membres sont insérés **sans** événement de serveur.
- Les comptes sont insérés s'ils sont nouveaux, mis à jour sinon. Un compte déjà connu dont le
  nom change produit son `username_changed` ou `global_name_changed` comme d'habitude : il n'y a
  jamais de renommage silencieux.
- `FirstSyncAt` prend la valeur `CollectedAt`.

**Rôles**

- Ils sont toujours mis à jour : nom, position, couleur, `Hoist`, `Managed`, `LastSeenAt`.
- Un rôle absent reçoit `DeletedAt`, un rôle réapparu le perd.
- `IsRank`, `RankOrder` et `RsiRankLabel` ne sont jamais modifiés par un envoi. Seul un nouveau
  rôle reçoit `IsRank` par défaut, et `RankOrder = Position` s'il devient un rang.

**Comptes**

- Un changement de `Username` ou de `GlobalName` produit `username_changed` ou
  `global_name_changed` (`GuildId = null`).
- `LastSeenAt` avance.

**Membre présent dans l'envoi, déjà connu et actif**

- **Rôles** : on compare les anciens rôles, moins ceux qui sont supprimés (avant ou par cet
  envoi), aux nouveaux rôles. S'ils diffèrent : `roles_changed`, avec `NotBefore` = `LastSeenAt`
  précédent. Sinon, aucun événement, et `RoleIdsJson` est simplement remplacé.
- **Pseudo** différent → `nick_changed`.
- **JoinedAt** :
  - les deux valeurs connues et la nouvelle postérieure à l'ancienne de plus d'1 s → `rejoined`
    (`OccurredAt` = nouveau `JoinedAt`) : le membre est parti puis revenu entre deux envois ;
  - la nouvelle antérieure → ignorée ;
  - un `JoinedAt` nul reçu ne remplace jamais une valeur connue ; un `JoinedAt` stocké nul est
    complété, sans événement.

**Membre présent, connu mais parti** (`LeftAt` non nul)

- Si les deux `JoinedAt` sont connus et que le nouveau est antérieur ou égal à l'ancien, le départ
  était faux : `LeftAt = null`, le dernier événement `left` de ce membre est supprimé, et aucun
  `rejoined` n'est créé.
- Sinon : `LeftAt = null` et `rejoined`, avec `OccurredAt` = nouveau `JoinedAt` s'il est connu,
  sinon `NotBefore` = `LeftAt` précédent.

**Membre présent et inconnu**

- `JoinedAt` connu et ≥ `FirstSyncAt` → `joined` avec `OccurredAt = JoinedAt`.
- `JoinedAt` connu et < `FirstSyncAt` → insertion **sans événement** : il était là avant le début
  du suivi et avait été manqué, par exemple par un envoi partiel ou une base interrompue.
- `JoinedAt` nul → `joined` seulement si `LastCompleteSyncAt` n'est pas nul (`NotBefore =
  LastCompleteSyncAt`, `OccurredAt = null`), sinon insertion sans événement.

**Envoi complet uniquement : départs**

- Un membre actif absent de l'envoi reçoit `LeftAt = CollectedAt` et un événement `left`
  (`NotBefore` = `LastSeenAt` précédent, `ObservedAt = CollectedAt`).
- **Garde-fou.** Si l'envoi ferait partir plus de 25 % des membres actifs, avec au moins 10
  départs, il est traité comme **partiel** :
  - aucun départ ;
  - `DepartureGuardTripped = 1` dans `discord_syncs` et `departureGuardTripped: true` dans la
    réponse ;
  - un badge dans l'onglet des envois.
  - Un admin peut autoriser le **prochain** envoi complet de ce serveur à dépasser le seuil :
    `POST api/discord/guilds/{guildId}/allow-mass-departure` (AdminOnly), qui pose
    `AllowMassDepartureOnce`, remis à faux après usage et écrit dans `activity_logs`.

**Envoi partiel**

- Aucun départ n'est déduit.
- Les membres absents de l'envoi ne sont pas touchés.

**Idempotence** : renvoyer le même contenu ne produit aucun événement. Seuls les `LastSeenAt`
avancent.

**Serveur**

- Mise à jour du nom, de l'icône, de `MemberCount`, de `LastSyncAt` et de `LastCollectedAt`.
- Mise à jour de `LastCompleteSyncAt` si l'envoi est complet et n'a pas déclenché le garde-fou.

**Journal** : une ligne `discord_syncs` par envoi accepté.

**Bots** : ils sont stockés, et comptés dans la complétude puisqu'ils figurent dans le message.
Ils sont exclus des totaux, de la répartition par rang, des suggestions, des recoupements et de la
multi-appartenance, et affichés avec un badge BOT.

### 9.4 Écritures bornées

Aucune transaction n'écrit plus de 5 000 lignes, pour ne pas retenir longtemps le verrou d'écriture
SQLite que partage le collecteur (`busy_timeout` de 5 s, aucun nouvel essai de son côté).

1. La ligne `discord_guilds` est écrite en premier.
2. Les comptes et membres nouveaux sont insérés en transactions de 5 000 lignes au plus.
3. Une transaction courte écrit les événements, les membres modifiés, les rôles, le serveur et
   `discord_syncs`.
4. Après le commit, les `LastSeenAt` des membres et des comptes présents sont mis à jour par
   `ExecuteUpdate`, en lots de 5 000 : `SET LastSeenAt = max(LastSeenAt, @CollectedAt)`. Cette
   mise à jour est monotone et idempotente : une interruption ne fait que vieillir un `NotBefore`,
   qui reste une borne valide.

Une base interrompue après l'étape 1 est complétée sans faux événement par l'envoi suivant, grâce
aux règles « inconnu avec `JoinedAt` < `FirstSyncAt` » et « `JoinedAt` nul sans envoi complet
antérieur ».

`SQLITE_BUSY` malgré le `busy_timeout` : un nouvel essai de la transaction en cours au bout de
2 s, puis 503 avec `Retry-After: 30`. Les transactions déjà validées restent valides, et l'envoi
suivant complète le reste.

### 9.5 Rang d'un membre

Il est déduit à la lecture. Parmi les rôles du membre qui sont des rangs et ne sont pas supprimés,
on prend le premier dans l'ordre : `RankOrder` décroissant, puis `Position` décroissante, puis
`RoleId`. Changer la configuration des rangs réinterprète tout l'historique sans rien réécrire.
L'interface présente un `roles_changed` qui change le rang comme **« Rang : X → Y »**.

## 10. Liaison et recoupements

### 10.1 Suggestions de liens

Elles sont calculées à la lecture, pour les membres actifs non bots d'un serveur dont l'ID Discord
n'est lié à personne.

- **Chaînes candidates** : `nick`, `globalName`, `username`.
- **Normalisation** :
  1. retirer les segments entre `[]`, `()`, `{}` et `«»` ;
  2. découper en jetons sur tout caractère hors `[A-Za-z0-9_-]` ;
  3. garder les jetons de 3 à 60 caractères ;
  4. ajouter la chaîne entière sans espaces ni émojis.
- **Recherche** : par lots de 500, avec `UserHandle COLLATE NOCASE IN (…)`, ce qui utilise les
  index NOCASE (§ 8). Les handles RSI sont en ASCII, donc NOCASE y est exact.
- **Confiance** :
  - **forte** : le jeton égale le handle d'un membre actif (`organization_members.IsActive`) de
    l'org reliée au serveur ;
  - **moyenne** : le jeton égale un `users.UserHandle` ou un handle de `user_handle_history`.
- Une suggestion porte le **CitizenId** quand il est connu, et le **handle courant canonique**, lu
  en base sur la ligne qui a correspondu :
  - `organization_members` ;
  - `users` ;
  - ou `user_handle_history`, puis `users` par CitizenId.
- **Exclusions** : couples présents dans `discord_link_rejections`, et IDs déjà liés.
- Pour un serveur **non relié**, seule la confiance moyenne existe.

**Valider** : `POST api/discord/links { discordUserId, citizenId?, handle }`.

- Sous le verrou d'écriture, le serveur relit la personne sans tenir compte de la casse.
- Si un CitizenId est connu, il appelle `IEntityResolver.ResolveOrCreateAsync(citizenId, <handle
  courant de users>, displayName)`. Il n'utilise jamais le jeton ni un ancien handle, ce qui
  évite d'écraser `CurrentHandle` ou de créer une entité en double.
- Il crée ensuite l'`EntityLink` (fournisseur `discord`, valeur = ID Discord), avec l'utilisateur
  connecté comme auteur. L'opération est idempotente.
- Un handle introuvable dans `users`, `user_handle_history` et `organization_members` donne 404,
  et aucune entité n'est créée.

**Ignorer** : `POST api/discord/link-rejections`. Un rejet est supprimable par son auteur ou par
un admin.

### 10.2 Statuts de recoupement

Ils concernent un serveur relié à une corpo. Un membre actif non bot est **lié** s'il existe au
moins une `entity_links` `discord` avec son ID. Les rangs sont comparés sans tenir compte de la
casse, espaces de début et de fin retirés.

| Statut | Condition |
|---|---|
| `rsi_unknown` | l'org n'a aucune ligne active dans `organization_members` : roster jamais collecté |
| `unlinked` | aucune personne liée |
| `ok` | l'une des personnes liées est dans le roster RSI actif de l'org (par `CitizenId`, sinon par handle sans casse), avec un rang cohérent ou sans `RsiRankLabel` configuré |
| `rank_mismatch` | l'une des personnes liées est dans le roster, mais aucune avec un rang cohérent |
| `not_in_rsi_org` | aucune personne liée n'est dans le roster. Libellé : « absent de l'org RSI ou caché sur RSI » |

- Un ID lié à plusieurs personnes est signalé (`multipleLinks`).
- **Sens inverse (`rsi_only`)** : les membres du roster RSI actif dont aucun ID Discord lié n'est
  actif sur ce serveur. Ce calcul n'a lieu que si `LastCompleteSyncAt` n'est pas nul. Sinon la
  liste est remplacée par « Aucun envoi complet : absences inconnues ».
- **Totaux** :
  - membres Discord actifs non bots, et nombre de liés ;
  - côté RSI, la dernière ligne `org_member_counts` de l'org, par `CollectedAt` : visibles,
    masqués et cachés, avec sa date. Si `VisibleCount` est nul (dernière lecture incomplète), on
    affiche `TotalRows` seul avec la mention « répartition inconnue », sans jamais se rabattre sur
    une ligne plus ancienne.
- Pour un serveur **non relié** : `discrepancies` renvoie `200 { orgSid: null, items: [], totals:
  null }`, le statut des membres vaut `null` et le filtre `reconciliation` est ignoré.

### 10.3 Multi-appartenance

- Les comptes non bots actifs dans **au moins deux** serveurs suivis, avec pour chacun le serveur,
  la corpo reliée et le rang.
- Si le compte est lié : l'union des orgs RSI actives de toutes les personnes liées. On peut ainsi
  repérer un membre RSI de l'org X présent sur le Discord de l'org Y.

### 10.4 Profil croisé (fiche citoyen)

- Les comptes Discord liés à la personne, et pour chacun les serveurs où il est ou était présent :
  corpo, rang actuel, arrivée, départ.
- Une **frise combinée** d'au plus 100 entrées, triée par date décroissante :
  - **Partie RSI.** L'entité liée donne le CitizenId ; sans CitizenId, on prend `CurrentHandle`
    seul. Pour chaque handle de ce CitizenId dans `user_handle_history`, handle courant compris,
    on lit les `change_events` dont `UserHandle` vaut ce handle.
    - Pour un handle ancien, on ne garde que les événements antérieurs au `FirstSeen` du handle
      suivant : au-delà, le handle a pu être repris par un autre citoyen.
    - Types retenus : `member_joined`, `member_left`, `rank_changed`, `roles_changed`,
      `handle_changed`.
  - **Partie Discord** : les `discord_member_events` de ses comptes liés.
  - Chaque entrée est marquée **RSI** ou **Discord**.

## 11. API de lecture et d'édition

- Toutes ces routes passent par le schéma `Smart` avec `[Authorize]`, donc une clé limitée y est
  refusée (401).
- Les listes longues sont paginées (`PaginatedResponse`, bornes `Paging`) et les flux sont
  plafonnés par `limit`.
- **Responsable d'un serveur** : l'utilisateur qui l'a relié à une corpo (`OrgMappedByApiUserId`).
  - Tant qu'un serveur n'est pas relié, tout utilisateur connecté peut le relier et configurer ses
    rangs.
  - Une fois relié, seuls le responsable et les admins peuvent changer la corpo ou les rangs
    (403 sinon), sur le modèle de `citizen-id` et des appartenances.
  - Ces modifications sont tracées dans `activity_logs` (`discord_map_org`, `discord_update_role`,
    avec l'ID du serveur ou `guildId:roleId`).

| Méthode et route | Rôle |
|---|---|
| `GET api/discord/ingest-config` | URL publique et empreinte à saisir dans le plugin (§ 6.4) |
| `GET api/discord/guilds` | serveurs : corpo, actifs, répartition par rang, dernier envoi |
| `GET api/discord/guilds/{guildId}` | détail et configuration (rôles avec `IsRank`, `RankOrder`, `RsiRankLabel`) |
| `GET api/discord/guilds/{guildId}/members?status=active\|former\|all&search=&rankRoleId=&reconciliation=&page=&pageSize=` | membres avec rang, liens et statut de recoupement |
| `GET api/discord/guilds/{guildId}/events?type=&userId=&limit=` | historique du serveur, avec l'auteur de l'envoi source |
| `GET api/discord/guilds/{guildId}/discrepancies` | écarts RSI et totaux (§ 10.2) |
| `GET api/discord/guilds/{guildId}/suggestions` | suggestions de liens (§ 10.1) |
| `GET api/discord/guilds/{guildId}/syncs?limit=` | journal des envois |
| `PUT api/discord/guilds/{guildId}/org` `{ orgSid \| null }` | relier la corpo : SID connu dans `organizations`, sinon 400 |
| `PUT api/discord/guilds/{guildId}/roles/{roleId}` `{ isRank, rankOrder, rsiRankLabel }` | configurer un rang |
| `POST api/discord/guilds/{guildId}/allow-mass-departure` (**AdminOnly**) | lever le garde-fou pour le prochain envoi complet |
| `POST api/discord/links` `{ discordUserId, citizenId?, handle }` | valider une suggestion |
| `POST api/discord/link-rejections` `{ discordUserId, citizenId?, handle }`, `DELETE api/discord/link-rejections/{id}` | ignorer une suggestion, ou annuler ce rejet |
| `GET api/discord/multi?page=&pageSize=` | multi-appartenance |
| `GET api/users/{handle}/discord` | profil croisé (§ 10.4) |
| `GET api/organizations/{sid}/discord` | serveurs reliés à la corpo : liste, vide si aucun |
| `DELETE api/discord/accounts/{discordUserId}` (**AdminOnly**) | effacer et exclure un compte (§ 13.2) |
| `DELETE api/discord/guilds/{guildId}?exclude=true\|false` (**AdminOnly**) | supprimer un serveur, en l'excluant ou non (§ 13.2) |
| `GET api/discord/optouts`, `DELETE api/discord/optouts/{discordUserId}` (**AdminOnly**) | exclusions de comptes |
| `GET api/discord/guild-optouts`, `DELETE api/discord/guild-optouts/{guildId}` (**AdminOnly**) | exclusions de serveurs |

Contrôleurs :

- `DiscordIngestController` : `[Route("api/ingest/discord")]`,
  `[Authorize(Policy = "DiscordIngest")]`, `[EnableRateLimiting("discord-ingest")]`,
  `[DiscordIngestGate]`, `[RequestSizeLimit]`.
- `DiscordRostersController` : `[Route("api")]`, sous-chemins complets, comme `DiscordController`.

Les DTOs vont dans `Dtos/Discord/DiscordDtos.cs` (records `*Request`, classes `*Dto`).

## 12. Interface web

**Onglet `DISCORD`**, ajouté à `NAV_ITEMS` (`components/layout/TopNav.tsx`).

**`/discord`** (groupe `(public)`, donc `requireAuthCtx()`) :

- en tête, les serveurs **non reliés**, avec un sélecteur de SID (recherche d'org existante) ;
- un tableau `HudDataGrid` : icône, nom, corpo (lien `/orgs/[sid]`), actifs, rangs, dernier envoi
  (badge complet ou partiel, auteur, date) ;
- l'icône se construit uniquement à partir de `GuildId` et `IconHash` validés, avec
  `encodeURIComponent` sur chaque segment ;
- si aucun serveur n'a encore été reçu : « Aucun serveur reçu. Installe le plugin : Paramètres →
  Clé d'envoi Discord ».

**`/discord/[guildId]`**, avec des onglets par paramètre d'URL `?tab=` :

- `members` : tableau paginé côté serveur (nom, pseudo, rang, handles liés vers `/users/[handle]`,
  rang RSI, statut, badge BOT) avec filtres ;
- `history` : flux des événements, avec des badges `joined` vert, `left` rouge et les autres
  orange, « Rang : X → Y », fenêtre `NotBefore`–`ObservedAt` quand la date exacte est inconnue, et
  auteur de l'envoi ;
- `gaps` : écarts RSI et totaux (§ 10.2) ;
- `suggestions` : Valider ou Ignorer ;
  - pour un serveur non relié, `gaps` et `suggestions` affichent aussi « Relie d'abord ce serveur
    à une corpo (onglet config) ».
- `config` :
  - corpo reliée ;
  - tableau des rôles : case rang, ordre, rang RSI équivalent choisi parmi les `Rank` RSI connus
    de l'org ;
  - modifiable par le responsable ou un admin une fois le serveur relié.
- `syncs` : journal des envois, badges « garde-fou » et « partiel » ;
- réservé aux admins :
  - « Autoriser un départ massif » ;
  - « Supprimer et exclure ce serveur » et « Supprimer et repartir d'une base » ;
  - sur chaque membre, « Supprimer et exclure ce compte ».

**Autres pages et sections**

- **`/discord/multi`** : multi-appartenance.
- **Fiche citoyen `/users/[handle]`** : section **« DISCORD · SERVEURS »** (§ 10.4), distincte du
  `DiscordPanel` existant.
- **Page corpo `/orgs/[sid]`** : panneau « DISCORD » qui liste les serveurs reliés. Il est masqué
  si la liste est vide.
- **Paramètres** : panneau « Clé d'envoi Discord » (§ 6.4).

**Conventions**

- server components `force-dynamic` ;
- fonctions typées dans `lib/api/endpoints.ts` et types dans `lib/api/types.ts` ;
- mutations par server actions (`getSession`, zod via `lib/validation.ts` avec un nouveau
  `snowflakeSchema`, puis `{ ok, error }`) ;
- aucun import du client API dans un composant client ;
- textes de l'interface en français.

## 13. RGPD

### 13.1 Minimisation

Seuls les champs du § 7.1 sont stockés. Rien d'autre n'est collecté : ni avatar, ni Nitro, ni
invitations, ni présence, ni permissions.

### 13.2 Effacement et opposition

Ces actions sont réservées aux admins, passent par le verrou d'écriture et sont livrées dès le
lot A.

**`DELETE api/discord/accounts/{id}`** :

- supprime `discord_members`, `discord_member_events`, `discord_accounts` et
  `discord_link_rejections` de ce compte, ainsi que ses `entity_links` du fournisseur `discord` ;
- ajoute l'ID à `discord_optouts`, pour que les envois suivants l'ignorent dès la réception.

**`DELETE api/discord/guilds/{id}?exclude=true|false`** :

- supprime le serveur, ses rôles, ses membres, ses événements et ses envois ;
- supprime aussi, dans la même opération, les comptes **non liés** qui n'ont plus aucune ligne
  `discord_members`, avec leurs événements et leurs rejets ;
- avec `exclude=true` (« Supprimer et exclure », en cas de retrait de l'accord), le serveur est
  ajouté à `discord_guild_optouts`, et tout envoi suivant reçoit 409 `guild_excluded` ;
- avec `exclude=false` (« Supprimer et repartir d'une base »), l'envoi suivant est une nouvelle
  base.

**Journal** : les actions écrivent dans `activity_logs` **après** la validation des
transactions tracker.db, avec `userId = UserId > 0 ? UserId : null`, parce que la clé admin
statique n'a pas de ligne dans `api_users`. Si l'écriture du journal échoue, l'erreur est
journalisée sans changer la réponse.

### 13.3 Conservation

`DiscordRetentionService` (`BackgroundService` de l'API) passe une fois par jour. Il travaille par
lots de 500, sous le verrou d'écriture repris à chaque lot, et relit `entity_links` juste avant de
supprimer.

- **Journal des envois** : les lignes `discord_syncs` plus vieilles que
  `Discord:Retention:SyncLogDays` (365 par défaut) sont supprimées.
- **Comptes** : un compte **non lié** est purgé, avec ses membres, événements et rejets, dans
  deux cas (`Discord:Retention:DepartedAccountDays`, 730 par défaut) :
  - chacune de ses lignes `discord_members` vérifie `coalesce(LeftAt, LastSeenAt) < maintenant −
    DepartedAccountDays` ;
  - il n'a aucune ligne `discord_members` et son `LastSeenAt` est plus ancien que ce délai.
- Le second cas couvre les serveurs uniquement partiels et ceux que plus personne n'envoie.

### 13.4 Information

`vencord/README.md` rappelle trois choses :

- il faut l'accord des admins de chaque serveur suivi ;
- les CGU Discord interdisent la collecte automatisée : le risque porte sur le compte de celui
  qui envoie ;
- un **modèle d'avis** (article 14) est fourni, à publier sur le serveur : qui collecte, quoi,
  pourquoi, combien de temps (§ 13.3), et comment demander l'effacement.

## 14. Erreurs

### 14.1 Plugin (toasts en français)

| Cas | Message et comportement |
|---|---|
| `pin_mismatch` | « Certificat inattendu : vérifie l'empreinte (voir Paramètres → Clé d'envoi Discord sur le site) ». Rien n'est envoyé. |
| 401 | « Clé invalide, révoquée ou expirée ». |
| 403 | « Accès refusé ». |
| 400 | « Données refusées » + détail ProblemDetails. |
| 409 `stale_sync` | « Un envoi plus récent a été reçu pendant ta collecte ». Pas de nouvel essai. |
| 409 `guild_excluded` | « Ce serveur est exclu du suivi ». |
| 413 (ou corps trop gros avant envoi) | « Serveur trop grand pour un envoi ». |
| 429 | « Trop d'envois, réessaie dans N s » (`Retry-After`). Sans cet en-tête (429 de nginx) : « réessaie dans quelques minutes ». |
| 503 | « Tracker occupé, réessaie dans N s ». |
| 500 ou statut inattendu | « Erreur du tracker, réessaie plus tard ». |
| `network` | « Tracker injoignable ». |
| `no_response_after_upload` | « Pas de réponse du tracker : l'envoi a peut-être été enregistré. Un renvoi ne crée aucun doublon d'événement ». |
| Discord 403 sur la recherche de membres | bascule sur `role-members` |
| Discord 202 (index pas prêt) | 3 essais, puis bascule |
| Discord 429 | attente de `retry_after` |
| Lot gateway sans réponse en 10 s | IDs retirés, envoi partiel |
| Plafond de 200 appels atteint | envoi partiel |
| « Arrêter » | rien n'est envoyé |

### 14.2 API

| Cas | Réponse |
|---|---|
| Validation | 400 ProblemDetails (`ValidationException`) |
| Serveur ou rôle inconnu, en lecture ou en édition | 404 |
| SID inconnu lors du rattachement | 400 |
| Envoi obsolète | 409 `stale_sync` |
| Serveur exclu | 409 `guild_excluded` |
| Corps trop gros | 413 |
| Verrou indisponible au bout de 10 s, ou SQLite occupée après un nouvel essai | 503 avec `Retry-After` |

Toute autre erreur passe par `ExceptionHandlingMiddleware`.

## 15. Tests

**Collector.Tests**

- `MigrationsTests` :
  - la chaîne complète s'applique ;
  - le script de `AddDiscordRosters`, généré par
    `IMigrator.GenerateScript("20260927013122_AddUserParserVersion", "AddDiscordRosters")`, ne
    contient ni `ef_temp_`, ni `DROP TABLE`, ni `RENAME TO`.
- `IndexSetTests` : nouveaux index, NOCASE compris.

**Collector.Api.Tests** (`ApiFactory`)

- **Clés limitées** :
  - création avec et sans `Scope` ; portée invalide → 400 ; expiration absente, passée ou à plus
    de 365 jours → 400 ;
  - `ApiKeyDto` expose `Scope` ;
  - le préfixe ne lève plus d'exception (boucle de créations).
- **Clés limitées, test exhaustif** dans `AuthorizationTests` : il parcourt toutes les routes
  (même énumération que le test anonyme) avec une clé `discord:ingest` et attend 401 partout,
  sauf `POST /api/ingest/discord/guilds/{guildId}/syncs` et la liste Anonymous.
- **Accès à l'ingestion** : un JWT, une clé complète, la clé admin statique, une clé révoquée ou
  expirée et un propriétaire banni → 401.
- **Ingestion** :
  - base : aucun événement de serveur, et renommage d'un compte déjà connu signalé ;
  - envoi complet :
    - arrivée (date = `JoinedAt`) ;
    - départ, retour, et faux départ corrigé sans `rejoined` ;
    - `JoinedAt` modifié ;
    - rôles, pseudo, nom ;
    - rôle supprimé : pas de `roles_changed` en rafale, et `unknownRoleRefs` compté ;
  - envoi partiel : jamais de départ ;
  - envoi identique : aucun événement ;
  - membre d'avant le suivi, manqué puis vu : pas d'arrivée ;
  - `JoinedAt` en `+00:00` puis en `+02:00`, et fuseau local du test hors UTC : aucun
    `rejoined` ;
  - opt-out ignoré, et l'envoi reste complet ;
  - `complete` déclaré mais nombres différents ou méthode autre que `member-search` → partiel ;
  - envoi complet vide → 400 ;
  - garde-fou (plus de 25 % et au moins 10 départs) : déclenchement, puis levée par un admin ;
  - envoi obsolète → 409 ;
  - serveur exclu → 409 ;
  - erreurs 400 (snowflake, `guildId` différent, doublon, icône invalide) ;
  - deux envois simultanés : le second attend, ou reçoit 503 ;
  - volume : un serveur de 50 000 membres, aucune transaction de plus de 5 000 lignes, et une
    base interrompue après le premier lot complétée sans événement ;
  - la politique `discord-ingest` renvoie 429 avec `Retry-After`.
- **413** : une fabrique dédiée avec `WebApplicationFactory.UseKestrel()` (TestServer n'applique
  pas `[RequestSizeLimit]`) envoie plus de 25 Mio et attend un 413 en ProblemDetails.
- **Lectures** :
  - rang déduit (`RankOrder`, `Position`, rôle supprimé) ;
  - chaque statut de recoupement, `rsi_unknown`, `multipleLinks`, `rsi_only` sans envoi complet ;
  - serveur non relié ;
  - totaux RSI avec `VisibleCount` nul ;
  - multi-appartenance ;
  - frise combinée avec un handle ancien repris par un autre citoyen.
- **Liaison** :
  - normalisation (balises, jetons, casse) ;
  - validation d'une suggestion issue d'un ancien handle : lien créé sur l'entité du CitizenId,
    `CurrentHandle` inchangé ;
  - handle introuvable → 404 ;
  - rejet puis annulation du rejet.
- **Droits** : un autre utilisateur reçoit 403 en voulant re-relier un serveur déjà relié ou
  modifier ses rangs.
- **RGPD** :
  - effacement d'un compte ;
  - suppression d'un serveur, avec et sans exclusion, et comptes orphelins supprimés ;
  - l'opt-out bloque les envois suivants ;
  - un effacement pendant une ingestion ne recrée pas le compte ;
  - `DiscordRetentionService` purge ce qu'il faut et rien d'autre (comptes liés conservés).

**Web (vitest)**

- `snowflakeSchema` et autres schémas.
- Arguments invalides des nouvelles server actions → `INVALID_ARGUMENTS` sans appel API.
- Chemins des nouveaux `endpoints`.

**Plugin (vitest, `vencord/tests/`)**

- `payload` : IDs en texte, aucune permission, champs minimisés, bornes, refus au-delà de 25 Mio.
- `coverage` : seule `member-search` peut être complète.
- `pacing` : espacement, plafond, annulation.
- `fingerprint` : formats `openssl`, minuscules, sans `:`, invalide.
- `errors` : traduction de chaque statut et de chaque erreur.
- `pinnedPost`, contre un serveur HTTPS local qui utilise le certificat de test de
  `vencord/tests/fixtures/` :
  - bonne empreinte → le serveur reçoit en-tête et corps ;
  - mauvaise empreinte → le serveur ne reçoit **ni** `x-api-key` **ni** corps, et l'erreur vaut
    `pin_mismatch`.

**CI : nouveau job `vencord`**

1. Node 22, `pnpm/action-setup` avec `package_json_file: vencord/package.json`.
2. Dans `vencord/` : `pnpm install --frozen-lockfile`, `tsc --noEmit`, `vitest run`.
3. Clonage de Vencord au commit `VENCORD_REF`, `pnpm install --frozen-lockfile`, puis copie du
   plugin par `install.mjs`.
4. `pnpm build` **et** `pnpm testTsc` : la vérification des types contrôle les appels aux stores
   et à `PluginNative` contre `@vencord/discord-types`.

**Recette manuelle** : liste de contrôle dans `vencord/README.md`.

1. Créer une clé d'envoi et configurer le plugin depuis le panneau du site.
2. Suivre et envoyer un petit serveur, en complet puis en partiel.
3. Sur un serveur de plus de 1000 membres avec accès à la recherche de membres, vérifier que les
   pages successives ne se recouvrent pas et que leur union atteint `total_result_count`.
4. Vérifier l'onglet, l'historique et une suggestion.
5. Tester une mauvaise empreinte.

## 16. Déploiement et exploitation

- La nouvelle migration tracker.db impose `deploy.sh <sha> --collector`. Tant que le collecteur
  n'a pas migré, `/api/health/ready` renvoie 503.
- La migration api.db (`Scope`) s'applique au démarrage de l'API.
- **nginx** : ajout manuel des zones et de la location (§ 6.1), puis `nginx -t` et `reload`,
  seulement une fois le lot A livré en entier.
- **`api.env`**, avec le préfixe `COLLECTOR_API_` des réglages de l'API :
  - `COLLECTOR_API_Discord__Ingest__PublicUrl` et
    `COLLECTOR_API_Discord__Ingest__CertificateSha256`, remplis par l'étape du
    `deploy/README.md` ;
  - réglages optionnels : `COLLECTOR_API_Api__RateLimit__DiscordIngest__*` et
    `COLLECTOR_API_Discord__Retention__*`.
- **Certificat régénéré** : mettre à jour `COLLECTOR_API_Discord__Ingest__CertificateSha256` et
  redémarrer l'API. Les émetteurs recopient l'empreinte depuis le panneau du site.

**À mettre à jour dans le dépôt**

- `src/Collector.Web/tests/e2e/smoke.spec.ts` : ajouter `"/discord"` et `"/discord/multi"` à
  `PRIVATE_PAGES`.
- `README.md` racine :
  - schéma d'architecture ;
  - section Sécurité (une seule route publique vers l'API, `/ingest/discord/`, clé
    `discord:ingest`) ;
  - Dépôt (`vencord/`) ;
  - CI (job `vencord`) ;
  - tableau des variables.
- `src/Collector.Web/README.md` : nouvelles pages, et ligne `/settings` « Mot de passe, jeton
  Discord, clé d'envoi Discord ».
- `deploy/README.md` : nginx, empreinte, variables `Discord__Ingest__*`.
- `deploy/env/api.env.example` : nouvelles variables commentées.

## 17. Découpage en lots

| Lot | Contenu | Dépend de |
|---|---|---|
| A — socle serveur | clés limitées (api.db, schéma `DiscordIngestKey`, politique, correctif du préfixe, panneau Paramètres, `ingest-config`) ; migration tracker.db (tables, index) ; `DiscordWriteGate`, filtre, `DiscordIngestService` et contrôleur ; garde-fou ; effacements et exclusions (§ 13.2) ; nginx, README et variables ; tests | — |
| B — plugin | `vencord/` complet, tests, job CI, README (installation, recette, avis RGPD) | contrat du § 7 (lot A) |
| C — lecture, liaison, UI | API de lecture et d'édition ; suggestions, recoupements, multi, profil croisé ; pages web ; `DiscordRetentionService` ; tests | A |

- B et C peuvent avancer en parallèle une fois A livré.
- La location nginx n'est ouverte en production qu'avec le lot A complet.
- Les boutons d'effacement arrivent avec les pages du lot C. D'ici là, un admin appelle les routes
  du § 13.2 depuis le VPS, en boucle locale, avec la clé admin statique.
- `DiscordRetentionService` peut attendre le lot C : sa première purge n'intervient au plus tôt
  que 365 jours après le premier envoi.

## 18. Risques résiduels

- **CGU Discord.** Le risque de sanction du compte qui envoie est réduit (clic manuel, appels de
  l'interface officielle, rythme lent) mais pas nul. Il est assumé par l'utilisateur.
- **Fragilité de Vencord.** Stores, constantes d'endpoints et noms d'événements Flux peuvent
  changer à une mise à jour de Discord. Le job CI vérifie les types contre une version figée, pas
  contre le client du jour. Le plugin doit échouer proprement : erreur affichée, rien d'envoyé.
- **Droits de la recherche de membres.** La documentation communautaire annonce `MANAGE_GUILD`,
  le client officiel accepte d'autres droits de modération. Le plugin tente, et se rabat sur les
  rôles en cas de 403.
- **Serveurs sans droits de modération.** Au plus 100 membres par rôle, plus le cache rafraîchi.
  Aucun départ n'y est jamais déduit, et `rsi_only` n'y est pas calculé.
- **Envois malveillants.** Un détenteur de clé peut encore fausser des pseudos ou des rôles par
  des envois partiels. C'est limité par l'expiration des clés, le garde-fou contre les départs
  massifs, l'auteur affiché sur chaque événement et la suppression d'un serveur. Il n'y a pas
  d'annulation fine (§ 3).
- **Certificat.** Un certificat régénéré bloque tous les envois jusqu'à la mise à jour de
  l'empreinte. C'est voulu.
