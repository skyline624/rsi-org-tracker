# Commandes `/tracker` dans le bot Liberastra — plan de mise en œuvre

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** permettre aux membres autorisés du serveur Discord Liberastra de chercher orgs et joueurs du tracker par des commandes `/tracker`, avec un accès réglable par grade.

**Architecture:** le tracker expose six routes de lecture `/api/bot/*`, accessibles uniquement avec une clé à portée `bot:read`, derrière une route nginx `/bot-api/` filtrée par l'IP de `panda`. Le bot Liberastra (autre dépôt, autre serveur) appelle ces routes par un client HTTP qui épingle le certificat du VPS, et contrôle l'accès par rôle Discord à partir d'une table de sa propre base.

**Tech Stack:** tracker — .NET 10, ASP.NET Core, EF Core 8 sur SQLite, xUnit + FluentAssertions ; bot — .NET 10, Discord.Net 3.17.2 (Interactions), EF Core 9.0.2 sur SQLite, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-03-discord-tracker-commands-design.md`

## Global Constraints

- Deux dépôts :
  - tracker : `C:\Users\pc.DESKTOP-DQ6SVVV\Downloads\sc_tracker`, branche `bot-api` depuis `main` ;
  - bot : `C:\Users\pc.DESKTOP-DQ6SVVV\Downloads\Liberastra-Bot-Discord` (clone de `LameuleFR/Liberastra-Bot-Discord`), branches `server-sync` puis `tracker-commands`.
- `dotnet` n'est pas dans le PATH du poste : les commandes `dotnet …` du plan s'exécutent avec `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" …` (PowerShell).
- Portée de clé : exactement `bot:read` (`ApiKeyScopes.BotRead`). Schéma `BotReadKey`, politique `BotRead`, préfixe de chemin `/api/bot`.
- Une clé `bot:read` doit expirer dans 365 jours au plus, comme `discord:ingest`.
- En-têtes envoyés par le bot :
  - `X-Discord-User` : 17 à 20 chiffres (`DiscordSnowflake.IsValid`) ;
  - `X-Bot-Command` : `joueur`, `historique`, `recherche`, `org`, `membres`, `mouvements` ou `autocomplete`.
- Journal d'activité : `Action = "bot:<commande>"`, `EntityType = "discord:<id>"`, `EntityId` = cible (100 caractères au plus). Les requêtes `autocomplete` ne sont pas inscrites.
- Bornes :
  - recherche de 2 à 50 caractères, 10 résultats de chaque type ;
  - membres par pages de 25 ;
  - mouvements de 1 à 90 jours (7 par défaut), 50 de chaque type au plus ;
  - 15 derniers événements dans l'historique.
- nginx : `location /bot-api/`, `allow 185.146.193.199; deny all;`, `limit_req` à 30r/m avec `burst=10`, vers `http://127.0.0.1:5000/api/bot/`.
- Bot :
  - réglages `TrackerApi__BaseUrl`, `TrackerApi__ApiKey`, `TrackerApi__CertSha256` ;
  - délai maximal de 10 s ;
  - réponses éphémères en français ;
  - la vérification TLS n'est jamais désactivée : seule l'empreinte configurée est acceptée.
- Accès :
  - la permission Discord Administrateur passe toujours ;
  - une sous-commande que personne n'a ouverte est fermée ;
  - table `TrackerCommandAccess`, unique sur (`GuildId`, `Command`, `RoleId`).
- Aucune donnée du tracker n'est transmise à Gemini.
- Toute étape qui touche un serveur ou un dépôt distant (push GitHub, push `panda`, déploiement, nginx, `.env`) attend le « oui » explicite de l'utilisateur.

## Review Focus

1. Pseudo saisi avec une autre casse (`kentoo` pour `Kentoo`) ou ancien pseudo : le joueur doit être trouvé. Couvert par la tâche A2, `ResolveHandleAsync`.
2. SID saisi en minuscules ou entouré d'espaces (` liberastra `) : il doit être normalisé et l'org trouvée. Couvert par la tâche A5, `AnOrgSid_IsNormalized`.
3. Réponse très longue (joueur passé par 60 orgs aux noms longs) : l'embed doit rester dans les limites de Discord, sans exception. Couvert par la tâche B6, `AHistoryWithManyOrgs_StaysWithinDiscordLimits`.
4. Tracker injoignable, lent, certificat différent ou clé refusée : un message clair, aucune exception remontée. Couvert par la tâche B3, `FailuresBecomeOutcomes`.
5. Autocomplétion par quelqu'un sans accès : aucune suggestion et aucune requête au tracker. Couvert par la tâche B7, `AutocompleteWithoutAccess_SendsNothing`.

---

# Partie A — tracker (`sc_tracker`, branche `bot-api`)

Préparer la branche :

```bash
git -C C:/Users/pc.DESKTOP-DQ6SVVV/Downloads/sc_tracker checkout -b bot-api main
```

