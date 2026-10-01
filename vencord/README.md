# Plugin ScTracker pour Vencord desktop

Le plugin ajoute un panneau de réglages et un menu contextuel des serveurs pour envoyer
leurs membres et rôles au SC Tracker, à la demande ou par un minuteur facultatif. Aucun
envoi immédiat n'est déclenché au démarrage. Un seul travail peut être actif, y compris
pendant son upload.

Le plugin n'a aucune base SQLite et ne conserve aucun roster ni historique. Il rassemble
les données du serveur demandé en mémoire et les transmet au tracker, qui les stocke et
crée les événements. Seuls les réglages de connexion, les serveurs cochés et les préférences
du minuteur sont mémorisés.

Le code renderer et natif est compilé contre le commit Vencord
`7f0c10cc29fd789f2f4828ae3dc947623e837920`, fixé dans `VENCORD_REF`.
Les tests utilisent des réponses Discord simulées et un serveur HTTPS local. Un premier
envoi réel par recherche de membres a été accepté dans le tracker local le 1er octobre 2026 :
168 comptes et 30 rôles, synchronisation complète reconnue par l'API. La recette reste à
compléter pour les fallbacks gateway, leur nonce et la pagination au-delà de 1 000 membres ;
les types amont décrivent en partie les enveloppes REST/Flux avec `any`.

## Installation et activation

