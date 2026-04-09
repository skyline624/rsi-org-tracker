# Collector.Web — Star Citizen Intel HUD

Frontend Next.js 15 au-dessus de `Collector.Api`. Thème cockpit HUD cyan/orange sur fond sombre.

## Prérequis

- Node 20+ (testé sur 25)
- pnpm 10+
- Backend `Collector.Api` démarré sur `https://localhost:5001`
- Certificat de dev trusté : `dotnet dev-certs https --trust`

## Dev

```bash
cp .env.local.example .env.local
# Renseigne API_INTERNAL_KEY avec l'admin key du backend :
#   cd ../Collector.Api && dotnet user-secrets list | grep AdminApiKey
# Et SESSION_SECRET :
#   openssl rand -hex 32

pnpm install
pnpm dev
# → http://localhost:3000
```

## Structure

- `src/app/` — App Router (RSC + Client), groupées par `(public)` / `(auth)` / `(user)`.
- `src/components/hud/` — Atomes HUD réutilisables (`HudPanel`, `HudButton`, `HudStatTile`, `HudDataGrid`…).
- `src/lib/api/` — Client fetch typé, types miroirs des DTOs C#, parsing ProblemDetails.
- `src/app/api/auth/[...route]/route.ts` — BFF proxy qui pose les cookies httpOnly.
- `src/middleware.ts` — Guard des routes authentifiées.

## Pages (v1)

| Route | Description |
|---|---|
| `/` | Landing "Citizen Intel" avec stats live |
| `/orgs` | Catalogue organisations (filtres, pagination) |
| `/orgs/[sid]` | Fiche organisation (members, growth, changes) |
| `/users` | Recherche utilisateurs |
| `/users/[handle]` | Profil utilisateur (historique handles, orgs, changes) |
| `/stats` | Dashboard global (timeline, archetypes, top orgs, activity) |
| `/changes` | Flux changelog type terminal (polling 30 s) |
| `/login`, `/register`, `/forgot-password` | Auth |
| `/dashboard` | Tuiles favoris + derniers changes (auth) |
| `/favorites` | Gestion locale des favoris (auth) |
| `/settings` | Profil, API keys, mot de passe (auth) |

## Tests

```bash
pnpm test:e2e        # Playwright headless
pnpm test:e2e:ui     # Playwright mode UI
```

## Build prod

```bash
pnpm build && pnpm start
```

## Dépannage

- **Erreur TLS au fetch de l'API en RSC** : vérifier `NODE_TLS_REJECT_UNAUTHORIZED=0` dans `.env.local` (dev only).
- **401 partout** : l'API key dans `API_INTERNAL_KEY` doit être valide (≥ 24 char, pas `change-me*`).
- **CORS error** : vérifier `http://localhost:3000` dans `Collector.Api/appsettings.json` → `Api:Cors:AllowedOrigins`.