Commandes de test (si l'API locale tourne, ajouter `-o <dossier temporaire>`, sinon le build est verrouillé) :

```bash
dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~Bot"
dotnet test src/Collector.Tests --filter "FullyQualifiedName~QueryPlan"
```

### Task A1: portée `bot:read` et schéma `BotReadKey`

**Files:**
- Modify: `src/Collector.Api/Models/ApiKeyScopes.cs`
- Modify: `src/Collector.Api/Services/ApiKeyService.cs` (méthode `ValidateScope`)
- Create: `src/Collector.Api/Auth/BotReadAuth.cs`
- Create: `src/Collector.Api/Auth/BotReadKeyAuthHandler.cs`
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (sélecteur `Smart`, schéma, politique)
- Test: `src/Collector.Api.Tests/Bot/BotReadKeyAuthTests.cs`

**Interfaces:**
- Produces: `ApiKeyScopes.BotRead` (`"bot:read"`) ; `BotReadAuth.SchemeName`, `PolicyName`, `ScopeClaimType`, `Scope`, `PathPrefix`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Collector.Api.Auth;
using Collector.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Bot;

/// <summary>The BotReadKey scheme and the Smart selector sending /api/bot to it (spec § 5.1).</summary>
[Collection(ApiCollection.Name)]
public class BotReadKeyAuthTests(ApiFactory factory)
{
    private const string BotPath = "/api/bot/search";

    private async Task<AuthenticateResult> AuthenticateAsync(string scheme, string path, string? apiKey)
    {
        using var scope = factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        if (apiKey is not null) context.Request.Headers["x-api-key"] = apiKey;
        return await context.AuthenticateAsync(scheme);
    }

    private static async Task<string> CreateKeyAsync(HttpClient owner, string? scope, int days = 30)
    {
        var response = await owner.PostAsJsonAsync("/api/api-keys", new
        {
            name = "bot-auth",
            expiresAt = scope is null ? (DateTime?)null : DateTime.UtcNow.AddDays(days),
            scope,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!;
    }

    private Task<HttpClient> OwnerAsync() => factory.SignedInClientAsync($"bot-auth-{Guid.NewGuid():N}");

    [Fact]
    public async Task BotKey_IsAuthenticated_OnTheBotScheme_WithItsScope()
    {
        var key = await CreateKeyAsync(await OwnerAsync(), ApiKeyScopes.BotRead);

        var result = await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, key);

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Principal!.FindFirstValue(BotReadAuth.ScopeClaimType).Should().Be(ApiKeyScopes.BotRead);
        result.Principal!.IsInRole("Admin").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApiKeyScopes.DiscordIngest)]
    public async Task OtherKeys_AreRefused_ByTheBotScheme(string? scope)
    {
        var key = await CreateKeyAsync(await OwnerAsync(), scope);

        (await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, key)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TheAdminKey_IsRefused_ByTheBotScheme()
    {
        (await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, ApiFactory.AdminApiKey)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task BotKey_IsRefused_ByTheGeneralScheme()
    {
        var key = await CreateKeyAsync(await OwnerAsync(), ApiKeyScopes.BotRead);

        (await AuthenticateAsync("ApiKey", "/api/organizations", key)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task BotKey_IsRefused_ByTheDiscordIngestScheme_AndTheReverse()
    {
        var owner = await OwnerAsync();
        var botKey = await CreateKeyAsync(owner, ApiKeyScopes.BotRead);
        var ingestKey = await CreateKeyAsync(owner, ApiKeyScopes.DiscordIngest);

        (await AuthenticateAsync(DiscordIngestAuth.SchemeName, "/api/ingest/discord/roster", botKey)).Succeeded.Should().BeFalse();
        (await AuthenticateAsync(BotReadAuth.SchemeName, BotPath, ingestKey)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TheSmartScheme_SendsTheBotPath_ToTheBotScheme()
    {
        var key = await CreateKeyAsync(await OwnerAsync(), ApiKeyScopes.BotRead);

        var result = await AuthenticateAsync("Smart", BotPath, key);

        result.Succeeded.Should().BeTrue(result.Failure?.Message);
        result.Principal!.FindFirstValue(BotReadAuth.ScopeClaimType).Should().Be(ApiKeyScopes.BotRead);
    }

    [Theory]
    [InlineData(400)]
    public async Task ABotKey_MustExpireWithinAYear(int days)
    {
        var owner = await OwnerAsync();

        var tooLong = await owner.PostAsJsonAsync("/api/api-keys",
            new { name = "bot", expiresAt = DateTime.UtcNow.AddDays(days), scope = ApiKeyScopes.BotRead });
        var noExpiry = await owner.PostAsJsonAsync("/api/api-keys",
            new { name = "bot", expiresAt = (DateTime?)null, scope = ApiKeyScopes.BotRead });

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noExpiry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~BotReadKeyAuthTests"`
Expected: compilation error (`BotReadAuth` et `ApiKeyScopes.BotRead` n'existent pas).

- [ ] **Step 3: Add the scope**

Dans `src/Collector.Api/Models/ApiKeyScopes.cs`, ajouter la constante et l'accepter dans `IsValid` :

```csharp
    /// <summary>Allows the /api/bot read routes only (the Liberastra Discord bot).</summary>
    public const string BotRead = "bot:read";
```

```csharp
    public static bool IsValid(string? scope) => scope is null or DiscordIngest or BotRead;
```

Dans `ApiKeyService.ValidateScope`, la règle d'expiration s'applique désormais à toute clé à portée (les messages de `discord:ingest` restent identiques) :

```csharp
    /// <summary>Requires a bounded expiry for every scoped credential while retaining full keys' optional expiry.</summary>
    private static void ValidateScope(string? scope, DateTime? expiresAt, DateTime now)
    {
        if (!ApiKeyScopes.IsValid(scope))
            throw new ValidationException("Unknown API key scope.");
        if (scope is null) return;

        if (expiresAt is null)
            throw new ValidationException($"A {scope} key needs an expiry date.");
        if (expiresAt <= now)
            throw new ValidationException("The expiry date must be in the future.");
        if (expiresAt > now.AddDays(ApiKeyScopes.MaxDiscordIngestLifetimeDays))
            throw new ValidationException(
                $"A {scope} key expires within {ApiKeyScopes.MaxDiscordIngestLifetimeDays} days.");
    }
```

- [ ] **Step 4: Add the scheme**

`src/Collector.Api/Auth/BotReadAuth.cs` :

```csharp
using Collector.Api.Models;

namespace Collector.Api.Auth;

/// <summary>Authentication boundary of the Liberastra Discord bot's read routes (spec 2026-10-03).</summary>
public static class BotReadAuth
{
    public const string SchemeName = "BotReadKey";
    public const string PolicyName = "BotRead";
    public const string ScopeClaimType = "scope";
    public const string Scope = ApiKeyScopes.BotRead;
    public const string PathPrefix = "/api/bot";
}
```

`src/Collector.Api/Auth/BotReadKeyAuthHandler.cs` :

```csharp
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Collector.Api.Auth;

/// <summary>Accepts only live bot:read keys, without their owner's other permissions.</summary>
public sealed class BotReadKeyAuthHandler : AuthenticationHandler<ApiKeySchemeOptions>
{
    private readonly ApiKeyService _apiKeyService;

    public BotReadKeyAuthHandler(
        IOptionsMonitor<ApiKeySchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The static admin key and bearer tokens have no standing in this scheme.
        var rawKey = Request.Headers["x-api-key"].ToString();
        if (string.IsNullOrWhiteSpace(rawKey))
            return AuthenticateResult.NoResult();

        var validation = await _apiKeyService.ValidateAsync(rawKey, Context.RequestAborted);
        if (validation is null)
            return AuthenticateResult.Fail("Invalid API key");
        if (validation.Scope != BotReadAuth.Scope)
            return AuthenticateResult.Fail("Not a bot:read key");
        if (validation.User.IsBanned)
            return AuthenticateResult.Fail("Account is banned");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, validation.User.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, validation.User.Username),
            new Claim(BotReadAuth.ScopeClaimType, BotReadAuth.Scope),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
```

Dans `ServiceCollectionExtensions.cs` : le sélecteur `Smart` envoie `/api/bot` au nouveau schéma, on enregistre le schéma, puis on ajoute la politique à côté de `DiscordIngestAuth.PolicyName`.

```csharp
            .AddPolicyScheme("Smart", "JWT or ApiKey", opts =>
                opts.ForwardDefaultSelector = ctx =>
                    ctx.Request.Path.StartsWithSegments(DiscordIngestAuth.PathPrefix, StringComparison.OrdinalIgnoreCase)
                        ? DiscordIngestAuth.SchemeName
                        : ctx.Request.Path.StartsWithSegments(BotReadAuth.PathPrefix, StringComparison.OrdinalIgnoreCase)
                            ? BotReadAuth.SchemeName
                            : ctx.Request.Headers.ContainsKey("Authorization")
                                ? JwtBearerDefaults.AuthenticationScheme
                                : "ApiKey")
```

```csharp
            .AddScheme<ApiKeySchemeOptions, DiscordIngestKeyAuthHandler>(DiscordIngestAuth.SchemeName, _ => { })
            .AddScheme<ApiKeySchemeOptions, BotReadKeyAuthHandler>(BotReadAuth.SchemeName, _ => { });
```

```csharp
            opts.AddPolicy(BotReadAuth.PolicyName,
                policy => policy
                    .AddAuthenticationSchemes(BotReadAuth.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(BotReadAuth.ScopeClaimType, BotReadAuth.Scope));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~BotReadKeyAuthTests|FullyQualifiedName~DiscordIngest|FullyQualifiedName~ApiKey"`
Expected: PASS (les tests de la clé d'envoi Discord passent toujours).

- [ ] **Step 6: Commit**

```bash
git add src/Collector.Api/Models/ApiKeyScopes.cs src/Collector.Api/Services/ApiKeyService.cs src/Collector.Api/Auth/BotReadAuth.cs src/Collector.Api/Auth/BotReadKeyAuthHandler.cs src/Collector.Api/Extensions/ServiceCollectionExtensions.cs src/Collector.Api.Tests/Bot/BotReadKeyAuthTests.cs
git commit -m "feat(api): bot:read API keys, valid on /api/bot only"
```

### Task A2: requêtes partagées entre le site et le bot

Le site et le bot doivent répondre la même chose. La recherche de joueurs, les orgs d'un joueur et la recherche d'orgs quittent donc les contrôleurs pour deux services. La résolution d'un pseudo saisi (casse, ancien pseudo) est ajoutée.

**Files:**
- Create: `src/Collector.Api/Services/UserLookupService.cs`
- Create: `src/Collector.Api/Services/OrganizationLookupService.cs`
- Modify: `src/Collector.Api/Controllers/UsersController.cs` (`GetAll`, `GetOrganizations`, suppression de `SearchUsersAsync` et des constantes déplacées)
- Modify: `src/Collector.Api/Controllers/OrganizationsController.cs` (`LatestOrgs`, `GetSuggestions`)
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (enregistrement des deux services)
- Test: `src/Collector.Api.Tests/Bot/UserLookupServiceTests.cs`

**Interfaces:**
- Produces:
  - `UserLookupService.SearchAsync(string search, int page, int pageSize, CancellationToken ct) : Task<PaginatedResponse<UserProfileDto>>`
  - `UserLookupService.GetMembershipsAsync(string handle, bool includeInactive, CancellationToken ct) : Task<IReadOnlyList<MembershipRow>>`
  - `record MembershipRow(OrganizationMember Latest, string? OrgName, DateTime? FirstSeen)`
  - `UserLookupService.ResolveHandleAsync(string input, CancellationToken ct) : Task<string?>`
  - `OrganizationLookupService.LatestOrgs() : IQueryable<Organization>`
  - `OrganizationLookupService.SuggestAsync(string? query, CancellationToken ct) : Task<IReadOnlyList<OrganizationSuggestionDto>>`

- [ ] **Step 1: Write the failing tests**

```csharp
using Collector.Api.Services;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Bot;

/// <summary>How a handle typed in Discord is matched to the one the tracker stores (spec § 5.2).</summary>
[Collection(ApiCollection.Name)]
public class UserLookupServiceTests(ApiFactory factory)
{
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    private async Task<string?> ResolveAsync(string input, Action<TrackerDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        seed(db);
        await db.SaveChangesAsync();
        return await scope.ServiceProvider.GetRequiredService<UserLookupService>().ResolveHandleAsync(input, default);
    }

    [Fact]
    public async Task ACitizenHandle_IsFound_WhateverItsCase()
    {
        var handle = Unique("Pilot");
        var resolved = await ResolveAsync(handle.ToLowerInvariant(), db =>
        {
            db.Users.Add(new User { CitizenId = Random.Shared.Next(1, int.MaxValue), UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "LOOKUP", UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true });
        });

        resolved.Should().Be(handle);
    }

    [Fact]
    public async Task ARosterOnlyHandle_IsFound_WhateverItsCase()
    {
        var handle = Unique("Roster");
        var resolved = await ResolveAsync(handle.ToUpperInvariant(), db =>
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "LOOKUP", UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true }));

        resolved.Should().Be(handle);
    }

    [Fact]
    public async Task AFormerHandle_LeadsToTheCurrentOne()
    {
        var current = Unique("Now");
        var former = Unique("Was");
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        var resolved = await ResolveAsync(former, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = current, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = former, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-1) });
        });

        resolved.Should().Be(current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NobodyByThatName0000")]
    public async Task AnUnknownHandle_IsNull(string input)
    {
        (await ResolveAsync(input, _ => { })).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~UserLookupServiceTests"`
Expected: compilation error (`UserLookupService` n'existe pas).

- [ ] **Step 3: Create `UserLookupService`**

Le corps de `SearchAsync` reçoit **sans modification** celui de `UsersController.SearchUsersAsync` (lignes 77-158 actuelles : SQL brut en UNION, comptage plafonné, pagination). Seul changement : `_db` devient `db` (deux occurrences).

```csharp
using Collector.Api.Dtos.Common;
using Collector.Api.Dtos.Users;
using Collector.Api.Errors;
using Collector.Api.Extensions;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

/// <summary>The latest roster row of a handle in one org, the org's latest name, and the handle's first appearance there.</summary>
public sealed record MembershipRow(OrganizationMember Latest, string? OrgName, DateTime? FirstSeen);

/// <summary>Player queries shared by the site (UsersController) and the Discord bot (BotController).</summary>
public sealed class UserLookupService(TrackerDbContext db, IOrganizationRepository orgRepo)
{
    public const int MinSearchLength = 2;
    public const int MaxCountedMatches = 1001;

    /// <summary>
    /// Searches enriched citizens AND roster-only members (handles tracked in
    /// organization_members that never got a CitizenId, so they can't exist in
    /// `users` — e.g. RSI accounts that hide their citizen number). EF Core cannot
    /// translate a UNION of these two differently-shaped projections under SQLite, so
    /// the combined set is expressed as raw SQL, which also lets the DB do the paging.
    /// </summary>
    public async Task<PaginatedResponse<UserProfileDto>> SearchAsync(
        string search, int page, int pageSize, CancellationToken ct)
    {
        // Lines 77-158 of UsersController.SearchUsersAsync, moved unchanged (_db → db).
    }

    /// <summary>Each org of the handle with its latest row; former ones only when asked.</summary>
    public async Task<IReadOnlyList<MembershipRow>> GetMembershipsAsync(
        string handle, bool includeInactive, CancellationToken ct)
    {
        var memberships = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .ToListAsync(ct);

        if (!includeInactive)
            memberships = memberships.Where(m => m.IsActive).ToList();

        // "Member since" = first snapshot in which this handle appeared in each org.
        // A plain GroupBy + Min aggregate (unlike the First() projection above) so it
        // stays a single translatable query.
        var firstSeen = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserHandle == handle)
            .GroupBy(m => m.OrgSid)
            .Select(g => new { OrgSid = g.Key, First = g.Min(m => m.Timestamp) })
            .ToDictionaryAsync(x => x.OrgSid, x => x.First, ct);

        var orgSids = memberships.Select(m => m.OrgSid).Distinct().ToList();
        var orgNames = await orgRepo.GetLatestNamesBySidsAsync(orgSids, ct);

        return memberships
            .Select(m => new MembershipRow(
                m,
                orgNames.GetValueOrDefault(m.OrgSid),
                firstSeen.TryGetValue(m.OrgSid, out var since) ? since : null))
            .ToList();
    }

    /// <summary>
    /// The handle as the tracker stores it, for a handle typed by someone: as typed in
    /// users; else ignoring case in rosters (IX_organization_members_UserHandle_NoCase);
    /// else a former handle of a citizen, which leads to the current one. Null if unknown.
    /// </summary>
    public async Task<string?> ResolveHandleAsync(string input, CancellationToken ct)
    {
        var handle = input.Trim();
        if (handle.Length == 0) return null;

        var exact = await db.Users.AsNoTracking()
            .Where(u => u.UserHandle == handle)
            .Select(u => u.UserHandle)
            .FirstOrDefaultAsync(ct);
        if (exact != null) return exact;

        var roster = await db.OrganizationMembers.AsNoTracking()
            .Where(m => EF.Functions.Collate(m.UserHandle, "NOCASE") == handle)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.UserHandle)
            .FirstOrDefaultAsync(ct);
        if (roster != null) return roster;

        var citizenId = await db.UserHandleHistories.AsNoTracking()
            .Where(h => EF.Functions.Collate(h.UserHandle, "NOCASE") == handle)
            .OrderByDescending(h => h.LastSeen)
            .Select(h => (int?)h.CitizenId)
            .FirstOrDefaultAsync(ct);
        if (citizenId is null) return null;

        return await db.Users.AsNoTracking()
            .Where(u => u.CitizenId == citizenId)
            .Select(u => u.UserHandle)
            .FirstOrDefaultAsync(ct);
    }
}
```

- [ ] **Step 4: Create `OrganizationLookupService`**

`LatestOrgs` reprend la requête de `OrganizationsController.LatestOrgs` (lignes 36-49). `SuggestAsync` reçoit **sans modification** le corps de `GetSuggestions` (lignes 56-90, de la vérification des 100 caractères au `ToListAsync`) ; seul le `return Ok(items);` final devient `return items;`.

```csharp
using System.Text;
using Collector.Api.Dtos.Organizations;
using Collector.Api.Errors;
using Collector.Data;
using Collector.Models;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services;

/// <summary>Organization queries shared by the site (OrganizationsController) and the Discord bot.</summary>
public sealed class OrganizationLookupService(TrackerDbContext db)
{
    // Efficient "latest snapshot per org" using INNER JOIN with MAX(Timestamp). The
    // list needs no long text: they are not read (the detail page fetches them).
    public IQueryable<Organization> LatestOrgs() =>
        db.Organizations.FromSqlRaw("""
            SELECT o.Id, o.Sid, o.Timestamp, o.Name, o.UrlImage, o.UrlCorpo,
                   o.Archetype, o.Lang, o.Commitment, o.Recruiting, o.Roleplay,
                   o.MembersCount, NULL AS Description, NULL AS History,
                   NULL AS Manifesto, NULL AS Charter,
                   o.FocusPrimaryName, o.FocusPrimaryImage, o.FocusSecondaryName,
                   o.FocusSecondaryImage, o.ContentCollected, o.Source
            FROM organizations AS o
            INNER JOIN (
                SELECT Sid, MAX(Timestamp) AS MaxTs
                FROM organizations GROUP BY Sid
            ) AS g ON o.Sid = g.Sid AND o.Timestamp = g.MaxTs
            """);

    /// <summary>Small name/SID lookup for explicit organization selection, independent of list pagination.</summary>
    public async Task<IReadOnlyList<OrganizationSuggestionDto>> SuggestAsync(string? query, CancellationToken ct)
    {
        // Lines 56-90 of OrganizationsController.GetSuggestions, moved unchanged
        // (FormKC normalization, LIKE on compact SID/name, top 10), then: return items;
    }
}
```

- [ ] **Step 5: Wire the controllers and DI**

Dans `ServiceCollectionExtensions.cs`, à côté de `services.AddScoped<StatsService>();` :

```csharp
        services.AddScoped<UserLookupService>();
        services.AddScoped<OrganizationLookupService>();
```

`UsersController` reçoit `UserLookupService lookup` par son constructeur (champ `_lookup`). Ses constantes `MinSearchLength`, `MaxCountedMatches` et la méthode `SearchUsersAsync` sont supprimées.

```csharp
        return Ok(await _lookup.SearchAsync(search, page, pageSize, ct));
```

```csharp
    [HttpGet("{handle}/organizations")]
    public async Task<ActionResult<IReadOnlyList<OrganizationMemberDto>>> GetOrganizations(
        string handle,
        [FromQuery] bool include_inactive = false,
        CancellationToken ct = default)
    {
        var rows = await _lookup.GetMembershipsAsync(handle, include_inactive, ct);
        return Ok(rows.Select(r => r.Latest.ToDto(r.OrgName, r.FirstSeen)).ToList());
    }
```

`OrganizationsController` reçoit `OrganizationLookupService lookup` (champ `_lookup`). Sa méthode privée `LatestOrgs()` est supprimée, et ses appels restants (hors `GetSuggestions`) deviennent `_lookup.LatestOrgs()`. `GetSuggestions` devient :

```csharp
    [HttpGet("suggestions")]
    public async Task<ActionResult<IReadOnlyList<OrganizationSuggestionDto>>> GetSuggestions(
        [FromQuery] string? query, CancellationToken ct)
        => Ok(await _lookup.SuggestAsync(query, ct));
```

- [ ] **Step 6: Run the new and the existing tests**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~UserLookupServiceTests|FullyQualifiedName~UserSearch|FullyQualifiedName~UserOrganizations|FullyQualifiedName~OrganizationSuggestions|FullyQualifiedName~OrgMembersPaging"`
Expected: PASS. Les tests existants vérifient que le site répond exactement comme avant.

- [ ] **Step 7: Commit**

```bash
git add src/Collector.Api/Services/UserLookupService.cs src/Collector.Api/Services/OrganizationLookupService.cs src/Collector.Api/Controllers/UsersController.cs src/Collector.Api/Controllers/OrganizationsController.cs src/Collector.Api/Extensions/ServiceCollectionExtensions.cs src/Collector.Api.Tests/Bot/UserLookupServiceTests.cs
git commit -m "refactor(api): player and org lookups shared by the site and the bot; handle resolution"
```

### Task A3: contrôleur du bot, en-têtes, journal et recherche

**Files:**
- Create: `src/Collector.Api/Dtos/Bot/BotDtos.cs`
- Create: `src/Collector.Api/Services/BotRequestFilter.cs`
- Create: `src/Collector.Api/Controllers/BotController.cs`
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (`AddScoped<BotRequestFilter>()`)
- Test: `src/Collector.Api.Tests/Bot/BotTestSupport.cs`, `src/Collector.Api.Tests/Bot/BotAccessTests.cs`

**Interfaces:**
- Consumes: `BotReadAuth.PolicyName` (A1) ; `UserLookupService`, `OrganizationLookupService` (A2).
- Produces: `GET /api/bot/search?q=` → `BotSearchDto` ; `BotRequestFilter.DiscordUserHeader`, `CommandHeader`, `Commands` ; tous les DTO `Bot*` utilisés par A4 et A5.

- [ ] **Step 1: Write the test support and the failing tests**

`src/Collector.Api.Tests/Bot/BotTestSupport.cs` :

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Models;
using Collector.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Collector.Api.Tests.Bot;

internal static class BotTestSupport
{
    public const string DiscordUser = "123456789012345678";

    public static string NewSid() => "B" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public static string NewHandle() => "p" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>A bot:read key owned by a fresh, non-admin account.</summary>
    public static async Task<string> CreateBotKeyAsync(ApiFactory factory)
    {
        var owner = await factory.SignedInClientAsync($"bot-{Guid.NewGuid():N}");
        var response = await owner.PostAsJsonAsync("/api/api-keys",
            new { name = "bot", expiresAt = DateTime.UtcNow.AddDays(30), scope = ApiKeyScopes.BotRead });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString()!;
    }

    public static HttpRequestMessage Get(string url, string? apiKey, string command, string? discordUser = DiscordUser)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        if (discordUser is not null) request.Headers.Add("X-Discord-User", discordUser);
        request.Headers.Add("X-Bot-Command", command);
        return request;
    }

    public static async Task SeedAsync(ApiFactory factory, Action<TrackerDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}
```

`src/Collector.Api.Tests/Bot/BotAccessTests.cs` :

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Api.Models;
using Collector.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>Who may call /api/bot, the headers it requires, and what it writes to the activity log (spec § 5.1, § 5.3).</summary>
[Collection(ApiCollection.Name)]
public class BotAccessTests(ApiFactory factory)
{
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => await factory.CreateClient().SendAsync(request);

    [Fact]
    public async Task ABotKey_WithItsHeaders_CanSearch()
    {
        var key = await CreateBotKeyAsync(factory);
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Searchable Corp", Timestamp = DateTime.UtcNow, MembersCount = 12 }));

        var response = await SendAsync(Get($"/api/bot/search?q={sid}", key, "recherche"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("orgs").EnumerateArray().Select(o => (o.GetProperty("sid").GetString(), o.GetProperty("membersCount").GetInt32()))
            .Should().Contain((sid, 12));
    }

    [Fact]
    public async Task EveryOtherCredential_IsRefused()
    {
        var owner = await factory.SignedInClientAsync($"bot-other-{Guid.NewGuid():N}");
        var fullKey = (await (await owner.PostAsJsonAsync("/api/api-keys", new { name = "full" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString();
        var ingestKey = (await (await owner.PostAsJsonAsync("/api/api-keys",
                new { name = "ingest", expiresAt = DateTime.UtcNow.AddDays(30), scope = ApiKeyScopes.DiscordIngest }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString();

        foreach (var key in new[] { fullKey, ingestKey, ApiFactory.AdminApiKey, null })
        {
            (await SendAsync(Get("/api/bot/search?q=ab", key, "recherche"))).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, $"key {key?[..6]}");
        }
        (await owner.SendAsync(Get("/api/bot/search?q=ab", null, "recherche"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a signed-in session has no standing on /api/bot");
    }

    [Fact]
    public async Task ABotKey_IsWorthNothing_OutsideTheBotRoutes()
    {
        var key = await CreateBotKeyAsync(factory);

        foreach (var url in new[] { "/api/organizations", "/api/users?search=ab", "/api/changes" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", key);
            (await SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, url);
        }
    }

    [Theory]
    [InlineData(null, "recherche")]
    [InlineData("not-a-snowflake", "recherche")]
    [InlineData(DiscordUser, "psyche")]
    [InlineData(DiscordUser, "")]
    public async Task MissingOrInvalidHeaders_AreA400(string? discordUser, string command)
    {
        var key = await CreateBotKeyAsync(factory);

        (await SendAsync(Get("/api/bot/search?q=ab", key, command, discordUser))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ASearch_IsWrittenToTheActivityLog_AnAutocompleteIsNot()
    {
        var key = await CreateBotKeyAsync(factory);
        var text = "zz" + Guid.NewGuid().ToString("N")[..6];

        (await SendAsync(Get($"/api/bot/search?q={text}", key, "recherche"))).EnsureSuccessStatusCode();
        (await SendAsync(Get($"/api/bot/search?q={text}x", key, "autocomplete"))).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var logs = await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ActivityLogs.AsNoTracking()
            .Where(l => l.EntityId != null && l.EntityId.StartsWith(text)).ToListAsync();
        logs.Select(l => (l.Action, l.EntityType, l.EntityId))
            .Should().Equal(("bot:recherche", $"discord:{DiscordUser}", text));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("012345678901234567890123456789012345678901234567890")]
    public async Task ASearchOutsideItsBounds_IsA400(string q)
    {
        var key = await CreateBotKeyAsync(factory);

        (await SendAsync(Get($"/api/bot/search?q={q}", key, "recherche"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~BotAccessTests"`
Expected: FAIL. La route n'existe pas : le bon cas reçoit un 404, et le test de journal ne trouve rien.

- [ ] **Step 3: Create the DTOs**

`src/Collector.Api/Dtos/Bot/BotDtos.cs` :

```csharp
namespace Collector.Api.Dtos.Bot;

// Compact answers for the Discord bot (spec § 5.2); dates in UTC.
public sealed record BotSearchDto(IReadOnlyList<BotOrgHitDto> Orgs, IReadOnlyList<BotPlayerHitDto> Players);
public sealed record BotOrgHitDto(string Sid, string Name, int MembersCount);
public sealed record BotPlayerHitDto(string Handle, string? DisplayName);

public sealed record BotMembershipDto(string Sid, string? Name, string? Rank, int? Stars, DateTime? Since, DateTime LastSeen, bool Active);
public sealed record BotPlayerDto(
    string Handle, string? DisplayName, int? CitizenId, DateTime? Enlisted, string? Location,
    bool ProfileRead, IReadOnlyList<BotMembershipDto> CurrentOrgs, DateTime? LastSeen);
public sealed record BotHandleDto(string Handle, DateTime FirstSeen, DateTime LastSeen);
public sealed record BotEventDto(DateTime At, string Type, string? OrgSid, string? Old, string? New);
public sealed record BotHistoryDto(
    string Handle, IReadOnlyList<BotMembershipDto> Orgs, IReadOnlyList<BotHandleDto> Handles, IReadOnlyList<BotEventDto> Events);

public sealed record BotCountsDto(int Total, int? Visible, int? Redacted, int? Hidden, DateTime At);
public sealed record BotTrendDto(int From, int To);
public sealed record BotOrgDto(
    string Sid, string Name, string? Archetype, string? Lang, bool? Recruiting, bool? Roleplay, int MembersCount,
    BotCountsDto? Counts, BotTrendDto? Trend30d, DateTime? MembersReadAt);
public sealed record BotMemberDto(string Handle, string? DisplayName, string? Rank, int? Stars, DateTime? Since);
public sealed record BotMembersPageDto(string Sid, int Page, int PageSize, int Total, IReadOnlyList<BotMemberDto> Items);
public sealed record BotMovementDto(string Handle, DateTime At);
public sealed record BotMovementsDto(string Sid, int Days, IReadOnlyList<BotMovementDto> Joined, IReadOnlyList<BotMovementDto> Left, bool Truncated);
```

- [ ] **Step 4: Create the request filter**

`src/Collector.Api/Services/BotRequestFilter.cs` :

```csharp
using Collector.Api.Auth;
using Collector.Api.Errors;
using Collector.Discord;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Collector.Api.Services;

/// <summary>
/// Requires the bot's headers on every /api/bot request (who asked, for which
/// subcommand) and writes each one, autocompletion aside, to the activity log (spec § 5.3).
/// </summary>
public sealed class BotRequestFilter(ActivityLogService activity, CurrentUserAccessor currentUser) : IAsyncActionFilter
{
    public const string DiscordUserHeader = "X-Discord-User";
    public const string CommandHeader = "X-Bot-Command";
    public const string Autocomplete = "autocomplete";
    public const int MaxTargetLength = 100;

    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "joueur", "historique", "recherche", "org", "membres", "mouvements", Autocomplete,
    };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;
        var discordUser = headers[DiscordUserHeader].ToString();
        var command = headers[CommandHeader].ToString();
        if (!DiscordSnowflake.IsValid(discordUser) || !Commands.Contains(command))
            throw new ValidationException(
                $"{DiscordUserHeader} (17 à 20 chiffres) et {CommandHeader} (sous-commande connue) sont requis.");

        await next();

        if (command == Autocomplete) return;
        var target = TargetOf(context.ActionArguments);
        await activity.LogAsync($"bot:{command}", currentUser.UserId, $"discord:{discordUser}", target,
            currentUser.IpAddress, context.HttpContext.RequestAborted);
    }

    /// <summary>The handle, SID or search text of the request.</summary>
    private static string? TargetOf(IDictionary<string, object?> arguments)
    {
        foreach (var name in new[] { "handle", "sid", "q" })
        {
            if (arguments.TryGetValue(name, out var value) && value is string text)
                return text.Length <= MaxTargetLength ? text : text[..MaxTargetLength];
        }
        return null;
    }
}
```

- [ ] **Step 5: Create the controller with the search route**

`src/Collector.Api/Controllers/BotController.cs` :

```csharp
using Collector.Api.Auth;
using Collector.Api.Dtos.Bot;
using Collector.Api.Errors;
using Collector.Api.Services;
using Collector.Data;
using Collector.Data.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Controllers;

/// <summary>
/// Read routes of the Liberastra Discord bot, reached from outside through nginx
/// /bot-api/ (panda's IP only) with a bot:read key (spec 2026-10-03, § 5).
/// </summary>
[ApiController]
[Route("api/bot")]
[Authorize(Policy = BotReadAuth.PolicyName)]
[ServiceFilter(typeof(BotRequestFilter))]
public sealed class BotController(
    TrackerDbContext db,
    UserLookupService users,
    OrganizationLookupService orgs,
    IOrganizationMemberRepository members,
    IUserHandleHistoryRepository handleHistory,
    IChangeEventRepository changes) : ControllerBase
{
    public const int SearchMax = 10;
    public const int MembersPageSize = 25;
    public const int MaxMovements = 50;
    public const int HistoryEvents = 15;

    [HttpGet("search")]
    public async Task<ActionResult<BotSearchDto>> Search([FromQuery] string? q, CancellationToken ct)
    {
        var text = (q ?? "").Trim();
        if (text.Length is < 2 or > 50)
            throw new ValidationException("La recherche fait de 2 à 50 caractères.");

        var orgHits = await orgs.SuggestAsync(text, ct);
        var sids = orgHits.Select(o => o.Sid).ToList();
        var counts = (await orgs.LatestOrgs().AsNoTracking()
                .Where(o => sids.Contains(o.Sid))
                .Select(o => new { o.Sid, o.MembersCount })
                .ToListAsync(ct))
            .DistinctBy(o => o.Sid)
            .ToDictionary(o => o.Sid, o => o.MembersCount);
        var players = await users.SearchAsync(text, 1, SearchMax, ct);

        return Ok(new BotSearchDto(
            orgHits.Select(o => new BotOrgHitDto(o.Sid, o.Name, counts.GetValueOrDefault(o.Sid))).ToList(),
            players.Items.Select(p => new BotPlayerHitDto(p.UserHandle, p.DisplayName)).ToList()));
    }
}
```

Dans `ServiceCollectionExtensions.cs`, à côté des deux services de A2 :

```csharp
        services.AddScoped<BotRequestFilter>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~BotAccessTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Collector.Api/Dtos/Bot/BotDtos.cs src/Collector.Api/Services/BotRequestFilter.cs src/Collector.Api/Controllers/BotController.cs src/Collector.Api/Extensions/ServiceCollectionExtensions.cs src/Collector.Api.Tests/Bot/BotTestSupport.cs src/Collector.Api.Tests/Bot/BotAccessTests.cs
git commit -m "feat(api): /api/bot search, headers required and written to the activity log"
```

### Task A4: fiche et historique d'un joueur

**Files:**
- Modify: `src/Collector.Api/Controllers/BotController.cs`
- Test: `src/Collector.Api.Tests/Bot/BotPlayerTests.cs`

**Interfaces:**
- Consumes: `UserLookupService.ResolveHandleAsync`, `GetMembershipsAsync` (A2) ; DTO `Bot*` (A3).
- Produces: `GET /api/bot/players/{handle}` → `BotPlayerDto` ; `GET /api/bot/players/{handle}/history` → `BotHistoryDto`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Models;
using FluentAssertions;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>/api/bot/players/{handle} and its history (spec § 5.2).</summary>
[Collection(ApiCollection.Name)]
public class BotPlayerTests(ApiFactory factory)
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, string command)
    {
        var key = await CreateBotKeyAsync(factory);
        var response = await factory.CreateClient().SendAsync(Get(url, key, command));
        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task APlayer_ShowsTheProfile_AndOnlyCurrentOrgs()
    {
        var handle = NewHandle();
        var (current, former) = (NewSid(), NewSid());
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        await SeedAsync(factory, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, DisplayName = "The Pilot", Enlisted = new DateTime(2019, 9, 29, 0, 0, 0, DateTimeKind.Utc), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Organizations.Add(new Organization { Sid = current, Name = "Current Corp", Timestamp = DateTime.UtcNow, MembersCount = 3 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = current, UserHandle = handle, Timestamp = DateTime.UtcNow.AddDays(-40), IsActive = false, Rank = "Pilot", Stars = 1 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = current, UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true, Rank = "Captain", Stars = 3 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = former, UserHandle = handle, Timestamp = DateTime.UtcNow.AddDays(-100), IsActive = false, Rank = "Recruit" });
        });

        var (status, body) = await GetAsync($"/api/bot/players/{handle.ToUpperInvariant()}", "joueur");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("handle").GetString().Should().Be(handle);
        body.GetProperty("citizenId").GetInt32().Should().Be(citizenId);
        body.GetProperty("profileRead").GetBoolean().Should().BeTrue();
        var orgs = body.GetProperty("currentOrgs").EnumerateArray().ToList();
        orgs.Should().ContainSingle();
        orgs[0].GetProperty("sid").GetString().Should().Be(current);
        orgs[0].GetProperty("rank").GetString().Should().Be("Captain");
        orgs[0].GetProperty("stars").GetInt32().Should().Be(3);
        orgs[0].GetProperty("since").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(-40), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ARosterOnlyPlayer_IsAPartialAnswer()
    {
        var handle = NewHandle();
        var sid = NewSid();
        await SeedAsync(factory, db => db.OrganizationMembers.Add(new OrganizationMember { OrgSid = sid, UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true, DisplayName = "Roster Name" }));

        var (status, body) = await GetAsync($"/api/bot/players/{handle}", "joueur");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("profileRead").GetBoolean().Should().BeFalse();
        body.GetProperty("displayName").GetString().Should().Be("Roster Name");
    }

    [Fact]
    public async Task AnUnknownPlayer_IsA404()
    {
        (await GetAsync($"/api/bot/players/{NewHandle()}", "joueur")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheHistory_ListsEveryOrg_FormerHandles_AndRecentEvents()
    {
        var handle = NewHandle();
        var former = NewHandle();
        var (now, before) = (NewSid(), NewSid());
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        await SeedAsync(factory, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = former, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-2) });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = handle, FirstSeen = DateTime.UtcNow.AddMonths(-2), LastSeen = DateTime.UtcNow });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = now, UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = before, UserHandle = handle, Timestamp = DateTime.UtcNow.AddMonths(-3), IsActive = false });
            for (var i = 0; i < 20; i++)
                db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddMinutes(-i), EntityType = "member", EntityId = handle, ChangeType = "rank_changed", OrgSid = now, UserHandle = handle, OldValue = "a", NewValue = "b" });
        });

        var (status, body) = await GetAsync($"/api/bot/players/{handle}/history", "historique");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("orgs").EnumerateArray().Select(o => (o.GetProperty("sid").GetString(), o.GetProperty("active").GetBoolean()))
            .Should().Equal((now, true), (before, false));
        body.GetProperty("handles").EnumerateArray().Select(h => h.GetProperty("handle").GetString())
            .Should().BeEquivalentTo(new[] { former, handle });
        body.GetProperty("events").GetArrayLength().Should().Be(15);
    }

    [Fact]
    public async Task AFormerHandle_FindsTheCurrentPlayer()
    {
        var handle = NewHandle();
        var former = NewHandle();
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        await SeedAsync(factory, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = former, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-2) });
        });

        var (status, body) = await GetAsync($"/api/bot/players/{former}", "joueur");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("handle").GetString().Should().Be(handle);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~BotPlayerTests"`
Expected: FAIL (404 partout : les routes n'existent pas).

- [ ] **Step 3: Implement the two routes**

Dans `BotController` :

```csharp
    [HttpGet("players/{handle}")]
    public async Task<ActionResult<BotPlayerDto>> Player(string handle, CancellationToken ct)
    {
        var resolved = await users.ResolveHandleAsync(handle, ct)
            ?? throw new NotFoundException($"Joueur « {handle} » inconnu du tracker.");
        var user = await db.Users.AsNoTracking()
            .Where(u => u.UserHandle == resolved)
            .OrderByDescending(u => u.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        var rows = await users.GetMembershipsAsync(resolved, includeInactive: true, ct);
        if (user is null && rows.Count == 0)
            throw new NotFoundException($"Joueur « {handle} » inconnu du tracker.");

        var latestRow = rows.MaxBy(r => r.Latest.Timestamp);
        return Ok(new BotPlayerDto(
            resolved,
            user?.DisplayName ?? latestRow?.Latest.DisplayName,
            user?.CitizenId,
            user?.Enlisted,
            user?.Location,
            ProfileRead: user is not null,
            rows.Where(r => r.Latest.IsActive).OrderBy(r => r.Latest.OrgSid).Select(ToMembership).ToList(),
            latestRow?.Latest.Timestamp));
    }

    [HttpGet("players/{handle}/history")]
    public async Task<ActionResult<BotHistoryDto>> History(string handle, CancellationToken ct)
    {
        var resolved = await users.ResolveHandleAsync(handle, ct)
            ?? throw new NotFoundException($"Joueur « {handle} » inconnu du tracker.");
        var rows = await users.GetMembershipsAsync(resolved, includeInactive: true, ct);

        var citizenId = await db.Users.AsNoTracking()
            .Where(u => u.UserHandle == resolved)
            .Select(u => (int?)u.CitizenId)
            .FirstOrDefaultAsync(ct)
            ?? (await handleHistory.GetByHandleAsync(resolved, ct))?.CitizenId;
        var handles = citizenId is null
            ? new List<BotHandleDto>()
            : (await handleHistory.GetByCitizenIdAsync(citizenId.Value, ct))
                .Select(h => new BotHandleDto(h.UserHandle, h.FirstSeen, h.LastSeen)).ToList();
        var events = (await changes.GetByUserHandleAsync(resolved, HistoryEvents, ct))
            .Select(e => new BotEventDto(e.Timestamp, e.ChangeType, e.OrgSid, e.OldValue, e.NewValue)).ToList();

        return Ok(new BotHistoryDto(
            resolved,
            rows.OrderByDescending(r => r.Latest.IsActive).ThenByDescending(r => r.Latest.Timestamp).Select(ToMembership).ToList(),
            handles,
            events));
    }

    private static BotMembershipDto ToMembership(MembershipRow r) => new(
        r.Latest.OrgSid, r.OrgName, r.Latest.Rank, r.Latest.Stars, r.FirstSeen, r.Latest.Timestamp, r.Latest.IsActive);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~BotPlayerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Collector.Api/Controllers/BotController.cs src/Collector.Api.Tests/Bot/BotPlayerTests.cs
git commit -m "feat(api): /api/bot player profile and history"
```

### Task A5: fiche, membres et mouvements d'une org

**Files:**
- Modify: `src/Collector/Data/Repositories/ChangeEventRepository.cs` (requête statique `MovementsQuery`)
- Modify: `src/Collector.Api/Controllers/BotController.cs`
- Test: `src/Collector.Api.Tests/Bot/BotOrgTests.cs`
- Test: `src/Collector.Tests/Data/QueryPlanTests.cs` (nouveau test)

**Interfaces:**
- Produces:
  - `ChangeEventRepository.MovementsQuery(IQueryable<ChangeEvent> source, string orgSid, DateTime since, string changeType) : IQueryable<ChangeEvent>` ;
  - `GET /api/bot/orgs/{sid}`, `/members?page=`, `/movements?days=`.

- [ ] **Step 1: Write the failing tests**

Ajouter dans `src/Collector.Tests/Data/QueryPlanTests.cs` :

```csharp
    [Fact]
    public async Task OrgMovements_SeekTheOrgSidTimestampIndex()
    {
        var plan = await PlanAsync(ChangeEventRepository.MovementsQuery(
            _db.ChangeEvents, "TEST", DateTime.UtcNow.AddDays(-7), "member_left").Take(51));

        plan.Should().Contain("IX_change_events_OrgSid_Timestamp").And.NotContain("TEMP B-TREE");
    }
```

`src/Collector.Api.Tests/Bot/BotOrgTests.cs` :

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Models;
using FluentAssertions;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>/api/bot/orgs/{sid}, its members and its movements (spec § 5.2, § 5.5).</summary>
[Collection(ApiCollection.Name)]
public class BotOrgTests(ApiFactory factory)
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, string command)
    {
        var key = await CreateBotKeyAsync(factory);
        var response = await factory.CreateClient().SendAsync(Get(url, key, command));
        var body = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : default;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task AnOrg_ShowsItsListing_Counts_AndTrend()
    {
        var sid = NewSid();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Counted Corp", Timestamp = DateTime.UtcNow, MembersCount = 40, Archetype = "PMC", Lang = "French" });
            db.OrgMemberCounts.Add(new OrgMemberCount { OrgSid = sid, CollectedAt = DateTime.UtcNow.AddDays(-45), TotalRows = 30, VisibleCount = 25, RedactedCount = 3, HiddenCount = 2 });
            db.OrgMemberCounts.Add(new OrgMemberCount { OrgSid = sid, CollectedAt = DateTime.UtcNow.AddDays(-1), TotalRows = 40, VisibleCount = 34, RedactedCount = 4, HiddenCount = 2 });
        });

        var (status, body) = await GetAsync($"/api/bot/orgs/{sid}", "org");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("name").GetString().Should().Be("Counted Corp");
        var counts = body.GetProperty("counts");
        (counts.GetProperty("total").GetInt32(), counts.GetProperty("visible").GetInt32(),
            counts.GetProperty("redacted").GetInt32(), counts.GetProperty("hidden").GetInt32())
            .Should().Be((40, 34, 4, 2));
        var trend = body.GetProperty("trend30d");
        (trend.GetProperty("from").GetInt32(), trend.GetProperty("to").GetInt32()).Should().Be((30, 40));
    }

    [Fact]
    public async Task AnOrgSid_IsNormalized()
    {
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Normalized", Timestamp = DateTime.UtcNow, MembersCount = 1 }));

        (await GetAsync($"/api/bot/orgs/%20{sid.ToLowerInvariant()}%20", "org")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnUnknownOrg_IsA404_OnEveryRoute()
    {
        var sid = NewSid();
        (await GetAsync($"/api/bot/orgs/{sid}", "org")).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/api/bot/orgs/{sid}/members", "membres")).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync($"/api/bot/orgs/{sid}/movements", "mouvements")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Members_AreTheActiveOnes_ByPagesOf25_AndMaskedRowsAreNeverListed()
    {
        var sid = NewSid();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Paged", Timestamp = DateTime.UtcNow, MembersCount = 32 });
            for (var i = 0; i < 30; i++)
                db.OrganizationMembers.Add(new OrganizationMember { OrgSid = sid, UserHandle = $"member{i:00}", Timestamp = DateTime.UtcNow, IsActive = true, Stars = 2 });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = sid, UserHandle = "gone", Timestamp = DateTime.UtcNow.AddDays(-9), IsActive = false });
            // Two masked rows are counted only: org_member_counts carries them, rosters never do.
            db.OrgMemberCounts.Add(new OrgMemberCount { OrgSid = sid, CollectedAt = DateTime.UtcNow, TotalRows = 32, VisibleCount = 30, RedactedCount = 1, HiddenCount = 1 });
        });

        var (_, first) = await GetAsync($"/api/bot/orgs/{sid}/members", "membres");
        var (_, second) = await GetAsync($"/api/bot/orgs/{sid}/members?page=2", "membres");

        first.GetProperty("total").GetInt32().Should().Be(30);
        first.GetProperty("items").GetArrayLength().Should().Be(25);
        second.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("handle").GetString())
            .Should().Equal("member25", "member26", "member27", "member28", "member29");
    }

    [Fact]
    public async Task Movements_AreTheJoinsAndLeavesOfTheLastDays()
    {
        var sid = NewSid();
        await SeedAsync(factory, db =>
        {
            db.Organizations.Add(new Organization { Sid = sid, Name = "Moving", Timestamp = DateTime.UtcNow, MembersCount = 2 });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-2), EntityType = "member", EntityId = "newbie", ChangeType = "member_joined", OrgSid = sid, UserHandle = "newbie" });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-3), EntityType = "member", EntityId = "leaver", ChangeType = "member_left", OrgSid = sid, UserHandle = "leaver" });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-20), EntityType = "member", EntityId = "old", ChangeType = "member_left", OrgSid = sid, UserHandle = "old" });
            db.ChangeEvents.Add(new ChangeEvent { Timestamp = DateTime.UtcNow.AddDays(-1), EntityType = "member", EntityId = "ranked", ChangeType = "rank_changed", OrgSid = sid, UserHandle = "ranked" });
        });

        var (status, body) = await GetAsync($"/api/bot/orgs/{sid}/movements?days=7", "mouvements");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("joined").EnumerateArray().Select(m => m.GetProperty("handle").GetString()).Should().Equal("newbie");
        body.GetProperty("left").EnumerateArray().Select(m => m.GetProperty("handle").GetString()).Should().Equal("leaver");
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task MovementDaysOutsideTheirBounds_AreA400(int days)
    {
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Bounds", Timestamp = DateTime.UtcNow, MembersCount = 1 }));

        (await GetAsync($"/api/bot/orgs/{sid}/movements?days={days}", "mouvements")).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AMembersPageBelowOne_IsA400(int page)
    {
        var sid = NewSid();
        await SeedAsync(factory, db => db.Organizations.Add(new Organization { Sid = sid, Name = "Pages", Timestamp = DateTime.UtcNow, MembersCount = 1 }));

        (await GetAsync($"/api/bot/orgs/{sid}/members?page={page}", "membres")).Status.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~BotOrgTests"` puis `dotnet test src/Collector.Tests --filter "FullyQualifiedName~OrgMovements"`
Expected: compilation error (`MovementsQuery`), puis FAIL (les routes n'existent pas).

- [ ] **Step 3: Add the movements query**

Dans `ChangeEventRepository` (projet `Collector`) :

```csharp
    /// <summary>
    /// One type of membership event of an org since a date, newest first, through the
    /// (OrgSid, Timestamp) index; the type is filtered on the rows it returns.
    /// </summary>
    public static IQueryable<ChangeEvent> MovementsQuery(
        IQueryable<ChangeEvent> source, string orgSid, DateTime since, string changeType)
        => source
            .Where(c => c.OrgSid == orgSid && c.Timestamp >= since && c.ChangeType == changeType)
            .OrderByDescending(c => c.Timestamp);
```

Lancer le test de plan. S'il montre que SQLite choisit `IX_change_events_ChangeType_Timestamp` et non `IX_change_events_OrgSid_Timestamp`, empêcher l'usage de cet index en écrivant le filtre sur le type `c.ChangeType + "" == changeType`, puis relancer.

- [ ] **Step 4: Implement the three org routes**

Dans `BotController` :

```csharp
    [HttpGet("orgs/{sid}")]
    public async Task<ActionResult<BotOrgDto>> Org(string sid, CancellationToken ct)
    {
        sid = NormalizeSid(sid);
        var org = await db.Organizations.AsNoTracking()
            .Where(o => o.Sid == sid)
            .OrderByDescending(o => o.Timestamp)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Organisation « {sid} » inconnue du tracker.");

        var latest = await db.OrgMemberCounts.AsNoTracking()
            .Where(c => c.OrgSid == sid)
            .OrderByDescending(c => c.CollectedAt)
            .FirstOrDefaultAsync(ct);
        BotTrendDto? trend = null;
        if (latest != null)
        {
            // RSI's total 30 days ago: the last count at that date, else the oldest one.
            var monthAgo = DateTime.UtcNow.AddDays(-30);
            var baseline = await db.OrgMemberCounts.AsNoTracking()
                    .Where(c => c.OrgSid == sid && c.CollectedAt <= monthAgo)
                    .OrderByDescending(c => c.CollectedAt)
                    .FirstOrDefaultAsync(ct)
                ?? await db.OrgMemberCounts.AsNoTracking()
                    .Where(c => c.OrgSid == sid)
                    .OrderBy(c => c.CollectedAt)
                    .FirstAsync(ct);
            trend = new BotTrendDto(baseline.TotalRows, latest.TotalRows);
        }
        var readAt = await db.DiscoveredOrganizations.AsNoTracking()
            .Where(d => d.Sid == sid)
            .Select(d => d.LastMembersCollectedAt)
            .FirstOrDefaultAsync(ct);

        return Ok(new BotOrgDto(
            org.Sid, org.Name, org.Archetype, org.Lang, org.Recruiting, org.Roleplay, org.MembersCount,
            latest is null ? null : new BotCountsDto(latest.TotalRows, latest.VisibleCount, latest.RedactedCount, latest.HiddenCount, latest.CollectedAt),
            trend,
            readAt));
    }

    [HttpGet("orgs/{sid}/members")]
    public async Task<ActionResult<BotMembersPageDto>> Members(string sid, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (page < 1)
            throw new ValidationException("La page commence à 1.");
        sid = await KnownSidAsync(sid, ct);
        var (items, total) = await members.GetLatestPageAsync(sid, true, page, MembersPageSize, ct);

        // "Since": first appearance of each member of the page in this org.
        var handles = items.Select(i => i.UserHandle).ToList();
        var since = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrgSid == sid && handles.Contains(m.UserHandle))
            .GroupBy(m => m.UserHandle)
            .Select(g => new { Handle = g.Key, First = g.Min(m => m.Timestamp) })
            .ToDictionaryAsync(x => x.Handle, x => x.First, ct);

        return Ok(new BotMembersPageDto(sid, page, MembersPageSize, total, items
            .Select(m => new BotMemberDto(m.UserHandle, m.DisplayName, m.Rank, m.Stars,
                since.TryGetValue(m.UserHandle, out var first) ? first : null))
            .ToList()));
    }

    [HttpGet("orgs/{sid}/movements")]
    public async Task<ActionResult<BotMovementsDto>> Movements(string sid, [FromQuery] int days = 7, CancellationToken ct = default)
    {
        if (days is < 1 or > 90)
            throw new ValidationException("Le nombre de jours va de 1 à 90.");
        sid = await KnownSidAsync(sid, ct);
        var since = DateTime.UtcNow.AddDays(-days);

        async Task<List<BotMovementDto>> ReadAsync(string type) =>
            (await ChangeEventRepository.MovementsQuery(db.ChangeEvents.AsNoTracking(), sid, since, type)
                .Take(MaxMovements + 1)
                .ToListAsync(ct))
            .Select(e => new BotMovementDto(e.UserHandle ?? e.EntityId, e.Timestamp))
            .ToList();

        var joined = await ReadAsync("member_joined");
        var left = await ReadAsync("member_left");
        var truncated = joined.Count > MaxMovements || left.Count > MaxMovements;
        return Ok(new BotMovementsDto(sid, days, joined.Take(MaxMovements).ToList(), left.Take(MaxMovements).ToList(), truncated));
    }

    private static string NormalizeSid(string sid)
    {
        var normalized = sid.Trim().ToUpperInvariant();
        if (normalized.Length == 0) throw new ValidationException("SID vide.");
        return normalized;
    }

    private async Task<string> KnownSidAsync(string sid, CancellationToken ct)
    {
        var normalized = NormalizeSid(sid);
        if (!await db.Organizations.AsNoTracking().AnyAsync(o => o.Sid == normalized, ct))
            throw new NotFoundException($"Organisation « {normalized} » inconnue du tracker.");
        return normalized;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~Bot"` puis `dotnet test src/Collector.Tests --filter "FullyQualifiedName~QueryPlan"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Collector/Data/Repositories/ChangeEventRepository.cs src/Collector.Api/Controllers/BotController.cs src/Collector.Api.Tests/Bot/BotOrgTests.cs src/Collector.Tests/Data/QueryPlanTests.cs
git commit -m "feat(api): /api/bot org profile, members and movements"
```

### Task A6: clé du bot créée par un administrateur

Le site ne gère plus les clés d'API, et le compte du bot ne se connecte jamais. Un administrateur doit donc pouvoir créer une clé **à portée** pour un autre compte.

**Files:**
- Modify: `src/Collector.Api/Controllers/AdminController.cs`
- Test: `src/Collector.Api.Tests/Bot/AdminScopedKeyTests.cs`

**Interfaces:**
- Produces: `POST /api/admin/users/{id}/api-keys` (corps `CreateApiKeyRequest`) → 201 `CreatedApiKeyDto`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Data;
using Collector.Api.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Collector.Api.Tests.Bot.BotTestSupport;

namespace Collector.Api.Tests.Bot;

/// <summary>An administrator creates the bot account's scoped key (spec § 5.1).</summary>
[Collection(ApiCollection.Name)]
public class AdminScopedKeyTests(ApiFactory factory)
{
    private async Task<long> NewAccountAsync()
    {
        var username = $"bot-account-{Guid.NewGuid():N}";
        await factory.CreateAccountAsync(username, "correct horse battery");
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiDbContext>().ApiUsers
            .Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    private static object BotKeyRequest(string? scope = ApiKeyScopes.BotRead)
        => new { name = "liberastra-bot", expiresAt = DateTime.UtcNow.AddDays(365), scope };

    [Fact]
    public async Task AnAdmin_CreatesAScopedKey_ForAnotherAccount()
    {
        var botId = await NewAccountAsync();
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var rawKey = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rawKey").GetString();
        (await factory.CreateClient().SendAsync(Get("/api/bot/search?q=ab", rawKey, "recherche"))).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AFullKey_CannotBeCreated_ForAnotherAccount()
    {
        var botId = await NewAccountAsync();
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        (await admin.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest(scope: null))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ANonAdmin_IsForbidden()
    {
        var botId = await NewAccountAsync();
        var user = await factory.SignedInClientAsync($"user-{Guid.NewGuid():N}");

        (await user.PostAsJsonAsync($"/api/admin/users/{botId}/api-keys", BotKeyRequest())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnUnknownAccount_IsA404()
    {
        var admin = await factory.SignedInClientAsync($"admin-{Guid.NewGuid():N}", isAdmin: true);

        (await admin.PostAsJsonAsync("/api/admin/users/999999999/api-keys", BotKeyRequest())).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~AdminScopedKeyTests"`
Expected: FAIL (405 ou 404 : la route n'existe pas).

- [ ] **Step 3: Add the route**

Dans `AdminController` : ajouter `ApiKeyService apiKeyService` et `CurrentUserAccessor currentUser` au constructeur (champs `_apiKeyService`, `_currentUser`), et les `using Collector.Api.Auth;` et `using Collector.Api.Dtos.ApiKeys;`.

```csharp
    /// <summary>
    /// A scoped key for another account, for a service that never signs in (the Discord
    /// bot's bot:read key). Full keys stay created by their owner only.
    /// </summary>
    [HttpPost("users/{id:long}/api-keys")]
    public async Task<ActionResult<CreatedApiKeyDto>> CreateScopedKey(
        long id, [FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        if (request.Scope is null)
            throw new ValidationException("Seule une clé à portée peut être créée pour un autre compte.");
        if (!await _db.ApiUsers.AnyAsync(u => u.Id == id, ct))
            throw new NotFoundException($"Compte {id} introuvable.");

        var (rawKey, dto) = await _apiKeyService.CreateAsync(id, request, ct);
        await _activityLog.LogAsync("admin_create_scoped_key", _currentUser.UserId, "user", id.ToString(),
            _currentUser.IpAddress, ct);

        var result = new CreatedApiKeyDto
        {
            Id = dto.Id,
            Name = dto.Name,
            KeyPrefix = dto.KeyPrefix,
            CreatedAt = dto.CreatedAt,
            ExpiresAt = dto.ExpiresAt,
            IsRevoked = dto.IsRevoked,
            Scope = dto.Scope,
            RawKey = rawKey,
        };
        return StatusCode(StatusCodes.Status201Created, result);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~AdminScopedKeyTests|FullyQualifiedName~Admin"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Collector.Api/Controllers/AdminController.cs src/Collector.Api.Tests/Bot/AdminScopedKeyTests.cs
git commit -m "feat(api): administrators create scoped keys for service accounts"
```

### Task A7: nginx, documentation et suite complète

**Files:**
- Modify: `deploy/nginx/sc-tracker.conf`
- Modify: `deploy/README.md` (nouvelle section « Bot Discord Liberastra »)

- [ ] **Step 1: Add the nginx block**

Dans `deploy/nginx/sc-tracker.conf` : la zone en tête, à côté de `sc_discord_ingest`, et le bloc `location` juste après celui de `/ingest/discord/`.

```nginx
# Read routes of the Liberastra Discord bot (location /bot-api/ below): panda only.
limit_req_zone $binary_remote_addr zone=sc_bot_api:1m rate=30r/m;
```

```nginx
    # Liberastra Discord bot (server panda): bot:read key, this IP only (spec 2026-10-03 § 5.4).
    location /bot-api/ {
        allow 185.146.193.199;
        deny all;
        limit_req zone=sc_bot_api burst=10 nodelay;
        limit_req_status 429;
        proxy_pass http://127.0.0.1:5000/api/bot/;
    }
```

- [ ] **Step 2: Document the setup**

Ajouter à `deploy/README.md` :

```markdown
## Bot Discord Liberastra (`/bot-api/`)

Le bot Liberastra (serveur `panda`, 185.146.193.199) lit le tracker par `/bot-api/`, réservé à
cette IP par nginx, avec une clé à portée `bot:read` (valable sur `/api/bot/` seulement,
365 jours au plus). Mise en place :

1. Compte dédié non administrateur `liberastra-bot` (mot de passe aléatoire, jamais utilisé).
2. Clé : `POST /api/admin/users/<id>/api-keys` avec
   `{"name":"liberastra-bot","expiresAt":"<date à moins d'un an>","scope":"bot:read"}`
   (clé d'administration de `api.env`, depuis le serveur). La clé n'est affichée qu'une fois :
   la copier directement dans `/root/discord/Liberastra-Bot-Discord/.env` de `panda`
   (`TrackerApi__ApiKey`).
3. Empreinte du certificat pour `TrackerApi__CertSha256` :
   `openssl x509 -in <certificat nginx> -noout -fingerprint -sha256`.
4. Renouvellement : avant l'expiration, nouvelle clé, mise à jour du `.env`, redémarrage du bot,
   puis révocation de l'ancienne. Si l'IP de `panda` change, mettre à jour `allow` dans le bloc
   `/bot-api/`.
```

- [ ] **Step 3: Run the full suites**

Run: `dotnet test src/Collector.Tests` puis `dotnet test src/Collector.Api.Tests`
Expected: PASS (aucun échec sur l'ensemble).

- [ ] **Step 4: Commit**

```bash
git add deploy/nginx/sc-tracker.conf deploy/README.md
git commit -m "feat(deploy): /bot-api/ for the Liberastra bot, panda's IP only"
```

---

# Partie B — bot (`Liberastra-Bot-Discord`)

Toutes les commandes s'exécutent dans `C:\Users\pc.DESKTOP-DQ6SVVV\Downloads\Liberastra-Bot-Discord`.

### Task B1: aligner le dépôt sur le code de `panda`

Le code en service sur `panda` diffère de GitHub (13ec14a) par cinq fichiers. Ils doivent entrer dans le dépôt avant tout développement.

**Files (copiés depuis `panda`):**
- `Commands/SlashCommands.cs`
- `Program.cs`
- `Services/AuditLogGeminiService.cs`
- `Services/AuditLogFunctionTool.cs`
- `deploy.sh`

- [ ] **Step 1: Create the branch and copy the files**

```bash
git checkout -b server-sync 13ec14a
for f in Commands/SlashCommands.cs Program.cs Services/AuditLogGeminiService.cs Services/AuditLogFunctionTool.cs deploy.sh; do scp -q "panda:/root/discord/Liberastra-Bot-Discord/$f" "$f"; done
git status --short
```

Expected: 2 fichiers modifiés (`SlashCommands.cs`, `Program.cs`), 3 nouveaux. Les différences de fins de ligne (CRLF) ne comptent pas.

- [ ] **Step 2: Build**

Run: `dotnet build BotLiberastra.csproj -c Release`
Expected: Build succeeded, 0 erreur.

- [ ] **Step 3: Commit**

```bash
git add Commands/SlashCommands.cs Program.cs Services/AuditLogGeminiService.cs Services/AuditLogFunctionTool.cs deploy.sh
git commit -m "chore: sync the code running on panda (audit-log analyst, deploy.sh)"
```

Le push de `server-sync` sur GitHub fait partie de la partie C et attend le « oui » de l'utilisateur.

### Task B2: projet de tests, réglages et épinglage du certificat

**Files:**
- Create: `BotLiberastra.Tests/BotLiberastra.Tests.csproj`
- Modify: `BotLiberastra.csproj` (exclure le dossier de tests)
- Modify: `BotLiberastra.slnx` (ajouter le projet de tests)
- Modify: `Configuration/BotConfig.cs` (`TrackerApiConfig`)
- Create: `Services/Tracker/CertificatePinning.cs`
- Test: `BotLiberastra.Tests/CertificatePinningTests.cs`

**Interfaces:**
- Produces:
  - `TrackerApiConfig { BaseUrl, ApiKey, CertSha256, IsComplete }` ;
  - `CertificatePinning.Matches(X509Certificate2? certificate, string expectedSha256) : bool`.

- [ ] **Step 1: Create the test project**

```bash
git checkout -b tracker-commands server-sync
mkdir BotLiberastra.Tests
```

`BotLiberastra.Tests/BotLiberastra.Tests.csproj` :

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <!-- The bot is published self-contained (PublishSingleFile); the test host still references it. -->
    <ValidateExecutableReferencesMatchSelfContained>false</ValidateExecutableReferencesMatchSelfContained>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\BotLiberastra.csproj" />
  </ItemGroup>

</Project>
```

Dans `BotLiberastra.csproj`, le projet du bot englobe tous les `.cs` sous sa racine : il faut en exclure les tests.

```xml
  <ItemGroup>
    <Compile Remove="BotLiberastra.Tests/**" />
    <None Remove="BotLiberastra.Tests/**" />
  </ItemGroup>
```

```bash
dotnet sln BotLiberastra.slnx add BotLiberastra.Tests/BotLiberastra.Tests.csproj
```

- [ ] **Step 2: Write the failing test**

`BotLiberastra.Tests/CertificatePinningTests.cs` :

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BotLiberastra.Services.Tracker;
using Xunit;

namespace BotLiberastra.Tests;

/// <summary>The tracker's self-signed certificate is trusted by its exact fingerprint only (spec § 6.2).</summary>
public class CertificatePinningTests
{
    private static X509Certificate2 NewCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    [Fact]
    public void TheConfiguredFingerprint_IsAccepted_WithOrWithoutColonsAndInAnyCase()
    {
        using var certificate = NewCertificate("tracker");
        var hex = Fingerprint(certificate);
        var colons = string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2))).ToLowerInvariant();

        Assert.True(CertificatePinning.Matches(certificate, hex));
        Assert.True(CertificatePinning.Matches(certificate, colons));
    }

    [Fact]
    public void AnotherCertificate_IsRefused()
    {
        using var expected = NewCertificate("tracker");
        using var other = NewCertificate("someone-else");

        Assert.False(CertificatePinning.Matches(other, Fingerprint(expected)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    public void AMissingOrShortFingerprint_RefusesEverything(string configured)
    {
        using var certificate = NewCertificate("tracker");

        Assert.False(CertificatePinning.Matches(certificate, configured));
        Assert.False(CertificatePinning.Matches(null, Fingerprint(certificate)));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test BotLiberastra.Tests`
Expected: compilation error (`CertificatePinning` n'existe pas).

- [ ] **Step 4: Implement**

Dans `Configuration/BotConfig.cs` :

```csharp
/// <summary>The tracker's /bot-api/ (spec 2026-10-03 § 6.1); incomplete settings turn /tracker off.</summary>
public class TrackerApiConfig
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string CertSha256 { get; set; } = "";

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(CertSha256);
}
```

`Services/Tracker/CertificatePinning.cs` :

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BotLiberastra.Services.Tracker;

/// <summary>
/// The tracker's certificate is self-signed and reached by IP: it is trusted when its
/// SHA-256 fingerprint is exactly the configured one, and never otherwise.
/// </summary>
public static class CertificatePinning
{
    public static bool Matches(X509Certificate2? certificate, string expectedSha256)
    {
        if (certificate is null) return false;
        var expected = Normalize(expectedSha256);
        if (expected.Length != 64) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
    }

    /// <summary>Hex digits only, upper case: "ab:cd" and "ABCD" are the same fingerprint.</summary>
    public static string Normalize(string value) => new string(value.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
}
```

- [ ] **Step 5: Run the tests and the build**

Run: `dotnet test BotLiberastra.Tests` puis `dotnet build BotLiberastra.csproj -c Release`
Expected: PASS ; le bot compile sans inclure les tests.

- [ ] **Step 6: Commit**

```bash
git add BotLiberastra.Tests BotLiberastra.csproj BotLiberastra.slnx Configuration/BotConfig.cs Services/Tracker/CertificatePinning.cs
git commit -m "feat(tracker): test project, tracker settings and certificate pinning"
```

### Task B3: client du tracker

**Files:**
- Create: `Services/Tracker/TrackerDtos.cs`
- Create: `Services/Tracker/TrackerResult.cs`
- Create: `Services/Tracker/TrackerApiClient.cs`
- Test: `BotLiberastra.Tests/TrackerApiClientTests.cs`, `BotLiberastra.Tests/FakeHandler.cs`

**Interfaces:**
- Consumes: `TrackerApiConfig` (B2).
- Produces:
  - `TrackerApiClient.IsConfigured` ;
  - `SearchAsync(string text, ulong discordUserId, string command, CancellationToken ct = default)` ;
  - `PlayerAsync(string handle, ulong discordUserId, ...)`, `HistoryAsync(...)`, `OrgAsync(string sid, ...)`, `MembersAsync(string sid, int page, ...)`, `MovementsAsync(string sid, int days, ...)` ;
  - chacun renvoie `Task<TrackerResult<T>>` ;
  - `TrackerOutcome { Ok, NotFound, RateLimited, Invalid, Unavailable, Refused, NotConfigured }` : `Refused` = clé refusée (401/403 : clé révoquée ou expirée, IP de `panda` refusée par nginx) ou certificat d'une autre empreinte. Un administrateur doit intervenir : journal en erreur.

- [ ] **Step 1: Write the failing tests**

`BotLiberastra.Tests/FakeHandler.cs` :

```csharp
using System.Net;
using System.Text;

namespace BotLiberastra.Tests;

/// <summary>Answers each request with the given function and keeps the requests.</summary>
public sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
```

`BotLiberastra.Tests/TrackerApiClientTests.cs` :

```csharp
using System.Net;
using BotLiberastra.Configuration;
using BotLiberastra.Services.Tracker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BotLiberastra.Tests;

public class TrackerApiClientTests
{
    private static readonly TrackerApiConfig Config = new()
    {
        BaseUrl = "https://203.0.113.10/bot-api/", ApiKey = "bot-key", CertSha256 = new string('A', 64),
    };

    private static TrackerApiClient Client(FakeHandler handler, TrackerApiConfig? config = null)
        => new(new HttpClient(handler), Options.Create(config ?? Config), NullLogger<TrackerApiClient>.Instance);

    [Fact]
    public async Task ARequest_CarriesTheKey_TheDiscordUser_AndTheCommand()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"orgs":[],"players":[]}"""));

        await Client(handler).SearchAsync("liber", 123456789012345678, "recherche");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://203.0.113.10/bot-api/search?q=liber", request.RequestUri!.ToString());
        Assert.Equal("bot-key", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("123456789012345678", request.Headers.GetValues("X-Discord-User").Single());
        Assert.Equal("recherche", request.Headers.GetValues("X-Bot-Command").Single());
    }

    [Fact]
    public async Task APlayer_IsRead()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""
            {"handle":"ErunDhyr","displayName":"ErunDhyr","citizenId":2373025,"enlisted":"2019-09-29T00:00:00Z",
             "location":null,"profileRead":true,"lastSeen":"2026-10-01T00:00:00Z",
             "currentOrgs":[{"sid":"TOOLINEAR","name":"Too Linear","rank":"Pilot","stars":2,
                             "since":"2026-02-23T00:00:00Z","lastSeen":"2026-10-01T00:00:00Z","active":true}]}
            """));

        var result = await Client(handler).PlayerAsync("erundhyr", 1);

        Assert.Equal(TrackerOutcome.Ok, result.Outcome);
        Assert.Equal(2373025, result.Value!.CitizenId);
        Assert.Equal("TOOLINEAR", Assert.Single(result.Value.CurrentOrgs).Sid);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, TrackerOutcome.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, TrackerOutcome.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, TrackerOutcome.Invalid)]
    [InlineData(HttpStatusCode.Unauthorized, TrackerOutcome.Refused)]
    [InlineData(HttpStatusCode.Forbidden, TrackerOutcome.Refused)]
    [InlineData(HttpStatusCode.BadGateway, TrackerOutcome.Unavailable)]
    public async Task StatusCodes_BecomeOutcomes(HttpStatusCode status, TrackerOutcome expected)
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"title":"x"}""", status));

        Assert.Equal(expected, (await Client(handler).OrgAsync("TEST", 1)).Outcome);
    }

    [Fact]
    public async Task FailuresBecomeOutcomes()
    {
        // A certificate whose fingerprint is not the configured one fails the TLS handshake.
        var otherCertificate = new FakeHandler(_ => throw new HttpRequestException(
            "SSL connection could not be established", new System.Security.Authentication.AuthenticationException("remote certificate rejected")));
        var unreachable = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var slow = new FakeHandler(_ => throw new TaskCanceledException("timeout"));
        var garbled = new FakeHandler(_ => FakeHandler.Json("not json"));

        Assert.Equal(TrackerOutcome.Refused, (await Client(otherCertificate).OrgAsync("TEST", 1)).Outcome);
        Assert.Equal(TrackerOutcome.Unavailable, (await Client(unreachable).OrgAsync("TEST", 1)).Outcome);
        Assert.Equal(TrackerOutcome.Unavailable, (await Client(slow).OrgAsync("TEST", 1)).Outcome);
        Assert.Equal(TrackerOutcome.Unavailable, (await Client(garbled).OrgAsync("TEST", 1)).Outcome);
    }

    [Fact]
    public async Task IncompleteSettings_SendNothing()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("{}"));

        var result = await Client(handler, new TrackerApiConfig()).OrgAsync("TEST", 1);

        Assert.Equal(TrackerOutcome.NotConfigured, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PathSegments_AreEscaped()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"sid":"A","page":2,"pageSize":25,"total":0,"items":[]}"""));

        await Client(handler).MembersAsync("A/../B", 2, 1);

        Assert.Equal("https://203.0.113.10/bot-api/orgs/A%2F..%2FB/members?page=2", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test BotLiberastra.Tests --filter "FullyQualifiedName~TrackerApiClientTests"`
Expected: compilation error.

- [ ] **Step 3: Implement**

`Services/Tracker/TrackerDtos.cs` :

```csharp
namespace BotLiberastra.Services.Tracker;

// Answers of the tracker's /api/bot routes (spec 2026-10-03 § 5.2), read case-insensitively.
public sealed record TrackerSearch(IReadOnlyList<TrackerOrgHit> Orgs, IReadOnlyList<TrackerPlayerHit> Players);
public sealed record TrackerOrgHit(string Sid, string Name, int MembersCount);
public sealed record TrackerPlayerHit(string Handle, string? DisplayName);

public sealed record TrackerMembership(string Sid, string? Name, string? Rank, int? Stars, DateTime? Since, DateTime LastSeen, bool Active);
public sealed record TrackerPlayer(
    string Handle, string? DisplayName, int? CitizenId, DateTime? Enlisted, string? Location,
    bool ProfileRead, IReadOnlyList<TrackerMembership> CurrentOrgs, DateTime? LastSeen);
public sealed record TrackerHandle(string Handle, DateTime FirstSeen, DateTime LastSeen);
public sealed record TrackerEvent(DateTime At, string Type, string? OrgSid, string? Old, string? New);
public sealed record TrackerHistory(
    string Handle, IReadOnlyList<TrackerMembership> Orgs, IReadOnlyList<TrackerHandle> Handles, IReadOnlyList<TrackerEvent> Events);

public sealed record TrackerCounts(int Total, int? Visible, int? Redacted, int? Hidden, DateTime At);
public sealed record TrackerTrend(int From, int To);
public sealed record TrackerOrg(
    string Sid, string Name, string? Archetype, string? Lang, bool? Recruiting, bool? Roleplay, int MembersCount,
    TrackerCounts? Counts, TrackerTrend? Trend30d, DateTime? MembersReadAt);
public sealed record TrackerMember(string Handle, string? DisplayName, string? Rank, int? Stars, DateTime? Since);
public sealed record TrackerMembersPage(string Sid, int Page, int PageSize, int Total, IReadOnlyList<TrackerMember> Items);
public sealed record TrackerMovement(string Handle, DateTime At);
public sealed record TrackerMovements(string Sid, int Days, IReadOnlyList<TrackerMovement> Joined, IReadOnlyList<TrackerMovement> Left, bool Truncated);
```

`Services/Tracker/TrackerResult.cs` :

```csharp
namespace BotLiberastra.Services.Tracker;

/// <summary>
/// Refused: the tracker refuses the bot (revoked or expired key, panda's IP not allowed) or
/// serves another certificate; an administrator must act. Unavailable: try again later.
/// </summary>
public enum TrackerOutcome { Ok, NotFound, RateLimited, Invalid, Unavailable, Refused, NotConfigured }

/// <summary>What a /tracker command shows: a value, or why there is none. Never an exception.</summary>
public sealed record TrackerResult<T>(TrackerOutcome Outcome, T? Value, string? Detail)
{
    public static TrackerResult<T> Ok(T value) => new(TrackerOutcome.Ok, value, null);
    public static TrackerResult<T> Fail(TrackerOutcome outcome, string? detail = null) => new(outcome, default, detail);
}
```

`Services/Tracker/TrackerApiClient.cs` :

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotLiberastra.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotLiberastra.Services.Tracker;

/// <summary>
/// The tracker's /bot-api/ (spec 2026-10-03 § 6.2). The primary handler, set in Program.cs,
/// trusts only the configured certificate fingerprint; failures come back as outcomes.
/// </summary>
public sealed class TrackerApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ILogger<TrackerApiClient> _logger;

    public TrackerApiClient(HttpClient http, IOptions<TrackerApiConfig> config, ILogger<TrackerApiClient> logger)
    {
        _http = http;
        _logger = logger;
        IsConfigured = config.Value.IsComplete;
        if (!IsConfigured) return;
        _http.BaseAddress = new Uri(config.Value.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.Add("x-api-key", config.Value.ApiKey);
    }

    public bool IsConfigured { get; }

    public Task<TrackerResult<TrackerSearch>> SearchAsync(string text, ulong discordUserId, string command, CancellationToken ct = default)
        => GetAsync<TrackerSearch>($"search?q={Uri.EscapeDataString(text)}", discordUserId, command, ct);

    public Task<TrackerResult<TrackerPlayer>> PlayerAsync(string handle, ulong discordUserId, CancellationToken ct = default)
        => GetAsync<TrackerPlayer>($"players/{Uri.EscapeDataString(handle)}", discordUserId, "joueur", ct);

    public Task<TrackerResult<TrackerHistory>> HistoryAsync(string handle, ulong discordUserId, CancellationToken ct = default)
        => GetAsync<TrackerHistory>($"players/{Uri.EscapeDataString(handle)}/history", discordUserId, "historique", ct);

    public Task<TrackerResult<TrackerOrg>> OrgAsync(string sid, ulong discordUserId, CancellationToken ct = default)
        => GetAsync<TrackerOrg>($"orgs/{Uri.EscapeDataString(sid)}", discordUserId, "org", ct);

    public Task<TrackerResult<TrackerMembersPage>> MembersAsync(string sid, int page, ulong discordUserId, CancellationToken ct = default)
        => GetAsync<TrackerMembersPage>(
            $"orgs/{Uri.EscapeDataString(sid)}/members?page={page.ToString(CultureInfo.InvariantCulture)}", discordUserId, "membres", ct);

    public Task<TrackerResult<TrackerMovements>> MovementsAsync(string sid, int days, ulong discordUserId, CancellationToken ct = default)
        => GetAsync<TrackerMovements>(
            $"orgs/{Uri.EscapeDataString(sid)}/movements?days={days.ToString(CultureInfo.InvariantCulture)}", discordUserId, "mouvements", ct);

    private async Task<TrackerResult<T>> GetAsync<T>(string path, ulong discordUserId, string command, CancellationToken ct)
    {
        if (!IsConfigured) return TrackerResult<T>.Fail(TrackerOutcome.NotConfigured);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Discord-User", discordUserId.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Bot-Command", command);
        try
        {
            using var response = await _http.SendAsync(request, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
                    return value is null
                        ? TrackerResult<T>.Fail(TrackerOutcome.Unavailable, "réponse vide")
                        : TrackerResult<T>.Ok(value);
                case HttpStatusCode.NotFound:
                    _logger.LogInformation("Tracker {Path}: not found", path);
                    return TrackerResult<T>.Fail(TrackerOutcome.NotFound);
                case HttpStatusCode.TooManyRequests:
                    _logger.LogInformation("Tracker {Path}: rate limited", path);
                    return TrackerResult<T>.Fail(TrackerOutcome.RateLimited);
                case HttpStatusCode.BadRequest:
                    return TrackerResult<T>.Fail(TrackerOutcome.Invalid);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    // A revoked or expired key, or nginx refusing panda's IP: an admin must act.
                    _logger.LogError("Tracker refused {Path}: HTTP {Status} (key or allowed IP)", path, (int)response.StatusCode);
                    return TrackerResult<T>.Fail(TrackerOutcome.Refused, $"HTTP {(int)response.StatusCode}");
                default:
                    _logger.LogWarning("Tracker {Path}: HTTP {Status}", path, (int)response.StatusCode);
                    return TrackerResult<T>.Fail(TrackerOutcome.Unavailable, $"HTTP {(int)response.StatusCode}");
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Tracker {Path}: no answer within 10 s", path);
            return TrackerResult<T>.Fail(TrackerOutcome.Unavailable, "délai dépassé");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            // The TLS handshake failed: the VPS serves a certificate of another fingerprint.
            _logger.LogError(ex, "Tracker {Path}: certificate refused (TrackerApi__CertSha256)", path);
            return TrackerResult<T>.Fail(TrackerOutcome.Refused, "certificat");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Tracker {Path}: request failed", path);
            return TrackerResult<T>.Fail(TrackerOutcome.Unavailable, ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Tracker {Path}: unreadable answer", path);
            return TrackerResult<T>.Fail(TrackerOutcome.Unavailable, "réponse illisible");
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test BotLiberastra.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/Tracker/TrackerDtos.cs Services/Tracker/TrackerResult.cs Services/Tracker/TrackerApiClient.cs BotLiberastra.Tests/FakeHandler.cs BotLiberastra.Tests/TrackerApiClientTests.cs
git commit -m "feat(tracker): client of the tracker's /bot-api/, failures as outcomes"
```

### Task B4: table des accès et règle d'accès

**Files:**
- Modify: `Services/Models/BotModels.cs` (entité `TrackerCommandAccessEntity`)
- Modify: `Services/DatabaseService.cs` (`DbSet` et configuration)
- Create: `Migrations/<horodatage>_AddTrackerCommandAccess.cs` et `.Designer.cs` (générés)
- Create: `Services/Tracker/TrackerCommandNames.cs`
- Create: `Services/Tracker/TrackerAccessService.cs`
- Test: `BotLiberastra.Tests/InMemoryBotDb.cs`, `BotLiberastra.Tests/TrackerAccessServiceTests.cs`

**Interfaces:**
- Produces:
  - `TrackerCommandNames.All` (les six noms) et ses constantes `Joueur`, `Historique`, `Recherche`, `Org`, `Membres`, `Mouvements` ;
  - `TrackerAccessService.IsAllowedAsync(ulong guildId, string command, IReadOnlyCollection<ulong> roleIds, bool isAdministrator, CancellationToken ct = default) : Task<bool>` ;
  - `GrantAsync(ulong guildId, string command, ulong roleId, ulong grantedBy, ...) : Task<bool>` (faux si déjà accordé) ;
  - `RevokeAsync(ulong guildId, string command, ulong roleId, ...) : Task<bool>` ;
  - `ListAsync(ulong guildId, ...) : Task<IReadOnlyDictionary<string, IReadOnlyList<ulong>>>` (les six clés, toujours).

- [ ] **Step 1: Write the failing tests**

`BotLiberastra.Tests/InMemoryBotDb.cs` :

```csharp
using BotLiberastra.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BotLiberastra.Tests;

/// <summary>The bot's database, migrated, in memory for one test.</summary>
public sealed class InMemoryBotDb : IDbContextFactory<BotDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public InMemoryBotDb()
    {
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public BotDbContext CreateDbContext() => new(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
```

`BotLiberastra.Tests/TrackerAccessServiceTests.cs` :

```csharp
using BotLiberastra.Services.Tracker;
using Xunit;

namespace BotLiberastra.Tests;

/// <summary>Who may run each /tracker subcommand (spec § 6.4).</summary>
public sealed class TrackerAccessServiceTests : IDisposable
{
    private const ulong Guild = 111111111111111111;
    private const ulong OtherGuild = 222222222222222222;
    private const ulong Recruiter = 333333333333333333;
    private const ulong Member = 444444444444444444;
    private const ulong Admin = 555555555555555555;

    private readonly InMemoryBotDb _db = new();
    private TrackerAccessService Access => new(_db);

    [Fact]
    public async Task AnAdministrator_AlwaysPasses_EvenWithNothingOpened()
    {
        Assert.True(await Access.IsAllowedAsync(Guild, TrackerCommandNames.Historique, [], isAdministrator: true));
    }

    [Fact]
    public async Task ASubcommandNobodyOpened_IsClosed()
    {
        Assert.False(await Access.IsAllowedAsync(Guild, TrackerCommandNames.Joueur, [Recruiter, Member], isAdministrator: false));
    }

    [Fact]
    public async Task AnOpenedRole_PassesThatSubcommandOnly()
    {
        Assert.True(await Access.GrantAsync(Guild, TrackerCommandNames.Historique, Recruiter, Admin));

        Assert.True(await Access.IsAllowedAsync(Guild, TrackerCommandNames.Historique, [Member, Recruiter], false));
        Assert.False(await Access.IsAllowedAsync(Guild, TrackerCommandNames.Membres, [Recruiter], false));
        Assert.False(await Access.IsAllowedAsync(Guild, TrackerCommandNames.Historique, [Member], false));
    }

    [Fact]
    public async Task AnOpeningOnAnotherServer_DoesNotCount()
    {
        await Access.GrantAsync(OtherGuild, TrackerCommandNames.Org, Recruiter, Admin);

        Assert.False(await Access.IsAllowedAsync(Guild, TrackerCommandNames.Org, [Recruiter], false));
    }

    [Fact]
    public async Task OpeningTwice_ChangesNothing_AndClosingCloses()
    {
        Assert.True(await Access.GrantAsync(Guild, TrackerCommandNames.Org, Recruiter, Admin));
        Assert.False(await Access.GrantAsync(Guild, TrackerCommandNames.Org, Recruiter, Admin));

        Assert.True(await Access.RevokeAsync(Guild, TrackerCommandNames.Org, Recruiter));
        Assert.False(await Access.RevokeAsync(Guild, TrackerCommandNames.Org, Recruiter));
        Assert.False(await Access.IsAllowedAsync(Guild, TrackerCommandNames.Org, [Recruiter], false));
    }

    [Fact]
    public async Task TheList_ShowsEverySubcommand_WithItsRoles()
    {
        await Access.GrantAsync(Guild, TrackerCommandNames.Joueur, Recruiter, Admin);
        await Access.GrantAsync(Guild, TrackerCommandNames.Joueur, Member, Admin);

        var list = await Access.ListAsync(Guild);

        Assert.Equal(TrackerCommandNames.All.OrderBy(c => c), list.Keys.OrderBy(c => c));
        Assert.Equal(new[] { Recruiter, Member }.OrderBy(r => r), list[TrackerCommandNames.Joueur].OrderBy(r => r));
        Assert.Empty(list[TrackerCommandNames.Mouvements]);
    }

    [Fact]
    public async Task AnUnknownSubcommand_CannotBeOpened()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Access.GrantAsync(Guild, "psyche", Recruiter, Admin));
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test BotLiberastra.Tests --filter "FullyQualifiedName~TrackerAccessServiceTests"`
Expected: compilation error.

- [ ] **Step 3: Add the entity, the names and the service**

Dans `Services/Models/BotModels.cs` :

```csharp
/// <summary>A Discord role allowed to run one /tracker subcommand on one server (spec § 6.4).</summary>
public class TrackerCommandAccessEntity
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public string Command { get; set; } = "";
    public ulong RoleId { get; set; }
    public ulong GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; }
}
```

Dans `BotDbContext` (`Services/DatabaseService.cs`) :

```csharp
    public DbSet<TrackerCommandAccessEntity> TrackerCommandAccess => Set<TrackerCommandAccessEntity>();
```

```csharp
        modelBuilder.Entity<TrackerCommandAccessEntity>(e =>
        {
            e.ToTable("TrackerCommandAccess");
            e.HasKey(a => a.Id);
            e.Property(a => a.Command).HasMaxLength(20);
            e.HasIndex(a => new { a.GuildId, a.Command, a.RoleId }).IsUnique();
        });
```

`Services/Tracker/TrackerCommandNames.cs` :

```csharp
namespace BotLiberastra.Services.Tracker;

/// <summary>The /tracker subcommands, as access is granted and as the tracker logs them.</summary>
public static class TrackerCommandNames
{
    public const string Joueur = "joueur";
    public const string Historique = "historique";
    public const string Recherche = "recherche";
    public const string Org = "org";
    public const string Membres = "membres";
    public const string Mouvements = "mouvements";

    public static readonly IReadOnlyList<string> All = [Joueur, Historique, Recherche, Org, Membres, Mouvements];
}
```

`Services/Tracker/TrackerAccessService.cs` (les entités sont dans `BotLiberastra.Services.Models`) :

```csharp
using BotLiberastra.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace BotLiberastra.Services.Tracker;

/// <summary>
/// Who may run a /tracker subcommand: a server administrator always; anyone else through
/// a role opened for that subcommand; nobody when no role is open (spec § 6.4).
/// </summary>
public sealed class TrackerAccessService(IDbContextFactory<BotDbContext> dbFactory)
{
    public async Task<bool> IsAllowedAsync(
        ulong guildId, string command, IReadOnlyCollection<ulong> roleIds, bool isAdministrator, CancellationToken ct = default)
    {
        if (isAdministrator) return true;
        if (roleIds.Count == 0) return false;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var opened = await db.TrackerCommandAccess.AsNoTracking()
            .Where(a => a.GuildId == guildId && a.Command == command)
            .Select(a => a.RoleId)
            .ToListAsync(ct);
        return opened.Any(roleIds.Contains);
    }

    public async Task<bool> GrantAsync(ulong guildId, string command, ulong roleId, ulong grantedBy, CancellationToken ct = default)
    {
        EnsureKnown(command);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.TrackerCommandAccess.AnyAsync(a => a.GuildId == guildId && a.Command == command && a.RoleId == roleId, ct))
            return false;
        db.TrackerCommandAccess.Add(new TrackerCommandAccessEntity
        {
            GuildId = guildId, Command = command, RoleId = roleId, GrantedBy = grantedBy, GrantedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevokeAsync(ulong guildId, string command, ulong roleId, CancellationToken ct = default)
    {
        EnsureKnown(command);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.TrackerCommandAccess
            .FirstOrDefaultAsync(a => a.GuildId == guildId && a.Command == command && a.RoleId == roleId, ct);
        if (row is null) return false;
        db.TrackerCommandAccess.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ulong>>> ListAsync(ulong guildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.TrackerCommandAccess.AsNoTracking()
            .Where(a => a.GuildId == guildId)
            .Select(a => new { a.Command, a.RoleId })
            .ToListAsync(ct);
        return TrackerCommandNames.All.ToDictionary(
            c => c,
            c => (IReadOnlyList<ulong>)rows.Where(r => r.Command == c).Select(r => r.RoleId).ToList());
    }

    private static void EnsureKnown(string command)
    {
        if (!TrackerCommandNames.All.Contains(command))
            throw new ArgumentException($"Sous-commande inconnue : {command}", nameof(command));
    }
}
```

- [ ] **Step 4: Generate the migration**

```bash
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 9.0.2
dotnet tool run dotnet-ef migrations add AddTrackerCommandAccess --project BotLiberastra.csproj
```

Relire la migration générée. Elle ne doit contenir qu'un `CreateTable("TrackerCommandAccess", …)` et un `CreateIndex` unique sur (`GuildId`, `Command`, `RoleId`), et aucune modification des autres tables.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test BotLiberastra.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add .config/dotnet-tools.json Services/Models/BotModels.cs Services/DatabaseService.cs Migrations Services/Tracker/TrackerCommandNames.cs Services/Tracker/TrackerAccessService.cs BotLiberastra.Tests/InMemoryBotDb.cs BotLiberastra.Tests/TrackerAccessServiceTests.cs
git commit -m "feat(tracker): access to /tracker subcommands by Discord role"
```

### Task B5: précondition et commandes `/tracker-acces`

**Files:**
- Create: `Commands/RequireTrackerAccessAttribute.cs`
- Create: `Commands/TrackerAccessCommands.cs`

**Interfaces:**
- Consumes: `TrackerAccessService`, `TrackerCommandNames` (B4).
- Produces: `[RequireTrackerAccess(string command)]`, utilisé par B7.

La règle d'accès est testée en B4. Ces deux classes ne font que la brancher sur Discord : la recette (partie C) les vérifie.

- [ ] **Step 1: Create the precondition**

```csharp
using BotLiberastra.Services.Tracker;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace BotLiberastra.Commands;

/// <summary>Runs a /tracker subcommand only for whom its access allows (spec § 6.4).</summary>
public sealed class RequireTrackerAccessAttribute(string command) : PreconditionAttribute
{
    public string Command { get; } = command;

    public override async Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        if (context.User is not IGuildUser member)
            return PreconditionResult.FromError("Commande utilisable seulement sur le serveur.");

        var access = services.GetRequiredService<TrackerAccessService>();
        var allowed = await access.IsAllowedAsync(
            member.GuildId, Command, member.RoleIds.ToList(), member.GuildPermissions.Administrator);
        return allowed
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(
                $"Tu n'as pas accès à /tracker {Command}. Un administrateur peut l'ouvrir avec /tracker-acces.");
    }
}
```

Un refus passe par la gestion d'erreur existante de `DiscordBotService` : avertissement dans les journaux, embed d'erreur éphémère.

- [ ] **Step 2: Create `/tracker-acces`**

```csharp
using System.Text;
using BotLiberastra.Services;
using BotLiberastra.Services.Tracker;
using Discord;
using Discord.Interactions;

namespace BotLiberastra.Commands;

/// <summary>Which roles may run each /tracker subcommand; server administrators only (spec § 6.4).</summary>
[Group("tracker-acces", "Rôles autorisés pour chaque commande /tracker")]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[RequireUserPermission(GuildPermission.Administrator)]
public class TrackerAccessCommands(TrackerAccessService access) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("ouvrir", "Autoriser un rôle à utiliser une commande /tracker")]
    public async Task OpenAsync(
        [Summary("commande"), Choice("joueur", "joueur"), Choice("historique", "historique"), Choice("recherche", "recherche"),
         Choice("org", "org"), Choice("membres", "membres"), Choice("mouvements", "mouvements")] string command,
        [Summary("role")] IRole role)
    {
        var added = await access.GrantAsync(Context.Guild.Id, command, role.Id, Context.User.Id);
        await RespondAsync(added
            ? $"/tracker {command} est ouvert au rôle {role.Mention}."
            : $"Le rôle {role.Mention} avait déjà accès à /tracker {command}.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("fermer", "Retirer à un rôle l'accès à une commande /tracker")]
    public async Task CloseAsync(
        [Summary("commande"), Choice("joueur", "joueur"), Choice("historique", "historique"), Choice("recherche", "recherche"),
         Choice("org", "org"), Choice("membres", "membres"), Choice("mouvements", "mouvements")] string command,
        [Summary("role")] IRole role)
    {
        var removed = await access.RevokeAsync(Context.Guild.Id, command, role.Id);
        await RespondAsync(removed
            ? $"/tracker {command} est fermé au rôle {role.Mention}."
            : $"Le rôle {role.Mention} n'avait pas accès à /tracker {command}.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("voir", "Rôles autorisés pour chaque commande /tracker")]
    public async Task ShowAsync()
    {
        var list = await access.ListAsync(Context.Guild.Id);
        var text = new StringBuilder();
        foreach (var (command, roles) in list)
        {
            text.Append($"**/tracker {command}** : ");
            text.AppendLine(roles.Count == 0
                ? "fermé (administrateurs seulement)"
                : string.Join(", ", roles.Select(r => $"<@&{r}>")));
        }
        await RespondAsync(text.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build BotLiberastra.csproj -c Release` puis `dotnet test BotLiberastra.Tests`
Expected: Build succeeded ; PASS.

- [ ] **Step 4: Commit**

```bash
git add Commands/RequireTrackerAccessAttribute.cs Commands/TrackerAccessCommands.cs
git commit -m "feat(tracker): /tracker-acces and the access precondition"
```

### Task B6: mise en forme des réponses

**Files:**
- Create: `Services/Tracker/TrackerEmbeds.cs`
- Test: `BotLiberastra.Tests/TrackerEmbedsTests.cs`

**Interfaces:**
- Consumes: DTO `Tracker*` (B3).
- Produces :
  - `TrackerEmbeds.Player(TrackerPlayer)`, `History(TrackerHistory)`, `Search(TrackerSearch)`, `Org(TrackerOrg)`, `Members(TrackerMembersPage)`, `Movements(TrackerMovements)`, qui renvoient chacune un `Embed` ;
  - `TrackerEmbeds.ErrorMessage(TrackerOutcome outcome, bool aboutPlayer) : string` ;
  - `TrackerEmbeds.InvisibleAffiliationsNote`.

- [ ] **Step 1: Write the failing tests**

```csharp
using BotLiberastra.Services.Tracker;
using Discord;
using Xunit;

namespace BotLiberastra.Tests;

/// <summary>The /tracker embeds: what they say, within Discord's limits (spec § 6.3).</summary>
public class TrackerEmbedsTests
{
    private static void AssertWithinDiscordLimits(Embed embed)
    {
        Assert.True(embed.Fields.Length <= 25, $"{embed.Fields.Length} fields");
        Assert.All(embed.Fields, f => Assert.True(f.Value.Length <= 1024, $"field {f.Name}: {f.Value.Length}"));
        Assert.True(embed.Length <= 6000, $"embed length {embed.Length}");
    }

    [Fact]
    public void APlayer_SaysTheTrackerOnlySeesVisibleAffiliations()
    {
        var player = new TrackerPlayer("ErunDhyr", "ErunDhyr", 2373025, new DateTime(2019, 9, 29), null, true,
            [new TrackerMembership("TOOLINEAR", "Too Linear", "Pilot", 2, new DateTime(2026, 2, 23), new DateTime(2026, 10, 1), true)],
            new DateTime(2026, 10, 1));

        var embed = TrackerEmbeds.Player(player);

        Assert.Contains("#2373025", string.Join(" ", embed.Fields.Select(f => f.Value)));
        Assert.Contains("TOOLINEAR", string.Join(" ", embed.Fields.Select(f => f.Value)));
        Assert.Contains(TrackerEmbeds.InvisibleAffiliationsNote, embed.Footer!.Value.Text);
        AssertWithinDiscordLimits(embed);
    }

    [Fact]
    public void AHistoryWithManyOrgs_StaysWithinDiscordLimits()
    {
        var orgs = Enumerable.Range(0, 60)
            .Select(i => new TrackerMembership($"ORG{i:00}", new string('N', 90), "A rank with a long name", 5,
                new DateTime(2025, 1, 1), new DateTime(2026, 1, 1), i % 2 == 0))
            .ToList();
        var history = new TrackerHistory("LongHistory", orgs,
            [new TrackerHandle("Older", new DateTime(2024, 1, 1), new DateTime(2025, 1, 1))],
            Enumerable.Range(0, 15).Select(i => new TrackerEvent(new DateTime(2026, 1, 1).AddDays(i), "member_joined", $"ORG{i:00}", null, null)).ToList());

        var embed = TrackerEmbeds.History(history);

        AssertWithinDiscordLimits(embed);
        Assert.Contains(embed.Fields, f => f.Value.Contains("tronquée"));
    }

    [Fact]
    public void AnOrg_ShowsVisibleAndMaskedCounts_AndTheTrend()
    {
        var org = new TrackerOrg("LIBERASTRA", "Liberastra", "PMC", "French", true, false, 40,
            new TrackerCounts(40, 34, 4, 2, new DateTime(2026, 10, 2)), new TrackerTrend(30, 40), new DateTime(2026, 10, 2));

        var text = string.Join(" ", TrackerEmbeds.Org(org).Fields.Select(f => $"{f.Name} {f.Value}"));

        Assert.Contains("34 visibles", text);
        Assert.Contains("6 masqués", text);
        Assert.Contains("+10", text);
    }

    [Fact]
    public void Movements_SayWhenTheListIsTruncated()
    {
        var movements = new TrackerMovements("BIG", 90,
            Enumerable.Range(0, 50).Select(i => new TrackerMovement($"joined{i}", new DateTime(2026, 9, 1))).ToList(),
            [], Truncated: true);

        var embed = TrackerEmbeds.Movements(movements);

        AssertWithinDiscordLimits(embed);
        Assert.Contains("50 premiers", embed.Footer!.Value.Text);
    }

    [Theory]
    [InlineData(TrackerOutcome.NotFound, true, "roster visible")]
    [InlineData(TrackerOutcome.NotFound, false, "Introuvable")]
    [InlineData(TrackerOutcome.RateLimited, false, "une minute")]
    [InlineData(TrackerOutcome.Unavailable, false, "réessaie plus tard")]
    [InlineData(TrackerOutcome.Refused, false, "Tracker indisponible")]
    [InlineData(TrackerOutcome.NotConfigured, false, "non configurée")]
    public void ErrorMessages_AreClear(TrackerOutcome outcome, bool aboutPlayer, string expected)
    {
        Assert.Contains(expected, TrackerEmbeds.ErrorMessage(outcome, aboutPlayer));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test BotLiberastra.Tests --filter "FullyQualifiedName~TrackerEmbedsTests"`
Expected: compilation error.

- [ ] **Step 3: Implement**

`Services/Tracker/TrackerEmbeds.cs` :

```csharp
using System.Globalization;
using System.Text;
using Discord;

namespace BotLiberastra.Services.Tracker;

/// <summary>The /tracker answers as Discord embeds, always within Discord's limits (spec § 6.3).</summary>
public static class TrackerEmbeds
{
    public const string InvisibleAffiliationsNote = "Le tracker ne voit que les orgs où le joueur est visible.";

    private const int MaxFieldValue = 1024;
    private const int MaxFields = 25;
    private const int MaxEmbed = 6000;
    private const int Reserve = 400;  // title, description, footer
    private const string TruncatedName = "…";
    private static readonly Color TrackerColor = new(0x2B9FD9);
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static Embed Player(TrackerPlayer p)
    {
        var e = Base($"Joueur {p.Handle}");
        if (!string.IsNullOrWhiteSpace(p.DisplayName) && p.DisplayName != p.Handle) e.WithDescription(p.DisplayName);
        e.AddField("Citoyen", p.CitizenId is { } id ? $"#{id}" : "inconnu", true);
        e.AddField("Inscription", Date(p.Enlisted), true);
        if (!string.IsNullOrWhiteSpace(p.Location)) e.AddField("Lieu", p.Location, true);
        AddLines(e, "Orgs actuelles", p.CurrentOrgs.Select(Membership), "aucune org visible");
        var profile = p.ProfileRead ? "" : " Profil RSI pas encore lu.";
        e.WithFooter($"{InvisibleAffiliationsNote}{profile} Vu le {Date(p.LastSeen)}.");
        return e.Build();
    }

    public static Embed History(TrackerHistory h)
    {
        var e = Base($"Historique de {h.Handle}");
        AddLines(e, "Orgs", h.Orgs.Select(Membership), "aucune org vue par le tracker");
        AddLines(e, "Pseudos", h.Handles.Select(x => $"{x.Handle} ({Date(x.FirstSeen)} → {Date(x.LastSeen)})"), "aucun ancien pseudo connu");
        AddLines(e, "Derniers événements", h.Events.Select(x => $"{Date(x.At)} · {x.Type} {x.OrgSid}"), "aucun");
        e.WithFooter(InvisibleAffiliationsNote);
        return e.Build();
    }

    public static Embed Search(TrackerSearch s)
    {
        var e = Base("Recherche dans le tracker");
        AddLines(e, "Orgs", s.Orgs.Select(o => $"**{o.Sid}** {o.Name} · {o.MembersCount} membres"), "aucune");
        AddLines(e, "Joueurs", s.Players.Select(p => p.DisplayName is null || p.DisplayName == p.Handle ? p.Handle : $"{p.Handle} ({p.DisplayName})"), "aucun");
        return e.Build();
    }

    public static Embed Org(TrackerOrg o)
    {
        var e = Base($"{o.Name} [{o.Sid}]");
        e.AddField("Type", o.Archetype ?? "?", true);
        e.AddField("Langue", o.Lang ?? "?", true);
        e.AddField("Recrute", o.Recruiting switch { true => "oui", false => "non", null => "?" }, true);
        if (o.Counts is { } c)
        {
            var masked = (c.Redacted ?? 0) + (c.Hidden ?? 0);
            var visible = c.Visible is { } v ? $"{v} visibles · {masked} masqués" : "répartition inconnue";
            e.AddField("Effectif RSI", $"{c.Total} ({visible})", false);
        }
        else
        {
            e.AddField("Effectif RSI", o.MembersCount.ToString(French), false);
        }
        if (o.Trend30d is { } t)
            e.AddField("Sur 30 jours", $"{t.From} → {t.To} ({t.To - t.From:+#;-#;0})", true);
        e.WithFooter($"Roster lu le {Date(o.MembersReadAt)}");
        return e.Build();
    }

    public static Embed Members(TrackerMembersPage m)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(m.Total / (double)m.PageSize));
        var e = Base($"Membres de {m.Sid}");
        AddLines(e, "Membres", m.Items.Select(x => $"{x.Handle} · {x.Rank ?? "?"} {Stars(x.Stars)} · depuis {Date(x.Since)}"), "aucun membre visible");
        e.WithFooter($"Page {m.Page} / {pages} · {m.Total} membres visibles");
        return e.Build();
    }

    public static Embed Movements(TrackerMovements m)
    {
        var e = Base($"Mouvements de {m.Sid} sur {m.Days} jours");
        AddLines(e, "Arrivées", m.Joined.Select(x => $"{Date(x.At)} · {x.Handle}"), "aucune");
        AddLines(e, "Départs", m.Left.Select(x => $"{Date(x.At)} · {x.Handle}"), "aucun");
        if (m.Truncated) e.WithFooter("Seuls les 50 premiers de chaque liste sont affichés.");
        return e.Build();
    }

    public static string ErrorMessage(TrackerOutcome outcome, bool aboutPlayer) => outcome switch
    {
        TrackerOutcome.NotFound => aboutPlayer
            ? "Introuvable dans le tracker. Il ne connaît que les joueurs vus dans un roster visible."
            : "Introuvable dans le tracker.",
        TrackerOutcome.RateLimited => "Trop de requêtes, réessaie dans une minute.",
        TrackerOutcome.Invalid => "Requête refusée par le tracker : vérifie ce que tu as saisi.",
        TrackerOutcome.Refused => "Tracker indisponible.",
        TrackerOutcome.NotConfigured => "Commande non configurée.",
        _ => "Le tracker ne répond pas, réessaie plus tard.",
    };

    private static EmbedBuilder Base(string title) => new EmbedBuilder()
        .WithTitle(title.Length <= 256 ? title : title[..255] + "…")
        .WithColor(TrackerColor);

    private static string Membership(TrackerMembership m)
    {
        var period = m.Active ? $"depuis {Date(m.Since)}" : $"{Date(m.Since)} → {Date(m.LastSeen)}";
        return $"**{m.Sid}** {m.Name} · {m.Rank ?? "?"} {Stars(m.Stars)} · {period}";
    }

    private static string Stars(int? stars) => stars is { } n and >= 0 and <= 5
        ? new string('★', n) + new string('☆', 5 - n)
        : "";

    private static string Date(DateTime? date) => date?.ToString("d MMM yyyy", French) ?? "inconnue";

    /// <summary>Lines in as many fields as needed, then a "truncated" field once Discord's limits are near.</summary>
    private static void AddLines(EmbedBuilder e, string title, IEnumerable<string> lines, string empty)
    {
        var chunk = new StringBuilder();
        var part = 0;
        var any = false;
        foreach (var raw in lines)
        {
            any = true;
            var line = raw.Length < MaxFieldValue ? raw : raw[..(MaxFieldValue - 2)] + "…";
            if (chunk.Length + line.Length + 1 > MaxFieldValue)
            {
                if (!TryAdd(e, part++ == 0 ? title : $"{title} (suite)", chunk.ToString())) return;
                chunk.Clear();
            }
            chunk.Append(line).Append('\n');
        }
        if (!any) chunk.Append(empty);
        TryAdd(e, part == 0 ? title : $"{title} (suite)", chunk.ToString());
    }

    private static bool TryAdd(EmbedBuilder e, string name, string value)
    {
        if (e.Fields.Any(f => f.Name == TruncatedName)) return false;
        var text = value.TrimEnd();
        var fits = e.Fields.Count < MaxFields - 1 && e.Length + name.Length + text.Length <= MaxEmbed - Reserve;
        e.AddField(fits ? name : TruncatedName, fits ? text : "Liste tronquée : limites de Discord.");
        return fits;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test BotLiberastra.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/Tracker/TrackerEmbeds.cs BotLiberastra.Tests/TrackerEmbedsTests.cs
git commit -m "feat(tracker): embeds of the /tracker answers within Discord's limits"
```

### Task B7: commandes `/tracker` et autocomplétion

**Files:**
- Create: `Services/Tracker/TrackerAutocompleteLogic.cs`
- Create: `Commands/TrackerAutocomplete.cs`
- Create: `Commands/TrackerCommands.cs`
- Modify: `Program.cs` (réglages, client HTTP avec épinglage, service d'accès)
- Test: `BotLiberastra.Tests/TrackerAutocompleteLogicTests.cs`

**Interfaces:**
- Consumes: B2 à B6.
- Produces: `TrackerAutocompleteLogic.SuggestAsync(TrackerAccessService access, TrackerApiClient tracker, ulong guildId, ulong userId, IReadOnlyCollection<ulong> roleIds, bool isAdministrator, string command, string input, bool orgs, CancellationToken ct = default) : Task<IReadOnlyList<(string Label, string Value)>>`.

- [ ] **Step 1: Write the failing tests**

```csharp
using BotLiberastra.Configuration;
using BotLiberastra.Services.Tracker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BotLiberastra.Tests;

/// <summary>Autocompletion reveals nothing to whom may not run the subcommand (spec § 6.3).</summary>
public sealed class TrackerAutocompleteLogicTests : IDisposable
{
    private const ulong Guild = 111111111111111111;
    private const ulong User = 123456789012345678;
    private const ulong Recruiter = 333333333333333333;

    private readonly InMemoryBotDb _db = new();
    private readonly FakeHandler _handler = new(_ => FakeHandler.Json("""
        {"orgs":[{"sid":"LIBERASTRA","name":"Liberastra","membersCount":40}],
         "players":[{"handle":"ErunDhyr","displayName":null}]}
        """));

    private TrackerApiClient Tracker => new(new HttpClient(_handler), Options.Create(new TrackerApiConfig
    {
        BaseUrl = "https://203.0.113.10/bot-api/", ApiKey = "k", CertSha256 = new string('A', 64),
    }), NullLogger<TrackerApiClient>.Instance);

    [Fact]
    public async Task AutocompleteWithoutAccess_SendsNothing()
    {
        var suggestions = await TrackerAutocompleteLogic.SuggestAsync(
            new TrackerAccessService(_db), Tracker, Guild, User, [Recruiter], false, TrackerCommandNames.Org, "liber", orgs: true);

        Assert.Empty(suggestions);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AutocompleteWithAccess_SuggestsOrgsBySid_AsAnAutocompleteRequest()
    {
        var access = new TrackerAccessService(_db);
        await access.GrantAsync(Guild, TrackerCommandNames.Org, Recruiter, User);

        var suggestions = await TrackerAutocompleteLogic.SuggestAsync(
            access, Tracker, Guild, User, [Recruiter], false, TrackerCommandNames.Org, "liber", orgs: true);

        Assert.Equal(("LIBERASTRA — Liberastra", "LIBERASTRA"), Assert.Single(suggestions));
        Assert.Equal("autocomplete", _handler.Requests.Single().Headers.GetValues("X-Bot-Command").Single());
    }

    [Fact]
    public async Task AShortInput_SendsNothing()
    {
        var suggestions = await TrackerAutocompleteLogic.SuggestAsync(
            new TrackerAccessService(_db), Tracker, Guild, User, [], true, TrackerCommandNames.Joueur, "e", orgs: false);

        Assert.Empty(suggestions);
        Assert.Empty(_handler.Requests);
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test BotLiberastra.Tests --filter "FullyQualifiedName~TrackerAutocompleteLogicTests"`
Expected: compilation error.

- [ ] **Step 3: Implement the autocompletion logic and handlers**

`Services/Tracker/TrackerAutocompleteLogic.cs` :

```csharp
namespace BotLiberastra.Services.Tracker;

/// <summary>Suggestions for a handle or SID option, for whom may run the subcommand only.</summary>
public static class TrackerAutocompleteLogic
{
    public static async Task<IReadOnlyList<(string Label, string Value)>> SuggestAsync(
        TrackerAccessService access, TrackerApiClient tracker, ulong guildId, ulong userId,
        IReadOnlyCollection<ulong> roleIds, bool isAdministrator, string command, string input, bool orgs,
        CancellationToken ct = default)
    {
        var text = input.Trim();
        if (text.Length < 2) return [];
        if (!await access.IsAllowedAsync(guildId, command, roleIds, isAdministrator, ct)) return [];

        var result = await tracker.SearchAsync(text.Length <= 50 ? text : text[..50], userId, "autocomplete", ct);
        if (result.Outcome != TrackerOutcome.Ok) return [];

        return orgs
            ? result.Value!.Orgs.Select(o => (Label(o.Sid + " — " + o.Name), o.Sid)).ToList()
            : result.Value!.Players.Select(p => (Label(p.DisplayName is null ? p.Handle : $"{p.Handle} ({p.DisplayName})"), p.Handle)).ToList();
    }

    // Discord caps a suggestion's label at 100 characters.
    private static string Label(string text) => text.Length <= 100 ? text : text[..99] + "…";
}
```

`Commands/TrackerAutocomplete.cs` :

```csharp
using BotLiberastra.Services.Tracker;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace BotLiberastra.Commands;

/// <summary>Handles of players for the /tracker joueur and historique options.</summary>
public class TrackerPlayerAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocomplete, IParameterInfo parameter, IServiceProvider services)
        => TrackerAutocomplete.SuggestAsync(context, autocomplete, parameter, services, orgs: false);
}

/// <summary>SIDs of organizations for the /tracker org, membres and mouvements options.</summary>
public class TrackerOrgAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocomplete, IParameterInfo parameter, IServiceProvider services)
        => TrackerAutocomplete.SuggestAsync(context, autocomplete, parameter, services, orgs: true);
}

internal static class TrackerAutocomplete
{
    public static async Task<AutocompletionResult> SuggestAsync(
        IInteractionContext context, IAutocompleteInteraction autocomplete, IParameterInfo parameter,
        IServiceProvider services, bool orgs)
    {
        if (context.User is not IGuildUser member) return AutocompletionResult.FromSuccess();
        var suggestions = await TrackerAutocompleteLogic.SuggestAsync(
            services.GetRequiredService<TrackerAccessService>(),
            services.GetRequiredService<TrackerApiClient>(),
            member.GuildId, member.Id, member.RoleIds.ToList(), member.GuildPermissions.Administrator,
            parameter.Command.Name,
            (autocomplete.Data.Current.Value as string) ?? "",
            orgs);
        return AutocompletionResult.FromSuccess(suggestions.Select(s => new AutocompleteResult(s.Label, s.Value)));
    }
}
```

- [ ] **Step 4: Create the `/tracker` commands**

`Commands/TrackerCommands.cs` :

```csharp
using BotLiberastra.Services;
using BotLiberastra.Services.Tracker;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;

namespace BotLiberastra.Commands;

/// <summary>
/// Searches in the SC organizations tracker (spec 2026-10-03 § 6.3). Answers are ephemeral;
/// nothing read here is passed to Gemini.
/// </summary>
[Group("tracker", "Recherches dans le tracker d'organisations")]
public class TrackerCommands(TrackerApiClient tracker, ILogger<TrackerCommands> logger) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("joueur", "Fiche d'un joueur : orgs actuelles, inscription")]
    [RequireTrackerAccess(TrackerCommandNames.Joueur)]
    public async Task PlayerAsync([Summary("pseudo"), Autocomplete(typeof(TrackerPlayerAutocomplete))] string pseudo)
    {
        await DeferAsync(ephemeral: true);
        await AnswerAsync(TrackerCommandNames.Joueur, pseudo, await tracker.PlayerAsync(pseudo, Context.User.Id), TrackerEmbeds.Player, aboutPlayer: true);
    }

    [SlashCommand("historique", "Historique d'un joueur : orgs passées, anciens pseudos, événements")]
    [RequireTrackerAccess(TrackerCommandNames.Historique)]
    public async Task HistoryAsync([Summary("pseudo"), Autocomplete(typeof(TrackerPlayerAutocomplete))] string pseudo)
    {
        await DeferAsync(ephemeral: true);
        await AnswerAsync(TrackerCommandNames.Historique, pseudo, await tracker.HistoryAsync(pseudo, Context.User.Id), TrackerEmbeds.History, aboutPlayer: true);
    }

    [SlashCommand("recherche", "Chercher des orgs et des joueurs par nom")]
    [RequireTrackerAccess(TrackerCommandNames.Recherche)]
    public async Task SearchAsync([Summary("texte")] string texte)
    {
        if (texte.Trim().Length < 2)
        {
            await RespondAsync(embed: EmbedFormatter.BuildErrorEmbed("Au moins 2 caractères."), ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var text = texte.Trim().Length <= 50 ? texte.Trim() : texte.Trim()[..50];
        await AnswerAsync(TrackerCommandNames.Recherche, text, await tracker.SearchAsync(text, Context.User.Id, TrackerCommandNames.Recherche), TrackerEmbeds.Search);
    }

    [SlashCommand("org", "Fiche d'une org : effectif RSI, visibles et masqués, tendance")]
    [RequireTrackerAccess(TrackerCommandNames.Org)]
    public async Task OrgAsync([Summary("sid"), Autocomplete(typeof(TrackerOrgAutocomplete))] string sid)
    {
        await DeferAsync(ephemeral: true);
        await AnswerAsync(TrackerCommandNames.Org, sid, await tracker.OrgAsync(sid, Context.User.Id), TrackerEmbeds.Org);
    }

    [SlashCommand("membres", "Membres actuels d'une org, par pages de 25")]
    [RequireTrackerAccess(TrackerCommandNames.Membres)]
    public async Task MembersAsync(
        [Summary("sid"), Autocomplete(typeof(TrackerOrgAutocomplete))] string sid,
        [Summary("page"), MinValue(1)] int page = 1)
    {
        await DeferAsync(ephemeral: true);
        await AnswerAsync(TrackerCommandNames.Membres, sid, await tracker.MembersAsync(sid, page, Context.User.Id), TrackerEmbeds.Members);
    }

    [SlashCommand("mouvements", "Arrivées et départs récents d'une org")]
    [RequireTrackerAccess(TrackerCommandNames.Mouvements)]
    public async Task MovementsAsync(
        [Summary("sid"), Autocomplete(typeof(TrackerOrgAutocomplete))] string sid,
        [Summary("jours"), MinValue(1), MaxValue(90)] int jours = 7)
    {
        await DeferAsync(ephemeral: true);
        await AnswerAsync(TrackerCommandNames.Mouvements, sid, await tracker.MovementsAsync(sid, jours, Context.User.Id), TrackerEmbeds.Movements);
    }

    private async Task AnswerAsync<T>(string command, string target, TrackerResult<T> result, Func<T, Embed> render, bool aboutPlayer = false)
    {
        logger.LogInformation("/tracker {Command} {Target} by {User}: {Outcome}", command, target, Context.User.Id, result.Outcome);
        var embed = result.Outcome == TrackerOutcome.Ok
            ? render(result.Value!)
            : EmbedFormatter.BuildErrorEmbed(TrackerEmbeds.ErrorMessage(result.Outcome, aboutPlayer));
        await FollowupAsync(embed: embed, ephemeral: true);
    }
}
```

- [ ] **Step 5: Register in `Program.cs`**

Après `builder.Services.Configure<GeminiConfig>(...)` :

```csharp
builder.Services.Configure<TrackerApiConfig>(builder.Configuration.GetSection("TrackerApi"));
```

Après l'enregistrement du client Galactapedia :

```csharp
// SC tracker /bot-api/ (spec 2026-10-03): its self-signed certificate is trusted by fingerprint only.
builder.Services.AddHttpClient<BotLiberastra.Services.Tracker.TrackerApiClient>()
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var expected = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TrackerApiConfig>>().Value.CertSha256;
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                BotLiberastra.Services.Tracker.CertificatePinning.Matches(certificate, expected),
        };
    });
builder.Services.AddSingleton<BotLiberastra.Services.Tracker.TrackerAccessService>();
```

Juste après `var app = builder.Build();` (le bot démarre même sans réglages ; spec § 6.1 et § 7) :

```csharp
if (!app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TrackerApiConfig>>().Value.IsComplete)
    app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>()
        .LogWarning("TrackerApi settings incomplete: /tracker answers \"Commande non configurée.\"");
```

`DiscordBotService.OnReadyAsync` découvre les modules (`AddModulesAsync`) : `TrackerCommands` et `TrackerAccessCommands` sont donc enregistrés sans autre changement.

- [ ] **Step 6: Run the tests and the build**

Run: `dotnet test BotLiberastra.Tests` puis `dotnet build BotLiberastra.csproj -c Release`
Expected: PASS ; Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add Services/Tracker/TrackerAutocompleteLogic.cs Commands/TrackerAutocomplete.cs Commands/TrackerCommands.cs Program.cs BotLiberastra.Tests/TrackerAutocompleteLogicTests.cs
git commit -m "feat(tracker): /tracker joueur, historique, recherche, org, membres, mouvements"
```

### Task B8: déploiement par envoi direct sur `panda`

**Files:**
- Modify: `deploy.sh`
- Modify: `README.md`

`panda` ne peut plus lire le dépôt privé sur GitHub. Le poste de développement lui pousse la version à déployer sur sa branche locale `release`, et `deploy.sh` déploie cette branche.

- [ ] **Step 1: Update `deploy.sh`**

Remplacer l'en-tête et la récupération du code (le reste est inchangé : compilation dans `publish_new/`, bascule, `publish_old/`) :

```bash
#!/usr/bin/env bash
# Déploie le bot Liberastra depuis la branche locale `release`, poussée depuis le poste de
# développement : git push panda main:release (panda ne peut plus lire le dépôt privé).
#   1. Bascule le dépôt sur release (exactement ce qui a été poussé)
#   2. Compile dans publish_new/ (le bot actuel continue de tourner)
#   3. Si la compilation réussit -> stop service, bascule, restart (~2-3 s de coupure)
#      Si elle échoue -> le bot garde l'ancienne version, aucune coupure
set -euo pipefail

# git reset remplace ce fichier pendant qu'il s'exécute : on tourne depuis une copie.
if [ "${LIBERASTRA_DEPLOY_COPY:-}" != 1 ]; then
  cp "$0" /tmp/liberastra-deploy.sh
  LIBERASTRA_DEPLOY_COPY=1 exec bash /tmp/liberastra-deploy.sh "$@"
fi

export DOTNET_ROOT=/root/.dotnet
export PATH="$PATH:/root/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

APP=/root/discord/Liberastra-Bot-Discord
cd "$APP"

echo "→ Version à déployer : branche release ($(git rev-parse --short release))…"
git reset --hard release
```

- [ ] **Step 2: Document in `README.md`**

```markdown
## Déployer sur panda

`panda` reçoit les versions par envoi direct (il ne lit pas le dépôt privé) :

    git remote add panda ssh://panda/root/discord/Liberastra-Bot-Discord   # une fois
    git push panda main:release
    ssh panda /root/discord/Liberastra-Bot-Discord/deploy.sh

Réglages de `/tracker` dans `.env` sur panda : `TrackerApi__BaseUrl`, `TrackerApi__ApiKey`,
`TrackerApi__CertSha256` (voir `deploy/README.md` du tracker, « Bot Discord Liberastra »).
Les accès se règlent dans Discord avec `/tracker-acces ouvrir|fermer|voir`.
```

- [ ] **Step 3: Check the script**

Run: `bash -n deploy.sh`
Expected: aucune sortie (syntaxe valide).

- [ ] **Step 4: Commit**

```bash
git add deploy.sh README.md
git commit -m "chore(deploy): panda deploys the release branch pushed to it"
```

---

# Partie C — déploiement et recette

Chaque étape ci-dessous touche un serveur ou un dépôt distant : **annoncer l'action, attendre le « oui » de l'utilisateur, puis l'exécuter**.

### Task C1: tracker en production

- [ ] **Step 1 :** fusionner `bot-api` dans `main` (avance rapide), `git push github main`, puis attendre la CI verte.
- [ ] **Step 2 :** `git push vps main:release`, puis sur le serveur, en détaché : `~/sc-tracker/current/deploy/deploy.sh <sha>`, **sans** `--collector` (pas de migration de `tracker.db`).
- [ ] **Step 3 :** installer le bloc nginx :
  ```bash
  sudo cp ~/sc-tracker/current/deploy/nginx/sc-tracker.conf /etc/nginx/sites-available/sc-tracker
  sudo nginx -t && sudo systemctl reload nginx
  ```
- [ ] **Step 4 :** contrôles :
  - depuis le poste local, `https://141.95.51.193/bot-api/search?q=ab` répond **403** (IP non autorisée) ;
  - sur le serveur, `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5000/api/bot/search?q=ab` répond **401** (pas de clé).

### Task C2: compte, clé et empreinte

- [ ] **Step 1 :** créer le compte `liberastra-bot`, non administrateur, avec un mot de passe aléatoire jamais affiché. Utiliser la création de compte admin existante, avec la clé d'administration de `/etc/sc-tracker/api.env`, depuis le serveur.
- [ ] **Step 2 :** créer sa clé avec `POST http://127.0.0.1:5000/api/admin/users/<id>/api-keys`, corps `{"name":"liberastra-bot","expiresAt":"<aujourd'hui + 364 jours>","scope":"bot:read"}`. Récupérer `rawKey` dans une variable, sans l'afficher.
- [ ] **Step 3 :** relever l'empreinte SHA-256 du certificat nginx (`openssl x509 -noout -fingerprint -sha256`), ou reprendre `Discord__Ingest__CertificateSha256` de `api.env`.
- [ ] **Step 4 :** ajouter dans le `.env` du bot sur `panda`, sans afficher la clé : `TrackerApi__BaseUrl=https://141.95.51.193/bot-api/`, `TrackerApi__ApiKey=<rawKey>`, `TrackerApi__CertSha256=<empreinte>`.

### Task C3: bot en production

- [ ] **Step 1 :** `git push origin server-sync`, puis fusionner `server-sync` dans `main` sur GitHub (avance rapide depuis 13ec14a).
- [ ] **Step 2 :** fusionner `tracker-commands` dans `main`, `git push origin main`.
- [ ] **Step 3 :** `git remote add panda ssh://panda/root/discord/Liberastra-Bot-Discord`, puis `git push panda main:release`.
- [ ] **Step 4 :** premier déploiement avec le nouveau script, extrait de `release` :
  ```bash
  ssh panda 'cd /root/discord/Liberastra-Bot-Discord && git show release:deploy.sh > /tmp/liberastra-deploy.sh && LIBERASTRA_DEPLOY_COPY=1 bash /tmp/liberastra-deploy.sh'
  ```
- [ ] **Step 5 :** vérifier `journalctl -u liberastra`. On doit y trouver le démarrage, l'enregistrement des commandes sur le serveur Discord et l'application de la migration `AddTrackerCommandAccess`, sans erreur.

### Task C4: recette dans Discord

- [ ] Sans aucun accès ouvert : un administrateur voit les réponses ; un membre reçoit le message de refus ; l'autocomplétion ne propose rien au membre.
- [ ] `/tracker-acces ouvrir joueur <rôle>` puis `voir` : le rôle est listé ; un membre de ce rôle utilise `/tracker joueur` mais pas `/tracker historique`.
- [ ] Les six sous-commandes, avec un joueur connu (`ErunDhyr` → introuvable avec la mention du roster visible), une org connue (`LIBERASTRA`), un SID en minuscules, `membres` page 2 et `mouvements` sur 30 jours.
- [ ] Dans l'administration du tracker, journal d'activité : les lignes `bot:<commande>`, `discord:<id>` apparaissent, sans lignes `autocomplete`.
- [ ] `/tracker-acces fermer` retire l'accès immédiatement.
- [ ] Ouvrir ensuite les sous-commandes aux rôles choisis par l'utilisateur.