Un checkout Vencord desktop au ref fixé, Node 24 et **pnpm 11.9.0** permettent de reproduire
le build amont. Vencord déclare Node ≥22 ; son
[CI de référence](https://github.com/Vendicated/Vencord/actions/runs/36612771756) utilise Node 24.

Depuis la racine de ce dépôt :

```sh
node vencord/install.mjs <chemin-du-checkout-Vencord>
```

Puis, depuis le checkout Vencord :

```sh
pnpm install --frozen-lockfile
pnpm build
pnpm testTsc
pnpm inject
```

Redémarrer complètement Discord, puis activer **ScTracker** dans les plugins Vencord.
L'injection est une étape manuelle d'installation dans le client ; les tests du dépôt ne
modifient pas une installation Discord. Après chaque modification, recopier le dossier puis
reconstruire. Un changement de `native.ts` exige un redémarrage complet du client.

Le script vérifie le package Vencord, les chemins résolus et l'absence de liens ou jonctions
sur `src`, `src/userplugins` et la destination. Il prépare une copie avant de remplacer
uniquement `src/userplugins/scTracker.desktop`. Les tests ne sont pas copiés.

## Configuration et utilisation

Sur le site, ouvrir **Paramètres → Clé d'envoi Discord**, créer une clé `discord:ingest`
à expiration bornée et copier l'URL HTTPS racine, l'empreinte SHA-256 et la clé dans le
panneau ScTracker. Cliquer sur **Enregistrer la configuration**, puis cocher les serveurs
à suivre. La clé masquée reste dans DataStore/IndexedDB sous `ScTracker_connection`, liée
à l'URL et à l'empreinte enregistrées, séparée des réglages synchronisés Vencord. Une
modification de l'URL ou du certificat par Cloud Sync ou un import bloque l'envoi jusqu'à
une nouvelle sauvegarde explicite. Une clé d'une ancienne version doit aussi être
enregistrée à nouveau. **Effacer la clé locale** la retire de ce client ; sa
révocation côté serveur se fait sur le site.

**Envoyer** traite le serveur de sa ligne, même s'il n'est pas coché. **Envoyer les serveurs cochés** les traite dans l'ordre,
avec 5 secondes de pause entre eux. Le clic droit propose toujours **« SC Tracker : envoyer
ce serveur »**, pour un envoi ponctuel sans changer les cases ni le minuteur. Le même menu
permet de suivre/ne plus suivre le serveur, ce qui change uniquement la sélection des lots
et du mode automatique. Les champs et boutons d'envoi sont
désactivés pendant un travail. La progression apparaît dans le panneau et les toasts ; le
résultat affiché pendant la session contient la date, la méthode, le nombre, la complétude
reconnue par l'API, la corpo reliée et le message de résultat. Il reste uniquement en mémoire,
disparaît au rechargement/désactivation du plugin et n'est pas synchronisé par Cloud Sync.
Les résumés enregistrés par les anciennes versions sont supprimés au chargement du plugin.

**Envoi automatique** est désactivé par défaut. L'activer dans le panneau envoie les serveurs
cochés après l'intervalle configuré : **60 minutes par défaut**, de 10 minutes à 7 jours.
Le panneau affiche la date du prochain envoi. Le délai suivant commence à la fin du lot.
La sélection est relue à chaque échéance ; un travail manuel déjà actif, une configuration
en cours d'enregistrement ou une sélection vide saute cette échéance. Le minuteur fonctionne
uniquement tant que Discord et le plugin sont actifs, sans rattrapage des envois manqués.
Modifier l'intervalle réinitialise le délai ; après un redémarrage, le premier envoi attend
à nouveau cet intervalle. Le prochain horaire n'est pas sauvegardé.

**Arrêter** désactive également l'envoi automatique. Pendant la collecte, il empêche tout POST de cette collecte et ignore les serveurs
suivants. Pendant l'upload, le tracker peut déjà enregistrer le message : arrêter ignore la
suite du lot, mais ne peut pas retirer cet envoi. Un 401, 403, 429, 503, `guild_excluded`,
un certificat inattendu ou un tracker injoignable arrête aussi le lot. Un 400, 413 ou
`stale_sync` permet de passer au serveur suivant.

## Collecte et limites

- Recherche de membres si les permissions l'autorisent : pages de 1 000, curseur d'arrivée
  en millisecondes, dédoublonnage, trois essais d'index au plus avec au moins 5 secondes
  d'attente, compteur remis à zéro après chaque page valide, puis fallback sur 202/403.
- Sinon, IDs des rôles non gérés et non vides, plus IDs du cache ; si ces endpoints
  échouent, les IDs déjà reçus et ceux du cache restent candidats. Les données des membres en cache
  ne sont jamais envoyées. Chaque candidat est redemandé par lots gateway de 100.
- Chaque lot porte un nonce de 32 caractères. Seuls les chunks du serveur et du nonce
  attendus sont acceptés. Le plugin patche les trois chemins Discord de propagation de ce
  champ, comme le plugin amont ImplicitRelationships. Si aucun lot ne restitue de nonce corrélé, le plugin affiche
  une erreur de compatibilité et ne transmet pas la collecte. Aucun résultat complet
  n'est revendiqué pour les méthodes rôles/cache.
- Espacement de 1,2 seconde entre appels REST, 1 seconde entre lots gateway, respect de
  `retry_after`, plafond commun de 200 appels et échéance de collecte de 25 minutes.
  Les IDs non résolus après 10 secondes sont omis ; un lot suivant attend 30 secondes.
  Une partie du budget reste réservée au rafraîchissement des IDs candidats. Une erreur
  tardive conserve les membres déjà reçus avec leur nonce ; une collecte vide n'est jamais envoyée.
- Une recherche n'est complète que si elle se termine normalement et que son nombre
  d'IDs uniques, non nul, égale l'attendu. Plafond, échéance, erreur ou curseur invalide/bloqué
  produisent un résultat partiel. Une collecte de plus de 30 minutes n'est pas envoyée.
- Maximum 50 000 membres et 25 Mio. Les rôles sont relus après la collecte, hors `@everyone`.
  Le message copie seulement les champs du contrat ; aucune permission `bigint` n'est incluse.

Les gardes de protocole vérifient les fonctions Discord et les réponses avant de les lire.
La compilation prouve l'intégration au ref Vencord, mais ne prouve pas que Discord conserve
ces fonctions ni le nonce. Une mise à jour du client peut nécessiter d'adapter ces gardes.

## Transport épinglé

Le renderer utilise uniquement le pont natif `ScTracker.postSync`, qui revalide URL,
empreinte, clé, serveur et taille. Il construit le seul chemin autorisé,
`/ingest/discord/guilds/{guildId}/syncs`. La clé et le corps ne sont écrits qu'après le
handshake TLS et la comparaison SHA-256 du certificat. Chaque requête utilise un socket
neuf ; aucun réglage TLS global n'est modifié.

Bornes par défaut : 15 secondes pour DNS/TCP/TLS, 30 secondes d'inactivité, 10 minutes
au total et 256 Kio de réponse. Une réponse HTTP anticipée ferme la connexion, même si
l'upload est encore en attente. Après upload, une coupure, un dépassement ou une absence
de réponse affiche que le message a peut-être été enregistré. Une empreinte différente
empêche l'écriture de la clé et du corps. Pour une rotation du certificat, recopier
l'empreinte affichée par le site avant le prochain envoi.

## Tests et développement

Ce package de tests utilise Node ≥22 et **pnpm 10.27.0**, séparément de la toolchain Vencord.
Depuis `vencord/` :

```sh
pnpm install --frozen-lockfile
pnpm test
pnpm typecheck
```

Le typecheck local couvre les modules purs et les tests. La CI copie également le plugin
dans Vencord au ref fixé pour son build et `testTsc`. Les tests couvrent pagination, chunks,
nonce, annulation et nettoyage, deadlines, lots, validation IPC, minimisation et TLS épinglé.
Le certificat et sa clé dans `tests/fixtures` sont publics et réservés à ces tests.

La recette restante dans Discord doit vérifier un petit serveur en mode partiel,
plus de 1 000 membres, les fallbacks rôles/cache, le nonce réel, les enveloppes de rôles,
les pauses/annulations, un certificat incorrect, l'arrêt d'un lot et le résultat sur le site.

## Données et droits des personnes

Obtenir l'accord des administrateurs de chaque serveur suivi avant la collecte.
Discord interdit l'automatisation d'un compte utilisateur hors de son API bot/OAuth2
et indique qu'elle peut entraîner une résiliation du compte
([règle officielle](https://support.discord.com/hc/fr/articles/115002192352-Comptes-d-utilisateur-automatis%C3%A9s-Self-Bots)).

Un envoi contient IDs Discord, noms, pseudos, indicateur de bot, rôles et dates d'arrivée.
L'administrateur du tracker doit informer les personnes concernées de ce suivi et de son
usage. Les vues du tracker donnent accès aux informations enregistrées ; l'expiration
et la révocation des clés limitent les possibilités d'envoi.

Les opérations administratives d'effacement des comptes et d'exclusion des comptes ou
serveurs sont décrites dans le README principal ; elles se font côté serveur. Supprimer
la clé locale ou décocher un serveur arrête ses futurs envois par ce client, sans effacer
l'historique déjà conservé. Les liens entre un compte Discord et un compte du tracker
peuvent être retirés via le site, indépendamment de l'historique collecté.

Un envoi complet fait foi : tous les départs constatés sont enregistrés. Le tracker signale
les départs massifs dans sa réponse et son journal, sans autorisation manuelle. Un envoi
partiel ne déduit aucun départ. Le site ne propose aucune suppression, remise à zéro ou
exclusion des serveurs et membres ; l'administration directe de l'API reste disponible.

Modèle d'information à adapter et publier sur le serveur avant sa première collecte :

> **Suivi du serveur dans [nom du tracker]**
>
> [Responsable et contact] enregistre les membres de ce serveur, à la demande ou à intervalle configuré, pour
> compléter l'historique de [corpo] et rapprocher les comptes Discord et RSI.
> Les données comprennent identifiant Discord, nom, nom affiché, pseudo du serveur,
> rôles, indicateur de bot et date d'arrivée. Aucun message ni statut de présence
> n'est collecté. Les liens RSI proposés sont confirmés à la main.
>
> Ces informations sont accessibles à [personnes ayant accès au tracker]. Le journal
> des envois est conservé [durée configurée, 365 jours par défaut]. Un compte non lié
> devenu inactif sur tous les serveurs est purgé après [durée configurée, 730 jours
> par défaut] ; les comptes liés et leur historique sont conservés jusqu'à effacement.
>
> Pour demander une correction, un effacement ou l'exclusion des futures collectes,
> contactez [contact]. L'effacement du compte ajoute son identifiant à une liste
> d'exclusion, afin que les futurs envois ne recréent pas ses données.
