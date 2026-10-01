# Plugin Vencord ScTracker — analyse et reprise

Analyse du 30 septembre 2026, sur la branche `lot-14-discord-roster`, HEAD `b6973ba`.

La fonctionnalité est préparée dans les documents, mais son implémentation n'a pas commencé dans les sources. La reprise doit livrer le socle d'ingestion du lot A et compléter le plan du lot B avant qu'un plugin utilisable puisse envoyer des rosters. Les bibliothèques pures du plugin peuvent être développées en parallèle du serveur avec des fixtures du contrat.

## 1. État vérifié

| Élément | État dans le dépôt | Preuve |
|---|---|---|
| Design | Présent et commité, encore marqué « proposé, en attente de relecture » | `docs/superpowers/specs/2026-09-30-discord-vencord-roster-design.md:4` |
| Contrats partagés | Présents, non suivis par Git au début de l'analyse | `docs/superpowers/plans/2026-09-30-discord-roster-contracts.md` |
| Plans serveur | A1–A14 présents, dont A6a/A6b | `docs/superpowers/plans/parts/lot-a-s1-auth.md`, `lot-a-s2-data.md`, `lot-a-s3-ingest.md`, `lot-a-s4-web-ops.md` |
| Plan plugin | Seulement B1–B5 : package/installateur, empreinte/TLS, payload, couverture, rythme, erreurs, curseur et suivi des chunks | `docs/superpowers/plans/parts/lot-b-s1-lib.md:1`, `:410`, `:852`, `:1487`, `:2165` |
| Plans lecture/site | C1–C10 présents | `docs/superpowers/plans/parts/lot-c-s1-api-read.md`, `lot-c-s2-api-links.md`, `lot-c-s3-web.md` |
| Points d'entrée des plans | Les trois fichiers `…-lot-a-server.md`, `…-lot-b-plugin.md`, `…-lot-c-read-ui.md` annoncés par les contrats sont absents | `docs/superpowers/plans/2026-09-30-discord-roster-contracts.md:4` |
| Sources plugin | Aucun dossier racine `vencord/`, aucun plugin à installer | Inventaire du dépôt et `git ls-files` |
| Stockage roster | Aucun modèle, DbSet, repository ou migration Discord | `src/Collector/Data/TrackerDbContext.cs:11` |
| Authentification dédiée | Pas de `Scope`, de schéma `DiscordIngestKey` ni de politique dédiée | `src/Collector.Api/Models/ApiKey.cs:3`, `src/Collector.Api/Dtos/ApiKeys/ApiKeyDtos.cs:5`, `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs:45` |
| Ingestion | Aucun `DiscordIngestController`, diff ou verrou Discord | Inventaire des sources API/collector |
| Configuration et clés sur le site | Panneau de gestion des clés encore provisoire | `src/Collector.Web/src/app/(user)/settings/page.tsx:56` |
| Lecture et navigation | Pas de pages `/discord`, ni d'onglet DISCORD | `src/Collector.Web/src/components/layout/TopNav.tsx:6` |
| Proxy et CI | Pas de location d'ingestion ni de job Vencord | `deploy/nginx/sc-tracker.conf:54`, `.github/workflows/ci.yml:15` |

Le Discord déjà présent sert à résoudre un profil via un bot et à afficher les comptes liés manuellement. Il ne collecte aucun roster (`src/Collector.Api/Controllers/DiscordController.cs:8`). Cette fonction doit continuer à coexister avec les nouvelles pages et sections.

Les sections « décisions enregistrées » des contrats décrivent du travail planifié. Elles ne constituent pas une preuve que les tâches correspondantes ont été exécutées. Les nombres de tests « Expected » du plan B sont également des attentes, pas des résultats obtenus sur ce dépôt.

## 2. Corrections à intégrer avant la reprise

### A. La portée des clés doit être implémentée avant le panneau et l'envoi réel

Aujourd'hui, `CreateApiKeyRequest` n'a pas de champ `Scope`. Ajouter seulement le panneau et envoyer `scope: "discord:ingest"` ne rendrait pas la clé limitée : l'API actuelle peut créer une clé complète, et son gestionnaire lui donne les droits de son propriétaire (`ApiKeyAuthHandler.cs:62`).

