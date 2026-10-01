# Collector.Web — Star Citizen Intel HUD

Frontend Next.js 15 au-dessus de `Collector.Api`. Thème cockpit HUD cyan/orange sur fond sombre.

## Prérequis

- Node 22, pnpm 10.27 via corepack
- Backend `Collector.Api` démarré sur `http://127.0.0.1:5000`

## Dev

```bash
cp .env.local.example .env.local
# Aucune clé d'API à renseigner : les pages serveur appellent l'API avec le
# JWT de l'utilisateur connecté.

pnpm install
pnpm dev
# → http://localhost:3000
```

## Structure

- `src/app/` — App Router (RSC + Client), groupées par `(public)` / `(auth)` / `(user)`.
- `src/components/hud/` — Atomes HUD réutilisables (`HudPanel`, `HudButton`, `HudStatTile`, `HudDataGrid`…).
- `src/lib/api/` — Client fetch typé, types miroirs des DTOs C#, parsing ProblemDetails.
- `src/app/api/auth/[...route]/route.ts` — BFF proxy qui pose les cookies httpOnly.
- `src/middleware.ts` — Vérifie la signature du JWT à chaque page (hors `/login`), renouvelle la session, pose la CSP.

## Pages

Toutes demandent un compte (créé par un administrateur), sauf `/login`.

| Route | Description |
|---|---|
| `/` | Accueil avec chiffres clés |
| `/orgs` | Catalogue organisations (filtres, tri et pagination côté API) |
| `/orgs/[sid]` | Fiche organisation : membres actuels et anciens paginés, croissance, activité, notes, serveurs Discord reliés |
| `/users` | Recherche de citoyens (2 caractères minimum) |
| `/users/[handle]` | Profil : organisations, historique des handles, changements, annotations, serveurs Discord et frise RSI + Discord |
| `/stats` | Tableau de bord global (graphiques chargés à la demande) |
| `/changes` | Flux des changements, rafraîchi toutes les 30 s |
| `/discord` | Serveurs Discord envoyés par le plugin Vencord : corpo reliée, actifs, rangs, dernier envoi ; serveurs non reliés en tête, avec recherche et choix d'une corpo par nom ou SID |
| `/discord/[guildId]` | Serveur Discord par onglets : membres, historique, écarts RSI, suggestions de liens, choix de la corpo et configuration des rangs, envois ; aucune suppression depuis l'interface |
| `/discord/multi` | Comptes Discord présents sur plusieurs serveurs suivis, avec les orgs RSI des citoyens liés |
| `/dashboard`, `/favorites` | Favoris et derniers changements |
| `/settings` | Mot de passe, jeton Discord, clé d'envoi Discord |
| `/accounts`, `/admin` | Comptes et ajouts manuels (administrateurs pour les comptes) |

## Tests

```bash
pnpm typecheck && pnpm test   # TypeScript + vitest
pnpm exec playwright install chromium
pnpm test:e2e                 # smoke Playwright (voir le README racine : E2E_BASE_URL, E2E_USERNAME, E2E_PASSWORD)
pnpm build && pnpm test:e2e:discord  # recette Discord isolée : API factice et sessions jetables
```

La recette Discord lance ses serveurs sur `127.0.0.1:3334` et `127.0.0.1:3335` puis
les arrête. Elle vérifie les onglets, les permissions, les liens et les clés d'envoi
sans contacter Discord ou RSI. Les tests du backend valident les règles métier et
la persistance. Pour un navigateur Chrome déjà installé, définir
`E2E_BROWSER_CHANNEL=chrome` ; par défaut Playwright utilise son Chromium.
La CI exécute aussi cette recette avec le serveur standalone compilé.

## Build prod

```bash
pnpm build && pnpm start
```

## Dépannage

- **Redirection vers /login en boucle** : la session a expiré ou le JWT est refusé (le front vérifie la signature avec `GET /api/auth/jwks` ; côté API, vérifier `Api:Jwt:PrivateKeyPath`).
- **Page d'erreur « Something failed »** : l'API ne répond pas ; le « REF » affiché se retrouve dans les journaux du serveur Next.