Appliquer ensemble A1/A2 : modèle et migration `Scope`, expiration bornée, validation retournant propriétaire et clé, routage de la zone d'ingestion vers le schéma dédié, refus des clés limitées sur les autres routes. Les tests exhaustifs d'autorisation sont un critère de livraison de ce socle.

Le correctif du préfixe aléatoire appartient à cette même étape : `ApiKeyService.cs:21` supprime `+` et `/` avant de prendre six caractères, et peut obtenir une chaîne trop courte.

### B. L'abonnement aux chunks ne prouve pas leur corrélation avec la requête

Le plan B5 propose de s'abonner juste avant le dispatch pour écarter les réponses anciennes (`lot-b-s1-lib.md:2187`). Le tracker proposé ne vérifie que le serveur et les IDs (`:2487`, `:2500`). Une réponse tardive d'une requête antérieure, pour le même serveur et le même ID, peut donc résoudre le nouveau lot.

Discord prévoit un `nonce` de requête, repris dans les chunks, de 32 octets au plus. Cela fournit une piste de corrélation, mais il reste à vérifier que le chemin Flux du client conserve ce champ. La documentation gateway ne démontre pas la forme de l'événement Flux interne. [Source Discord](https://docs.discord.com/developers/events/gateway-events#request-guild-members).

Prévoir une vérification d'intégration avant d'écrire `refresh.ts` : enveloppe réelle de `GUILD_MEMBERS_CHUNK_BATCH`, représentation des membres, champ des absents et passage du nonce. Tester deux requêtes successives avec une réponse tardive du premier lot. Tant que la corrélation n'est pas établie, ne pas considérer la simple réception après abonnement comme une preuve de fraîcheur.

### C. Le budget de 200 appels ne borne pas la durée à 30 minutes

Le serveur refuse `collectionDurationMs > 1_800_000` (`spec:406`, `lot-a-s3-ingest.md:359`). Pourtant, 200 lots expirant après 10 secondes, séparés par 199 attentes de 30 secondes, représentent environ 133 minutes. Les attentes REST/429 peuvent aussi prolonger la collecte.

Ajouter une échéance de collecte, avec une marge avant 30 minutes, commune aux stratégies et aux pauses. À l'échéance, garder les membres réellement obtenus et produire un envoi partiel si la durée reste recevable. Si un appel non annulable revient après la borne, afficher une erreur et ne pas envoyer un payload invalide. Ne pas tronquer artificiellement la durée mesurée.

### D. La complétude exige aussi une fin de parcours normale

`computeCoverage` proposé utilise méthode + égalité des nombres (`lot-b-s1-lib.md:1248`). Il ne reçoit pas la raison d'arrêt. Avec 1 000 membres obtenus, un total annoncé de 1 000 et une deuxième page répétée, il rendrait `complete: true`, alors que le design exige un envoi partiel quand le curseur n'avance plus.

L'orchestration doit mémoriser la raison de fin du parcours et forcer `complete: false` après plafond, échéance, curseur bloqué/invalide ou réponse inexploitable. Le nombre égal au total est nécessaire, mais ne suffit pas. Le serveur prend bien en compte le booléen déclaré avant de recalculer la couverture (`lot-a-s3-ingest.md:605`). Ajouter un test où les nombres sont égaux malgré une interruption.

### E. L'annulation doit bloquer l'envoi même si REST continue

Le contrat REST Vencord inspecté ne propose pas de `AbortSignal`. Il est donc insuffisant d'annuler les pauses du pacer. Vérifier l'état d'annulation après chaque réponse, pendant l'attente des chunks, après lecture de DataStore et immédiatement avant l'appel IPC. Libérer abonnements, timers et état « occupé » dans un `finally`, y compris à l'arrêt du plugin.

La garantie prévue porte sur la collecte : « Arrêter » avant l'upload doit entraîner zéro appel natif. Une fois l'upload commencé, l'interface doit afficher une phase d'envoi distincte ; une annulation ne peut promettre d'effacer ce que le serveur a déjà reçu. Tester aussi une réponse REST tardive après clic sur Arrêter.

### F. Le build typé ne remplace pas une recette Discord

Les signatures de `PermissionStore.canAccessMemberSafetyPage`, `SortedGuildStore.getFlattenedGuildIds` et `GuildRoleStore.getSortedRoles` existent au commit de référence. En revanche, le REST, les endpoints et les événements Flux sont largement typés avec `any` ou des clés arbitraires. `testTsc` ne prouvera donc pas le nom d'un endpoint, la forme d'une réponse, ni la corrélation des chunks. [Types Vencord à la référence](https://github.com/Vendicated/Vencord/blob/7f0c10cc29fd789f2f4828ae3dc947623e837920/packages/discord-types/src/utils.d.ts#L102).

Prévoir des gardes à l'exécution et une recette sur un serveur de plus de 1 000 membres, avec et sans droits de recherche. Une forme de réponse inconnue doit produire une erreur explicite ou une couverture partielle maîtrisée, jamais un snapshot réputé complet.

### G. Les deux packages utilisent des versions différentes de pnpm

Le package plugin proposé est figé à `pnpm@10.27.0`. À la référence `7f0c10cc29fd789f2f4828ae3dc947623e837920`, Vencord utilise `pnpm@11.9.0`, déclare Node `>=22` et teste avec Node 24. Node 22 n'est donc pas déclaré incompatible, mais un unique pnpm 10 ne reproduit pas le build amont. [Package Vencord](https://github.com/Vendicated/Vencord/blob/7f0c10cc29fd789f2f4828ae3dc947623e837920/package.json#L83), [workflow amont](https://github.com/Vendicated/Vencord/blob/7f0c10cc29fd789f2f4828ae3dc947623e837920/.github/workflows/test.yml#L16).

Séparer les étapes ou les jobs : package local avec son gestionnaire figé ; checkout Vencord avec le sien, de préférence Node 24 pour reproduire sa CI. La référence possède un [run amont réussi](https://github.com/Vendicated/Vencord/actions/runs/36612771756). Cela ne valide pas encore notre plugin, qui n'existe pas.

### H. Le retour des arguments refusés par native.ts doit être défini

`validatePostArgs` produit une erreur française libre, alors que `postSync` doit retourner un `PostResult` dont `error` accepte seulement `pin_mismatch`, `network` et `no_response_after_upload` (`contrats:705`, `:739`, `:752`). Le contrat ne précise pas la traduction entre les deux.

Fixer ce cas avant B7, par exemple une réponse locale de statut 400 avec un corps ProblemDetails contenant le détail de validation. Tester ce retour sans requête réseau et vérifier que le renderer affiche la cause réelle, plutôt qu'une erreur de réseau trompeuse.

## 3. Points serveur et transport à vérifier pendant l'implémentation

- **Bases anciennes sans historique EF** : `DatabaseBootstrap.cs:63` inscrit toutes les migrations compilées comme appliquées lors de l'adoption. Ajouter de nouvelles migrations sans traiter ce cas pourrait laisser tables Discord ou colonne `Scope` absentes malgré un historique déclarant leur présence. Tester l'adoption d'une base ancienne ou confirmer que les bases ciblées possèdent déjà un historique valide. La readiness actuelle vérifie les migrations pendantes, pas toutes les tables (`HealthController.cs:66`).
- **SQLite partagé** : le verrou Discord dans l'API ne bloque pas les écritures du collector. Conserver les transactions courtes et les reprises sur SQLite occupé prévues par A7/A11.
- **TLS** : conserver le contrôle d'empreinte avant tout `write/end`, sur un socket neuf, et les tests qui prouvent l'absence d'en-tête et de corps en cas de mauvais certificat. Le code proposé ajoute seulement un délai d'inactivité ; il ne borne pas à lui seul connexion et durée totale. Node applique `request.setTimeout` après connexion du socket. [Documentation Node](https://nodejs.org/api/http.html#requestsettimeouttimeout-callback).
- **Réponse du tracker** : le code B2 proposé accumule les buffers sans borne (`lot-b-s1-lib.md:778`). Ajouter une petite borne de réponse et une échéance totale pour empêcher une réponse continue de retenir indéfiniment mémoire et état d'envoi. Ce durcissement est distinct du contrôle d'empreinte.
- **Retour arrière** : `deploy.sh` et `rollback.sh` ne gèrent pas l'application ou le retrait de la configuration nginx. La recette doit contrôler le proxy séparément. Ouvrir `/ingest/discord/` seulement après livraison du lot A complet.

## 4. Ordre de reprise recommandé

| Étape | Travail concret | Critère de fin |
|---|---|---|
| 1 | Ajouter des points d'entrée aux plans A/B/C et y relier les fragments existants ; intégrer les corrections ci-dessus au contrat et aux plans concernés | Une entrée claire par lot, dépendances et état réel identifiables |
| 2 | Implémenter A1–A4 ; en parallèle, B1–B5 avec les corrections de suivi/couverture | Clés réellement limitées et tests d'autorisation ; modules plugin testés et typés |
| 3 | Implémenter A5–A12 : schéma, diff, repositories, verrou, contrôleur, garde-fou, effacements | Ingestion locale complète/partielle correcte, sans départ sur snapshot partiel |
| 4 | Livrer A13–A14 : configuration, panneau de clés et déploiement | Clé créée/révoquée depuis le site, migrations vérifiées, proxy opérationnel |
| 5 | Compléter puis implémenter l'intégration du plugin décrite ci-dessous | Plugin installable, envoi manuel et annulation vérifiés dans Discord |
| 6 | Réaliser C1–C10 en parallèle de l'intégration B, une fois A disponible | Pages, historique, liens, rangs, recoupements et rétention vérifiés |

Le lot C n'est pas nécessaire pour tester le transport et le stockage du plugin. Il est nécessaire pour le parcours utilisateur complet, notamment le lien de fin d'envoi vers `/discord` et le rattachement à une corpo.

## 5. Complément proposé au plan du lot B

Ces tâches ne figurent pas dans les fragments actuels. Les numéros suivants sont une proposition pour prolonger B1–B5.

| Tâche | Fichiers / résultat | Vérification principale |
|---|---|---|
| B6 — contrats du client et collecte | Adaptateur des réponses réelles ; `collect/memberSearch.ts`, `refresh.ts`, `roleMembers.ts`, `strategy.ts` | Recherche paginée, bascule 403/202, 429, nonce/chunks tardifs, absents, échéance, plafond partagé et complétude prudente |
| B7 — pont natif | `scTracker.desktop/native.ts`, validation via `postArgs`, POST épinglé | Arguments invalides sans connexion ; bon/mauvais certificat ; réponse bornée ; absence de retry automatique après upload ambigu |
| B8 — réglages et stockage | `settings.tsx`, accès DataStore, serveurs suivis et derniers résultats | Clé conservée hors settings/Cloud Sync, erreurs de stockage visibles, boutons et statuts cohérents |
| B9 — orchestration et menu | `index.tsx`, un envoi à la fois, lot séquentiel, progression et arrêt | Déclenchement uniquement manuel, pause interserveurs, politique stop/continue, zéro upload après annulation, nettoyage au stop |
| B10 — intégration et livraison | Job(s) CI Vencord, `README.md`, entrée du README racine, checklist de recette | Installation par copie, build natif/renderer, `testTsc` dans la référence figée, redémarrage Discord et recette réelle |

Garder les tests hors du dossier copié du plugin. Les imports de Node doivent rester dans le chemin natif : les modules renderer utilisent des imports de types pour `PostResult`, et n'importent jamais `pinnedPost` comme valeur.

## 6. Vérifications réalisées et limites

- Sources, plans, historique récent, état Git, configuration nginx et CI inspectés. Le seul fichier modifié par cette analyse est le présent rapport ; les plans existants ont été conservés.
- Front existant : `node node_modules/typescript/bin/tsc --noEmit` réussi ; `node node_modules/vitest/vitest.mjs run` réussi, **22 fichiers et 168 tests**.
- Les commandes usuelles `pnpm typecheck` et `pnpm test` se sont arrêtées avant les outils, le pnpm fourni (11.19.0) tentant de réinstaller les modules du package figé à 10.27.0. La vérification a donc utilisé directement les dépendances présentes, sous Node 24.19.0. Le magasin local temporaire créé par ces tentatives a été retiré.
- SDK .NET non trouvé dans l'environnement d'exécution : aucune compilation, migration ni suite .NET exécutée. Le build Next de production et les tests Playwright n'ont pas été relancés pour cet audit documentaire.
- Aucun test du plugin, build Vencord avec ScTracker, appel Discord authentifié, envoi de roster ou déploiement réalisé. Les extraits de code des plans ont été relus sur les frontières critiques, pas exécutés comme une implémentation existante.

La prochaine unité de travail concrète est A1/A2 pour l'authentification, avec B1 en parallèle pour créer le package du plugin. Avant B6, résoudre la corrélation gateway et fixer les garanties de fin de collecte dans le contrat.
