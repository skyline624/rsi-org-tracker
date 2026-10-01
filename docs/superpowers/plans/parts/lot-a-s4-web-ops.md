### Task A13: Ingest config endpoint and the settings key panel

**Files:**
- Create: `src/Collector.Api/Options/DiscordOptions.cs`
- Modify: `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs` (method `AddApiServices`, after the `AudioSettings` binding)
- Modify: `src/Collector.Api/Dtos/Discord/DiscordDtos.cs` (new class after `DiscordUserDto`)
- Modify: `src/Collector.Api/Controllers/DiscordController.cs` (constructor, new action `GetIngestConfig`)
- Modify: `src/Collector.Api.Tests/ApiFactory.cs` (constants after `LegacyJwtSecret`, env vars in the constructor after `COLLECTOR_API_Api__AdminApiKey`)
- Modify: `src/Collector.Web/src/lib/api/types.ts` (new sections after `CycleStatusDto`)
- Modify: `src/Collector.Web/src/lib/api/endpoints.ts` (type imports, new sections after `me`)
- Modify: `src/Collector.Web/src/lib/validation.ts` (after `discordTokenSchema`)
- Create: `src/Collector.Web/src/app/(user)/settings/discord-key-actions.ts`
- Create: `src/Collector.Web/src/app/(user)/settings/DiscordIngestKeysPanel.tsx`
- Modify: `src/Collector.Web/src/app/(user)/settings/page.tsx` (imports, data loading, the "API KEYS" v2 panel)
- Test: `src/Collector.Api.Tests/Discord/DiscordIngestConfigTests.cs`
- Test: `src/Collector.Web/src/lib/validation.test.ts`
- Test: `src/Collector.Web/src/lib/api/endpoints.test.ts`
- Test: `src/Collector.Web/src/app/(user)/settings/discord-key-actions.test.ts`
- Test: `src/Collector.Web/src/app/action-arguments.test.ts`

**Interfaces:**
- Consumes:
  - CONTRACTS § 4, scoped keys: `CreateApiKeyRequest(string Name, DateTime? ExpiresAt, string? Scope = null)`. `POST /api/api-keys` with `scope: "discord:ingest"` requires `ExpiresAt` in the future and at most 365 days away; `ApiKeyDto.Scope` is returned by `GET /api/api-keys`.
  - Existing: `DELETE /api/api-keys/{id}` (owner only, 204 or 404), `ApiFactory.SignedInClientAsync`, `ApiFactory.CreateAccountAsync`, `ApiFactory.LoginAsync`, `ApiCollection.Name`, `sessionCtx(session)`, `apiGet` / `apiPost` / `apiDelete`, `INVALID_ARGUMENTS`, `idSchema`, `valid`.
- Produces:
  - `Collector.Api.Options.DiscordOptions`, exactly the CONTRACTS § 4 shape (`Section = "Discord"`, `Ingest { PublicUrl, CertificateSha256 }`, `Retention { SyncLogDays = 365, DepartedAccountDays = 730 }`). It is bound once, in `AddApiServices`, with `services.AddOptions<DiscordOptions>().Bind(configuration.GetSection(DiscordOptions.Section))`; lot C's `DiscordRetentionService` reads `IOptions<DiscordOptions>().Value.Retention` and must not bind it again.
  - `Collector.Api.Dtos.Discord.DiscordIngestConfigDto { string? PublicUrl; string? CertificateSha256 }`, a class in `Dtos/Discord/DiscordDtos.cs`.
  - `GET api/discord/ingest-config` (`DiscordController.GetIngestConfig`, inherits `[Authorize]`): 200 `{ "publicUrl": …, "certificateSha256": … }`. Values are trimmed; a blank or missing setting is `null`. The fingerprint is returned as configured (the plugin normalises it).
  - `ApiFactory.DiscordIngestPublicUrl` and `ApiFactory.DiscordIngestCertificateSha256`, also exported to the shared test host through `COLLECTOR_API_Discord__Ingest__PublicUrl` / `__CertificateSha256`. A test that needs them unset uses `PostConfigure<DiscordOptions>` on a `WithWebHostBuilder` host.
  - Web: `ApiKeyDto`, `CreatedApiKeyDto`, `DiscordIngestConfigDto` (types.ts); `listApiKeys(ctx)`, `getDiscordIngestConfig(ctx)` (endpoints.ts); `apiKeyNameSchema`, `ingestKeyExpiryDaysSchema` (validation.ts); `createDiscordIngestKeyAction(name: unknown, expiresInDays: unknown)` returning `CreateDiscordIngestKeyResult { ok; data?: CreatedApiKeyDto; error? }` and `revokeApiKeyAction(id: unknown)` returning `RevokeApiKeyResult { ok; error? }`; client component `DiscordIngestKeysPanel({ config: DiscordIngestConfigDto | null; keys: ApiKeyDto[] | null; defaultName: string })`, which renders its own `HudPanel` titled "CLÉ D'ENVOI DISCORD". The list shows every key of the user (full-access ones created through the API and revoked ones included, with a status badge); only `discord:ingest` keys can be created from the site.

- [ ] **Step 1: Write the failing API test**

Add the test settings to `src/Collector.Api.Tests/ApiFactory.cs`. Replace:

```csharp
    /// <summary>Legacy HS256 secret: still configured, must no longer be accepted.</summary>
    public const string LegacyJwtSecret = "test-jwt-secret-0123456789abcdef0123456789";
```

with:

```csharp
    /// <summary>Legacy HS256 secret: still configured, must no longer be accepted.</summary>
    public const string LegacyJwtSecret = "test-jwt-secret-0123456789abcdef0123456789";

    /// <summary>Plugin settings published by <c>GET api/discord/ingest-config</c> (api.env in production).</summary>
    public const string DiscordIngestPublicUrl = "https://203.0.113.10";

    /// <summary>A fingerprint in the openssl format (32 colon-separated bytes).</summary>
    public const string DiscordIngestCertificateSha256 =
        "0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0:0F:1E:2D:3C:4B:5A:69:78:87:96:A5:B4:C3:D2:E1:F0";
```

Then, in the constructor, replace:

```csharp
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__AdminApiKey", AdminApiKey);
```

with:

```csharp
        Environment.SetEnvironmentVariable("COLLECTOR_API_Api__AdminApiKey", AdminApiKey);
        // Same variables as /etc/sc-tracker/api.env (deploy/README.md, "Rosters Discord").
        Environment.SetEnvironmentVariable("COLLECTOR_API_Discord__Ingest__PublicUrl", DiscordIngestPublicUrl);
        Environment.SetEnvironmentVariable("COLLECTOR_API_Discord__Ingest__CertificateSha256", DiscordIngestCertificateSha256);
```

Create `src/Collector.Api.Tests/Discord/DiscordIngestConfigTests.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Options;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// The settings panel shows what to enter in the Vencord plugin: the public URL and the
/// certificate fingerprint set in api.env (COLLECTOR_API_Discord__Ingest__*), or nothing
/// while the administrator has not set them, so the panel can send users to them.
/// </summary>
[Collection(ApiCollection.Name)]
public class DiscordIngestConfigTests(ApiFactory factory)
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task IngestConfig_ReturnsWhatApiEnvSets()
    {
        var client = await factory.SignedInClientAsync("ingest-config-reader");

        var config = await client.GetFromJsonAsync<JsonElement>("/api/discord/ingest-config");

        config.GetProperty("publicUrl").GetString().Should().Be(ApiFactory.DiscordIngestPublicUrl);
        config.GetProperty("certificateSha256").GetString().Should().Be(ApiFactory.DiscordIngestCertificateSha256);
    }

    [Fact]
    public async Task IngestConfig_IsNull_WhileTheAdministratorHasNotSetIt()
    {
        await factory.CreateAccountAsync("ingest-config-unset", Password);
        var login = await factory.LoginAsync("ingest-config-unset", Password);
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<DiscordOptions>(o =>
            {
                o.Ingest.PublicUrl = null;
                o.Ingest.CertificateSha256 = "   ";
            })));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var config = await client.GetFromJsonAsync<JsonElement>("/api/discord/ingest-config");

        config.GetProperty("publicUrl").ValueKind.Should().Be(JsonValueKind.Null);
        config.GetProperty("certificateSha256").ValueKind.Should().Be(JsonValueKind.Null, "a blank setting is not a fingerprint");
    }

    [Fact]
    public async Task IngestConfig_TrimsWhatTheAdministratorPasted()
    {
        await factory.CreateAccountAsync("ingest-config-padded", Password);
        var login = await factory.LoginAsync("ingest-config-padded", Password);
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<DiscordOptions>(o =>
            {
                o.Ingest.PublicUrl = "  https://198.51.100.7\n";
                o.Ingest.CertificateSha256 = " AB:CD:EF ";
            })));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var config = await client.GetFromJsonAsync<JsonElement>("/api/discord/ingest-config");

        config.GetProperty("publicUrl").GetString().Should().Be("https://198.51.100.7");
        config.GetProperty("certificateSha256").GetString().Should().Be("AB:CD:EF", "the plugin gets exactly what to paste");
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordIngestConfigTests"`

Expected: the build fails with `error CS0246: The type or namespace name 'DiscordOptions' could not be found` in `DiscordIngestConfigTests.cs`.

- [ ] **Step 3: Write the minimal implementation (API)**

Create `src/Collector.Api/Options/DiscordOptions.cs`:

```csharp
namespace Collector.Api.Options;

/// <summary>
/// Discord roster settings, bound to <c>Discord</c> (env <c>COLLECTOR_API_Discord__*</c>).
/// The same section holds the bot token (<c>Discord:BotToken</c>), which
/// <c>DiscordTokenStore</c> reads on its own.
/// </summary>
public sealed class DiscordOptions
{
    public const string Section = "Discord";

    /// <summary>What users enter in the Vencord plugin, shown in the settings panel.</summary>
    public IngestOptions Ingest { get; set; } = new();

    /// <summary>How long the upload journal and departed, unlinked accounts are kept.</summary>
    public RetentionOptions Retention { get; set; } = new();

    public sealed class IngestOptions
    {
        /// <summary>Public base URL of the tracker, scheme and host only (<c>https://&lt;IP&gt;</c>).</summary>
        public string? PublicUrl { get; set; }

        /// <summary>SHA-256 fingerprint of the nginx certificate, as openssl prints it (the plugin normalises it).</summary>
        public string? CertificateSha256 { get; set; }
    }

    public sealed class RetentionOptions
    {
        /// <summary><c>discord_syncs</c> rows older than this many days are deleted.</summary>
        public int SyncLogDays { get; set; } = 365;

        /// <summary>An unlinked account gone from every server for this many days is purged.</summary>
        public int DepartedAccountDays { get; set; } = 730;
    }
}
```

In `src/Collector.Api/Extensions/ServiceCollectionExtensions.cs`, method `AddApiServices`, replace:

```csharp
        services.AddOptions<Collector.Api.Options.AudioSettings>().Bind(configuration.GetSection(Collector.Api.Options.AudioSettings.Section));
```

with:

```csharp
        services.AddOptions<Collector.Api.Options.AudioSettings>().Bind(configuration.GetSection(Collector.Api.Options.AudioSettings.Section));

        // Discord rosters: plugin settings shown to users (Discord:Ingest) and retention (Discord:Retention).
        services.AddOptions<Collector.Api.Options.DiscordOptions>().Bind(configuration.GetSection(Collector.Api.Options.DiscordOptions.Section));
```

In `src/Collector.Api/Dtos/Discord/DiscordDtos.cs`, replace:

```csharp
public class DiscordUserDto
{
    public string Id { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string> Badges { get; set; } = new();
}
```

with:

```csharp
public class DiscordUserDto
{
    public string Id { get; set; } = null!;
    public string Username { get; set; } = null!;
    public string? GlobalName { get; set; }
    public string? AvatarUrl { get; set; }
    public List<string> Badges { get; set; } = new();
}

/// <summary>
/// What to enter in the Vencord plugin: the tracker's public URL and the SHA-256
/// fingerprint of its certificate. Each is null while the administrator has not set it.
/// </summary>
public class DiscordIngestConfigDto
{
    public string? PublicUrl { get; set; }
    public string? CertificateSha256 { get; set; }
}
```

Replace the whole content of `src/Collector.Api/Controllers/DiscordController.cs`:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collector.Api.Controllers;

/// <summary>Resolves a public Discord profile from a numeric user id (bot-token backed).</summary>
[ApiController]
[Route("api")]
[Authorize]
public class DiscordController : ControllerBase
{
    private readonly DiscordClient _discord;

    public DiscordController(DiscordClient discord) => _discord = discord;

    [HttpGet("discord/users/{id}")]
    public async Task<ActionResult<DiscordUserDto>> GetUser(string id, CancellationToken ct)
    {
        var user = await _discord.GetUserAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }
}
```

with:

```csharp
using Collector.Api.Dtos.Discord;
using Collector.Api.Options;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Collector.Api.Controllers;

/// <summary>
/// Resolves a public Discord profile from a numeric user id (bot-token backed), and tells
/// signed-in users what to enter in the Vencord plugin that uploads Discord rosters.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class DiscordController : ControllerBase
{
    private readonly DiscordClient _discord;
    private readonly DiscordOptions.IngestOptions _ingest;

    public DiscordController(DiscordClient discord, IOptions<DiscordOptions> options)
    {
        _discord = discord;
        _ingest = options.Value.Ingest;
    }

    [HttpGet("discord/users/{id}")]
    public async Task<ActionResult<DiscordUserDto>> GetUser(string id, CancellationToken ct)
    {
        var user = await _discord.GetUserAsync(id, ct);
        return user is null ? NotFound() : Ok(user);
    }

    /// <summary>
    /// Public URL and certificate fingerprint of the ingest route, from <c>Discord:Ingest</c>
    /// (api.env). Null while unset: the settings panel then sends users to the administrator.
    /// </summary>
    [HttpGet("discord/ingest-config")]
    public ActionResult<DiscordIngestConfigDto> GetIngestConfig() => Ok(new DiscordIngestConfigDto
    {
        PublicUrl = NullIfBlank(_ingest.PublicUrl),
        CertificateSha256 = NullIfBlank(_ingest.CertificateSha256),
    });

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

- [ ] **Step 4: Run the API tests to verify they pass**

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordIngestConfigTests"`

Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3`.

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~AuthorizationTests"`

Expected: `Passed!  - Failed:     0` (the new route answers 401 to an anonymous caller and to a `discord:ingest` key, like every `[Authorize]` route).

- [ ] **Step 5: Write the failing schema tests**

In `src/Collector.Web/src/lib/validation.test.ts`, replace:

```ts
import {
  handleSchema,
  idSchema,
  linkProviderSchema,
  noteBodySchema,
  sidSchema,
  valid,
} from "./validation";
```

with:

```ts
import {
  apiKeyNameSchema,
  handleSchema,
  idSchema,
  ingestKeyExpiryDaysSchema,
  linkProviderSchema,
  noteBodySchema,
  sidSchema,
  valid,
} from "./validation";
```

Then replace:

```ts
  it("only knows the link providers the API accepts", () => {
    expect(valid(linkProviderSchema, "twitch")).toBe(true);
    expect(valid(linkProviderSchema, "javascript")).toBe(false);
  });
});
```

with:

```ts
  it("only knows the link providers the API accepts", () => {
    expect(valid(linkProviderSchema, "twitch")).toBe(true);
    expect(valid(linkProviderSchema, "javascript")).toBe(false);
  });

  it("bounds API key names to what the API stores", () => {
    expect(valid(apiKeyNameSchema, "Vencord 2026-10-01")).toBe(true);
    expect(valid(apiKeyNameSchema, "x".repeat(100))).toBe(true);
    expect(valid(apiKeyNameSchema, "   ")).toBe(false);
    expect(valid(apiKeyNameSchema, "x".repeat(101))).toBe(false);
    expect(valid(apiKeyNameSchema, 42)).toBe(false);
  });

  it.each([1, 180, 365])("accepts an ingest key lifetime of %s days", (days) => {
    expect(valid(ingestKeyExpiryDaysSchema, days)).toBe(true);
  });

  it.each([0, 366, 1.5, -1, "180", Number.NaN, null])("rejects an ingest key lifetime of %j", (days) => {
    expect(valid(ingestKeyExpiryDaysSchema, days)).toBe(false);
  });
});
```

- [ ] **Step 6: Run it to verify it fails**

Run, from `src/Collector.Web` (the repository root makes corepack pick another pnpm): `pnpm exec vitest run src/lib/validation.test.ts`

Expected: `Test Files  1 failed (1)` and `Tests  11 failed | 29 passed (40)`. Every new test fails with `TypeError: Cannot read properties of undefined (reading 'safeParse')`: `valid()` receives the schemas that do not exist yet.

- [ ] **Step 7: Write the minimal implementation (schemas)**

In `src/Collector.Web/src/lib/validation.ts`, replace:

```ts
/** Discord bot token (admin setting). */
export const discordTokenSchema = z.string().trim().min(20).max(200);
```

with:

```ts
/** Discord bot token (admin setting). */
export const discordTokenSchema = z.string().trim().min(20).max(200);

/** API key name (the API stores up to 100 characters). */
export const apiKeyNameSchema = z.string().trim().min(1).max(100);

/** Lifetime of a Discord ingest key, in whole days: the API refuses more than 365. */
export const ingestKeyExpiryDaysSchema = z.number().int().min(1).max(365);
```

- [ ] **Step 8: Run the tests to verify they pass**

Run, from `src/Collector.Web`: `pnpm exec vitest run src/lib/validation.test.ts`

Expected: `Test Files  1 passed (1)` and `Tests  40 passed (40)`.

- [ ] **Step 9: Write the failing endpoint tests**

In `src/Collector.Web/src/lib/api/endpoints.test.ts`, replace:

```ts
import { getOrgMembersPage } from "./endpoints";
```

with:

```ts
import { getDiscordIngestConfig, getOrgMembersPage, listApiKeys } from "./endpoints";
```

Then append at the end of the file:

```ts

describe("settings endpoints", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubFetch(body: unknown) {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(body), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  function firstCall(fetchMock: ReturnType<typeof stubFetch>) {
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    return { url: new URL(url), init };
  }

  it("lists the user's API keys with the user's token", async () => {
    const fetchMock = stubFetch([]);

    await listApiKeys({ bearerToken: "t" });

    const { url, init } = firstCall(fetchMock);
    expect(url.pathname).toBe("/api/api-keys");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer t");
  });

  it("reads the plugin settings from the Discord ingest config", async () => {
    const fetchMock = stubFetch({ publicUrl: null, certificateSha256: null });

    const config = await getDiscordIngestConfig({ bearerToken: "t" });

    expect(firstCall(fetchMock).url.pathname).toBe("/api/discord/ingest-config");
    expect(config).toEqual({ publicUrl: null, certificateSha256: null });
  });
});
```

- [ ] **Step 10: Run it to verify it fails**

Run, from `src/Collector.Web`: `pnpm exec vitest run src/lib/api/endpoints.test.ts`

Expected: `Test Files  1 failed (1)` and `Tests  2 failed | 1 passed (3)`: `FAIL  src/lib/api/endpoints.test.ts > settings endpoints > …` with `TypeError: listApiKeys is not a function` and `TypeError: getDiscordIngestConfig is not a function`; the `getOrgMembersPage` test still passes.

- [ ] **Step 11: Write the minimal implementation (types and endpoints)**

In `src/Collector.Web/src/lib/api/types.ts`, replace:

```ts
// ── Health ──────────────────────────────────────────────────
export interface CycleStatusDto {
  queue_pending: number;
  queue_stuck: number;
  last_member_collection: { org_sid: string; at: string } | null;
  discovered_orgs: number;
}
```

with:

```ts
// ── Health ──────────────────────────────────────────────────
export interface CycleStatusDto {
  queue_pending: number;
  queue_stuck: number;
  last_member_collection: { org_sid: string; at: string } | null;
  discovered_orgs: number;
}

// ── API keys ────────────────────────────────────────────────
export interface ApiKeyDto {
  id: number;
  name: string;
  keyPrefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
  isRevoked: boolean;
  /** null: full access; "discord:ingest": Discord roster uploads only. */
  scope: string | null;
}

/** Returned once, at creation: the raw key is never shown again. */
export type CreatedApiKeyDto = ApiKeyDto & { rawKey: string };

// ── Discord ─────────────────────────────────────────────────
/** What to enter in the Vencord plugin; null while the administrator has not set it. */
export interface DiscordIngestConfigDto {
  publicUrl: string | null;
  certificateSha256: string | null;
}
```

In `src/Collector.Web/src/lib/api/endpoints.ts`, replace:

```ts
import type {
  ArchetypeStatsDto,
  AuthResponse,
  ChangeEventDto,
  ChangeSummaryDto,
  CycleStatusDto,
  GrowthDataPoint,
```

with:

```ts
import type {
  ApiKeyDto,
  ArchetypeStatsDto,
  AuthResponse,
  ChangeEventDto,
  ChangeSummaryDto,
  CycleStatusDto,
  DiscordIngestConfigDto,
  GrowthDataPoint,
```

Then replace:

```ts
export const me = (bearerToken: string) =>
  apiGet<UserDto>("/api/auth/me", undefined, { bearerToken });
```

with:

```ts
export const me = (bearerToken: string) =>
  apiGet<UserDto>("/api/auth/me", undefined, { bearerToken });

// ── API keys ────────────────────────────────────────────────
/** The signed-in user's keys, newest first (revoked ones included). */
export const listApiKeys = (ctx: Ctx = {}) =>
  apiGet<ApiKeyDto[]>("/api/api-keys", undefined, ctx);

// ── Discord ingest ──────────────────────────────────────────
/** Public URL and certificate fingerprint to enter in the Vencord plugin. */
export const getDiscordIngestConfig = (ctx: Ctx = {}) =>
  apiGet<DiscordIngestConfigDto>("/api/discord/ingest-config", undefined, ctx);
```

- [ ] **Step 12: Run the tests to verify they pass**

Run, from `src/Collector.Web`: `pnpm exec vitest run src/lib/api/endpoints.test.ts`

Expected: `Test Files  1 passed (1)` and `Tests  3 passed (3)`.

- [ ] **Step 13: Write the failing server action tests**

Create `src/Collector.Web/src/app/(user)/settings/discord-key-actions.test.ts`:

```ts
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("next/headers", () => ({ cookies: async () => ({ set: vi.fn() }), headers: async () => new Headers() }));
vi.mock("@/lib/auth/session", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/auth/session")>()),
  getSession: vi.fn(async () => ({
    userId: 1,
    username: "pilot",
    isAdmin: false,
    accessToken: "jwt",
    clientIp: "203.0.113.7",
    expiresAt: new Date(),
  })),
}));
vi.mock("@/lib/api/client", () => ({ apiPost: vi.fn(), apiDelete: vi.fn() }));

const { apiDelete, apiPost } = await import("@/lib/api/client");
const { createDiscordIngestKeyAction, revokeApiKeyAction } = await import("./discord-key-actions");

/** What sessionCtx() gives the API client for the mocked session. */
const ctx = { bearerToken: "jwt", clientIp: "203.0.113.7" };

describe("createDiscordIngestKeyAction", () => {
  beforeEach(() => {
    vi.mocked(apiPost).mockReset();
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-01T10:00:00Z"));
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("creates a discord:ingest key that expires after the chosen number of days", async () => {
    const created = {
      id: 7,
      name: "Vencord 2026-10-01",
      keyPrefix: "Ab3dE9",
      createdAt: "2026-10-01T10:00:00Z",
      lastUsedAt: null,
      expiresAt: "2027-03-30T10:00:00Z",
      isRevoked: false,
      scope: "discord:ingest",
      rawKey: "Ab3dE9_c2VjcmV0",
    };
    vi.mocked(apiPost).mockResolvedValue(created);

    const result = await createDiscordIngestKeyAction("  Vencord 2026-10-01  ", 180);

    expect(result).toEqual({ ok: true, data: created });
    expect(apiPost).toHaveBeenCalledWith(
      "/api/api-keys",
      { name: "Vencord 2026-10-01", expiresAt: "2027-03-30T10:00:00.000Z", scope: "discord:ingest" },
      ctx,
    );
  });

  it("asks for the longest lifetime the API accepts, 365 days", async () => {
    vi.mocked(apiPost).mockResolvedValue({});

    await createDiscordIngestKeyAction("Vencord", 365);

    expect(vi.mocked(apiPost).mock.calls[0]?.[1]).toEqual({
      name: "Vencord",
      expiresAt: "2027-10-01T10:00:00.000Z",
      scope: "discord:ingest",
    });
  });

  it("reports the API's refusal instead of throwing", async () => {
    vi.mocked(apiPost).mockRejectedValue(new Error("Bad Request"));

    expect(await createDiscordIngestKeyAction("Vencord", 30)).toEqual({ ok: false, error: "Bad Request" });
  });
});

describe("revokeApiKeyAction", () => {
  // Braces matter: a function returned by beforeEach runs as its teardown, and
  // mockReset() returns the mock itself.
  beforeEach(() => {
    vi.mocked(apiDelete).mockReset();
  });

  it("revokes the key by its id", async () => {
    vi.mocked(apiDelete).mockResolvedValue(undefined);

    expect(await revokeApiKeyAction(12)).toEqual({ ok: true });
    expect(apiDelete).toHaveBeenCalledWith("/api/api-keys/12", ctx);
  });

  it("reports a key the API does not find for this user", async () => {
    vi.mocked(apiDelete).mockRejectedValue(new Error("Not Found"));

    expect(await revokeApiKeyAction(99)).toEqual({ ok: false, error: "Not Found" });
  });
});
```

In `src/Collector.Web/src/app/action-arguments.test.ts`, replace:

```ts
const { setDiscordTokenAction } = await import("./(user)/settings/discord-token-actions");
```

with:

```ts
const { setDiscordTokenAction } = await import("./(user)/settings/discord-token-actions");
const { createDiscordIngestKeyAction, revokeApiKeyAction } = await import("./(user)/settings/discord-key-actions");
```

Then replace:

```ts
  it.each([
    ["no token", () => setDiscordTokenAction(anyValue(null))],
    ["an object", () => setDiscordTokenAction(anyValue({ token: "x" }))],
  ])("setDiscordTokenAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });
```

with:

```ts
  it.each([
    ["no token", () => setDiscordTokenAction(anyValue(null))],
    ["an object", () => setDiscordTokenAction(anyValue({ token: "x" }))],
  ])("setDiscordTokenAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["no name", () => createDiscordIngestKeyAction(undefined, 180)],
    ["a blank name", () => createDiscordIngestKeyAction("   ", 180)],
    ["a name too long", () => createDiscordIngestKeyAction("x".repeat(101), 180)],
    ["an expiry as text", () => createDiscordIngestKeyAction("Vencord", "180")],
    ["an expiry of zero days", () => createDiscordIngestKeyAction("Vencord", 0)],
    ["an expiry beyond 365 days", () => createDiscordIngestKeyAction("Vencord", 366)],
    ["a fractional expiry", () => createDiscordIngestKeyAction("Vencord", 1.5)],
  ])("createDiscordIngestKeyAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });

  it.each([
    ["a path as id", () => revokeApiKeyAction("../admin/users/3")],
    ["an id as text", () => revokeApiKeyAction("12")],
    ["a negative id", () => revokeApiKeyAction(-1)],
  ])("revokeApiKeyAction: %s", async (_, call) => {
    expect(await call()).toEqual(invalid);
    noApiCall();
  });
```

- [ ] **Step 14: Run them to verify they fail**

Run, from `src/Collector.Web`: `pnpm exec vitest run "src/app/(user)/settings/discord-key-actions.test.ts" src/app/action-arguments.test.ts`

Expected: `Test Files  2 failed (2)` and `Tests  no tests`. Both files fail to load, with `Error: Cannot find module '/src/app/(user)/settings/discord-key-actions' imported from …`: the module does not exist yet.

- [ ] **Step 15: Write the minimal implementation (server actions)**

Create `src/Collector.Web/src/app/(user)/settings/discord-key-actions.ts`:

```ts
"use server";

import {
  INVALID_ARGUMENTS,
  apiKeyNameSchema,
  idSchema,
  ingestKeyExpiryDaysSchema,
} from "@/lib/validation";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiDelete, apiPost } from "@/lib/api/client";
import type { CreatedApiKeyDto } from "@/lib/api/types";

/** The only kind of key the site creates: it uploads Discord rosters and nothing else. */
const DISCORD_INGEST_SCOPE = "discord:ingest";
const DAY_MS = 24 * 60 * 60 * 1000;

export interface CreateDiscordIngestKeyResult {
  ok: boolean;
  /** Carries the raw key: the panel shows it once, the site never stores it. */
  data?: CreatedApiKeyDto;
  error?: string;
}

export interface RevokeApiKeyResult {
  ok: boolean;
  error?: string;
}

export async function createDiscordIngestKeyAction(
  name: unknown,
  expiresInDays: unknown,
): Promise<CreateDiscordIngestKeyResult> {
  const parsedName = apiKeyNameSchema.safeParse(name);
  const parsedDays = ingestKeyExpiryDaysSchema.safeParse(expiresInDays);
  if (!parsedName.success || !parsedDays.success) return { ok: false, error: INVALID_ARGUMENTS };
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };

  // Computed on the server, whose clock is the API's: it refuses more than 365 days.
  const expiresAt = new Date(Date.now() + parsedDays.data * DAY_MS).toISOString();
  try {
    const data = await apiPost<CreatedApiKeyDto>(
      "/api/api-keys",
      { name: parsedName.data, expiresAt, scope: DISCORD_INGEST_SCOPE },
      sessionCtx(session),
    );
    return { ok: true, data };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec de la création de la clé." };
  }
}

export async function revokeApiKeyAction(id: unknown): Promise<RevokeApiKeyResult> {
  const parsed = idSchema.safeParse(id);
  if (!parsed.success) return { ok: false, error: INVALID_ARGUMENTS };
  const session = await getSession();
  if (!session) return { ok: false, error: "Non authentifié." };
  try {
    await apiDelete(`/api/api-keys/${parsed.data}`, sessionCtx(session));
    return { ok: true };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : "Échec de la révocation." };
  }
}
```

- [ ] **Step 16: Run the tests to verify they pass**

Run, from `src/Collector.Web`: `pnpm exec vitest run "src/app/(user)/settings/discord-key-actions.test.ts" src/app/action-arguments.test.ts`

Expected: `Test Files  2 passed (2)` and `Tests  35 passed (35)`: 5 in `discord-key-actions.test.ts`, 30 in `action-arguments.test.ts` (the 10 new cases, and `still passes well-formed arguments through` still sees exactly 2 `apiPost` calls).

- [ ] **Step 17: Render the panel from the settings page (the consumer first)**

In `src/Collector.Web/src/app/(user)/settings/page.tsx`, replace:

```tsx
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiGet } from "@/lib/api/client";
import { formatDate } from "@/lib/utils/format";
import { ChangePasswordForm } from "./ChangePasswordForm";
import { DiscordTokenForm } from "./DiscordTokenForm";
```

with:

```tsx
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiGet } from "@/lib/api/client";
import { getDiscordIngestConfig, listApiKeys } from "@/lib/api/endpoints";
import { formatDate } from "@/lib/utils/format";
import { ChangePasswordForm } from "./ChangePasswordForm";
import { DiscordIngestKeysPanel } from "./DiscordIngestKeysPanel";
import { DiscordTokenForm } from "./DiscordTokenForm";
```

Then replace:

```tsx
    : false;

  return (
```

with:

```tsx
    : false;

  // Neither is essential to the page: the panel says what is missing instead.
  const [ingestConfig, apiKeys] = await Promise.all([
    getDiscordIngestConfig(sessionCtx(session)).catch(() => null),
    listApiKeys(sessionCtx(session)).catch(() => null),
  ]);

  return (
```

Then replace:

```tsx
      <HudPanel label="API KEYS" accent="orange">
        <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
          — API key management UI arriving in v2 —
          <br />
          Use the /api/api-keys endpoints directly for now.
        </p>
      </HudPanel>
```

with:

```tsx
      <DiscordIngestKeysPanel
        config={ingestConfig}
        keys={apiKeys}
        defaultName={`Vencord ${new Date().toISOString().slice(0, 10)}`}
      />
```

- [ ] **Step 18: Run the typecheck to verify it fails**

Run, from `src/Collector.Web`: `pnpm typecheck`

Expected: exits non-zero with one error, `src/app/(user)/settings/page.tsx(…): error TS2307: Cannot find module './DiscordIngestKeysPanel' or its corresponding type declarations.`

- [ ] **Step 19: Write the panel**

Create `src/Collector.Web/src/app/(user)/settings/DiscordIngestKeysPanel.tsx`:

```tsx
"use client";
import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudButton } from "@/components/hud/HudButton";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import { HudInput } from "@/components/hud/HudInput";
import { HudPanel } from "@/components/hud/HudPanel";
import type { ApiKeyDto, DiscordIngestConfigDto } from "@/lib/api/types";
import { formatDate } from "@/lib/utils/format";
import { createDiscordIngestKeyAction, revokeApiKeyAction } from "./discord-key-actions";

const DEFAULT_EXPIRY_DAYS = 180;
const MAX_EXPIRY_DAYS = 365;

interface DiscordIngestKeysPanelProps {
  /** Plugin settings published by the API; null when they could not be read. */
  config: DiscordIngestConfigDto | null;
  /** The user's API keys, newest first; null when the list could not be read. */
  keys: ApiKeyDto[] | null;
  /** "Vencord <date>", computed by the page so the server and the browser render the same value. */
  defaultName: string;
}

async function copyToClipboard(value: string, what: string) {
  try {
    await navigator.clipboard.writeText(value);
    toast.success(`${what} copiée.`);
  } catch {
    toast.error("Copie impossible : sélectionne le texte et copie-le à la main.");
  }
}

/** A value to paste into the plugin, with its copy button. */
function CopyField({ label, value, what }: { label: string; value: string; what: string }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="hud-label">{label}</span>
      <div className="flex items-center gap-2">
        <code className="min-w-0 flex-1 break-all border border-hud-cyan-dim bg-hud-bg/60 px-2 py-1 font-mono text-xs text-hud-cyan">
          {value}
        </code>
        <HudButton type="button" variant="ghost" onClick={() => copyToClipboard(value, what)}>
          COPIER
        </HudButton>
      </div>
    </div>
  );
}

function scopeBadge(scope: string | null) {
  if (scope === null) return <HudBadge tone="orange">COMPLÈTE</HudBadge>;
  if (scope === "discord:ingest") return <HudBadge tone="cyan">ENVOI DISCORD</HudBadge>;
  return <HudBadge tone="dim">{scope}</HudBadge>;
}

function isExpired(key: ApiKeyDto) {
  return key.expiresAt !== null && new Date(key.expiresAt).getTime() <= Date.now();
}

/**
 * Settings panel for the Vencord plugin: what to enter in it (URL and certificate
 * fingerprint), a form that creates a discord:ingest key shown once, and the user's
 * keys with a revoke button. Creating and revoking go through server actions; the list
 * comes from the page and is read again with router.refresh().
 */
export function DiscordIngestKeysPanel({ config, keys, defaultName }: DiscordIngestKeysPanelProps) {
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [revokingId, setRevokingId] = useState<number | null>(null);
  const [rawKey, setRawKey] = useState<string | null>(null);

  const publicUrl = config?.publicUrl ?? null;
  const fingerprint = config?.certificateSha256 ?? null;

  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = new FormData(e.currentTarget);
    const name = String(form.get("name") ?? "").trim();
    const days = Number(form.get("expiresInDays"));
    if (!Number.isInteger(days) || days < 1 || days > MAX_EXPIRY_DAYS) {
      toast.error(`Expiration : un nombre entier de jours, de 1 à ${MAX_EXPIRY_DAYS}.`);
      return;
    }
    setCreating(true);
    const res = await createDiscordIngestKeyAction(name, days);
    setCreating(false);
    if (res.ok && res.data) {
      setRawKey(res.data.rawKey);
      toast.success("Clé créée.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function revoke(key: ApiKeyDto) {
    if (!confirm(`Révoquer la clé « ${key.name} » ? Le plugin qui l'utilise ne pourra plus rien envoyer.`)) return;
    setRevokingId(key.id);
    const res = await revokeApiKeyAction(key.id);
    setRevokingId(null);
    if (res.ok) {
      toast.success("Clé révoquée.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  const columns: HudColumn<ApiKeyDto>[] = [
    { key: "name", header: "NOM", render: (k) => <span className="break-all text-hud-text">{k.name}</span> },
    { key: "scope", header: "PORTÉE", width: "w-32", render: (k) => scopeBadge(k.scope) },
    { key: "createdAt", header: "CRÉÉE LE", width: "w-28", render: (k) => formatDate(k.createdAt) },
    {
      key: "expiresAt",
      header: "EXPIRE LE",
      width: "w-28",
      render: (k) => (k.expiresAt ? formatDate(k.expiresAt) : "jamais"),
    },
    {
      key: "lastUsedAt",
      header: "DERNIÈRE UTILISATION",
      width: "w-32",
      render: (k) => (k.lastUsedAt ? formatDate(k.lastUsedAt) : "jamais"),
    },
    {
      key: "revoke",
      header: "",
      width: "w-28",
      align: "right",
      render: (k) =>
        k.isRevoked ? (
          <HudBadge tone="dim">RÉVOQUÉE</HudBadge>
        ) : isExpired(k) ? (
          <HudBadge tone="red">EXPIRÉE</HudBadge>
        ) : (
          <HudButton
            type="button"
            variant="danger"
            className="px-2 py-1"
            disabled={revokingId === k.id}
            onClick={() => revoke(k)}
          >
            {revokingId === k.id ? "…" : "RÉVOQUER"}
          </HudButton>
        ),
    },
  ];

  return (
    <HudPanel label="CLÉ D'ENVOI DISCORD" accent="orange">
      <div className="flex flex-col gap-6">
        <section className="flex flex-col gap-3">
          <div className="hud-label text-hud-text-dim">CONFIGURATION DU PLUGIN</div>
          {publicUrl && fingerprint ? (
            <>
              <CopyField label="URL DU TRACKER" value={publicUrl} what="URL" />
              <CopyField label="EMPREINTE SHA-256 DU CERTIFICAT" value={fingerprint} what="Empreinte" />
            </>
          ) : (
            <p className="font-mono text-xs text-hud-orange">
              {"Demande l'URL et l'empreinte à l'administrateur."}
            </p>
          )}
        </section>

        <section className="flex flex-col gap-3">
          <div className="hud-label text-hud-text-dim">CRÉER UNE CLÉ</div>
          <form onSubmit={create} className="flex flex-col gap-3">
            <div className="grid gap-3 sm:grid-cols-[1fr_11rem]">
              <HudInput
                label="NOM"
                name="name"
                type="text"
                required
                maxLength={100}
                autoComplete="off"
                defaultValue={defaultName}
              />
              <HudInput
                label={`EXPIRATION (JOURS, ≤ ${MAX_EXPIRY_DAYS})`}
                name="expiresInDays"
                type="number"
                required
                min={1}
                max={MAX_EXPIRY_DAYS}
                step={1}
                defaultValue={DEFAULT_EXPIRY_DAYS}
              />
            </div>
            <div className="flex justify-end">
              <HudButton type="submit" disabled={creating}>
                {creating ? "…" : "CRÉER LA CLÉ"}
              </HudButton>
            </div>
          </form>
          {rawKey && (
            <div className="flex flex-col gap-2 border border-hud-orange/60 bg-hud-orange/5 p-3">
              <p className="font-mono text-xs text-hud-orange">
                {"Copie cette clé et colle-la dans le plugin maintenant : elle ne sera plus jamais affichée."}
              </p>
              <CopyField label="CLÉ D'API" value={rawKey} what="Clé" />
              <div className="flex justify-end">
                <HudButton type="button" variant="ghost" onClick={() => setRawKey(null)}>
                  {"J'AI COPIÉ LA CLÉ"}
                </HudButton>
              </div>
            </div>
          )}
        </section>

        <section className="flex flex-col gap-3">
          <div className="hud-label text-hud-text-dim">MES CLÉS</div>
          {keys ? (
            <HudDataGrid columns={columns} rows={keys} rowKey={(k) => String(k.id)} empty="Aucune clé." />
          ) : (
            <p className="font-mono text-xs text-hud-red">Liste des clés indisponible.</p>
          )}
          <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            {"Rotation : crée une nouvelle clé, colle-la dans le plugin, puis révoque l'ancienne."}
          </p>
        </section>
      </div>
    </HudPanel>
  );
}
```

- [ ] **Step 20: Run the typecheck and the whole web suite to verify they pass**

Run, from `src/Collector.Web`: `pnpm typecheck`

Expected: exits 0 with no `error TS` line.

Run, from `src/Collector.Web`: `pnpm test`

Expected: the whole suite passes, `Test Files  23 passed (23)` and `Tests  196 passed (196)` (22 files and 168 tests before this task, plus `discord-key-actions.test.ts` and the 28 new tests). `src/lib/api/server-boundary.test.ts` is among them: the panel imports only `@/lib/api/types` (type-only) and the server actions, never `@/lib/api/client` or `@/lib/api/endpoints`.

- [ ] **Step 21: Commit**

```bash
git add src/Collector.Api/Options/DiscordOptions.cs \
  src/Collector.Api/Extensions/ServiceCollectionExtensions.cs \
  src/Collector.Api/Dtos/Discord/DiscordDtos.cs \
  src/Collector.Api/Controllers/DiscordController.cs \
  src/Collector.Api.Tests/ApiFactory.cs \
  src/Collector.Api.Tests/Discord/DiscordIngestConfigTests.cs \
  src/Collector.Web/src/lib/api/types.ts \
  src/Collector.Web/src/lib/api/endpoints.ts \
  src/Collector.Web/src/lib/api/endpoints.test.ts \
  src/Collector.Web/src/lib/validation.ts \
  src/Collector.Web/src/lib/validation.test.ts \
  "src/Collector.Web/src/app/(user)/settings/discord-key-actions.ts" \
  "src/Collector.Web/src/app/(user)/settings/discord-key-actions.test.ts" \
  "src/Collector.Web/src/app/(user)/settings/DiscordIngestKeysPanel.tsx" \
  "src/Collector.Web/src/app/(user)/settings/page.tsx" \
  src/Collector.Web/src/app/action-arguments.test.ts
git commit -F - <<'EOF'
feat(web): the settings page creates and revokes Discord ingest keys

The "v2" API keys placeholder becomes the panel a sender needs to set up the
Vencord plugin: the public URL and certificate fingerprint to paste, read from
the new GET api/discord/ingest-config (Discord:Ingest in api.env), or a pointer to
the administrator while they are unset; a form that creates a discord:ingest key
for 180 days by default (365 at most) and shows the raw key once; and the user's
keys with their scope, dates and a revoke button. The site creates no other kind
of key: the server action fixes the scope and validates its arguments before any
API call.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```

### Task A14: Deployment, configuration and documentation

**Files:**
- Modify: `deploy/nginx/sc-tracker.conf` (http-level zones after `sc_login`; new location before `location /`)
- Modify: `deploy/env/api.env.example` (header comment; new variables after `Discord__BotToken`)
- Modify: `deploy/README.md` (new section "Rosters Discord (plugin Vencord, lot 14)" before "## Tests")
- Modify: `README.md` (Architecture diagram, paragraph and `tracker.db` bullet, Sécurité, Variables d'environnement)
- Modify: `src/Collector.Web/README.md` (Pages table, `/settings` row)
- Modify: `src/Collector.Api/Program.cs` (the forwarded-headers comment and the "browsers never talk to it" comment)
- Test: none in the test projects. The check is a grep script run from the repository root; `nginx -t` runs on the server during deployment (nginx is not available locally).

**Interfaces:**
- Consumes:
  - CONTRACTS § 5: `DiscordIngestController` at `api/ingest/discord`, action `POST guilds/{guildId}/syncs`; § 4: `DiscordIngestAuth.PathPrefix = "/api/ingest/discord"`, `RateLimitSettings.DiscordIngest { PermitLimit = 20, WindowSeconds = 600 }` under `Api:RateLimit:DiscordIngest`, `DiscordOptions` sections `Discord:Ingest:*` and `Discord:Retention:*`; § 5 admin routes `DELETE api/discord/accounts/{discordUserId}`, `DELETE api/discord/guilds/{guildId}?exclude=`, `POST api/discord/guilds/{guildId}/allow-mass-departure`.
  - Task A13: `GET api/discord/ingest-config`, `DiscordOptions` bound in `AddApiServices`, the settings panel "CLÉ D'ENVOI DISCORD".
- Produces:
  - nginx zones `sc_discord_ingest` (10 r/m) and `sc_discord_conn` at http level, and `location /ingest/discord/` → `http://127.0.0.1:5000/api/ingest/discord/` (POST only, burst 10, 2 connections, 25 MB) before `location /`. The zones sit next to `sc_login` because `limit_req_zone` and `limit_conn_zone` are only valid in the `http` context, where this file is included.
  - Operator documentation (French) and commented `api.env` variables. The documented fingerprint value is the part after `Fingerprint=` (no space, so `api.env` can still be sourced with `set -a; .`); the plugin accepts the full openssl line too.

- [ ] **Step 1: Write the failing check**

The check, run from the repository root in Git Bash:

```bash
check() { if grep -qF -- "$2" "$1"; then echo "ok      $1: $2"; else echo "MISSING $1: $2"; fi; }
check deploy/nginx/sc-tracker.conf 'limit_req_zone  $binary_remote_addr zone=sc_discord_ingest:1m rate=10r/m;'
check deploy/nginx/sc-tracker.conf 'limit_conn_zone $binary_remote_addr zone=sc_discord_conn:1m;'
check deploy/nginx/sc-tracker.conf 'limit_except POST { deny all; }'
check deploy/nginx/sc-tracker.conf 'limit_req zone=sc_discord_ingest burst=10 nodelay;'
check deploy/nginx/sc-tracker.conf 'limit_conn sc_discord_conn 2;'
check deploy/nginx/sc-tracker.conf 'client_max_body_size 25m;'
check deploy/nginx/sc-tracker.conf 'proxy_pass http://127.0.0.1:5000/api/ingest/discord/;'
awk '/zone=sc_discord_ingest:1m/ { z = NR } /^server \{/ && !s { s = NR }
     /location \/ingest\/discord\/ \{/ { d = NR } /location \/ \{/ { r = NR }
     END { print ((z && z < s) ? "ok      zones at http level" : "MISSING zones at http level");
           print ((d && d < r) ? "ok      ingest location before location /" : "MISSING ingest location before location /") }' \
  deploy/nginx/sc-tracker.conf
check deploy/env/api.env.example 'COLLECTOR_API_Discord__Ingest__PublicUrl='
check deploy/env/api.env.example 'COLLECTOR_API_Discord__Ingest__CertificateSha256='
check deploy/env/api.env.example '#COLLECTOR_API_Api__RateLimit__DiscordIngest__PermitLimit=20'
check deploy/env/api.env.example '#COLLECTOR_API_Discord__Retention__DepartedAccountDays=730'
check deploy/README.md '## Rosters Discord (plugin Vencord, lot 14)'
check deploy/README.md 'openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256'
check deploy/README.md 'COLLECTOR_API_Discord__Ingest__CertificateSha256=AB:CD:…'
check deploy/README.md '**Certificat régénéré**'
check README.md '| API | `COLLECTOR_API_Discord__Ingest__PublicUrl` |'
check README.md "Une seule route publique mène à l'API"
check README.md 'seule, les rosters Discord (tables `discord_*`)'
check src/Collector.Web/README.md "| \`/settings\` | Mot de passe, jeton Discord, clé d'envoi Discord |"
check src/Collector.Api/Program.cs 'relays one public route, /ingest/discord/ to /api/ingest/discord/'
```

- [ ] **Step 2: Run it to verify it fails**

Run the Step 1 script.

Expected: 22 lines, every one starting with `MISSING`.

- [ ] **Step 3: Add the zones and the location to nginx**

In `deploy/nginx/sc-tracker.conf`, replace:

```nginx
# Credential stuffing guard in front of the app's own per-IP login limit.
limit_req_zone $binary_remote_addr zone=sc_login:10m rate=5r/m;
```

with:

```nginx
# Credential stuffing guard in front of the app's own per-IP login limit.
limit_req_zone $binary_remote_addr zone=sc_login:10m rate=5r/m;

# Discord roster uploads from the Vencord plugin (location /ingest/discord/ below):
# a few manual uploads an hour, at most two at once per address.
limit_req_zone  $binary_remote_addr zone=sc_discord_ingest:1m rate=10r/m;
limit_conn_zone $binary_remote_addr zone=sc_discord_conn:1m;
```

Then replace:

```nginx
    location / {
        proxy_pass http://127.0.0.1:3000;
    }
```

with:

```nginx
    # The only route that reaches the API directly. The API accepts nothing but
    # discord:ingest keys under /api/ingest/discord, and those keys nowhere else.
    # Request buffering stays on (default): the API only sees complete bodies, so a
    # slow client never holds the ingest lock.
    location /ingest/discord/ {
        limit_except POST { deny all; }
        limit_req zone=sc_discord_ingest burst=10 nodelay;
        limit_req_status 429;
        limit_conn sc_discord_conn 2;
        client_max_body_size 25m;
        proxy_pass http://127.0.0.1:5000/api/ingest/discord/;
    }

    location / {
        proxy_pass http://127.0.0.1:3000;
    }
```

- [ ] **Step 4: Add the variables to `deploy/env/api.env.example`**

Replace:

```bash
# Production: no user-secrets, no exception details in responses, no Swagger.
# Plain HTTP on loopback only — nginx terminates TLS, browsers never reach the API.
```

with:

```bash
# Production: no user-secrets, no exception details in responses, no Swagger.
# Plain HTTP on loopback only — nginx terminates TLS. Browsers never reach the API;
# nginx relays a single public route to it, /ingest/discord/ (Vencord plugin uploads).
```

Then replace:

```bash
# Discord bot token (previously in sc-api.service.d/discord.conf).
Discord__BotToken=
```

with:

```bash
# Discord bot token (previously in sc-api.service.d/discord.conf).
Discord__BotToken=

# Vencord plugin settings, shown to every user in Settings → Clé d'envoi Discord (the
# panel sends users to the administrator while either is empty). See deploy/README.md.
# Public URL of the tracker, scheme and host only: https://<IP>
COLLECTOR_API_Discord__Ingest__PublicUrl=
# SHA-256 fingerprint of the nginx certificate: the part after "Fingerprint=" of
#   openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256
COLLECTOR_API_Discord__Ingest__CertificateSha256=

# Optional: uploads per sender and per window (seconds), on top of nginx's 10 requests
# a minute per address.
#COLLECTOR_API_Api__RateLimit__DiscordIngest__PermitLimit=20
#COLLECTOR_API_Api__RateLimit__DiscordIngest__WindowSeconds=600
# Optional: retention in days of the upload journal, and of unlinked accounts gone from
# every server.
#COLLECTOR_API_Discord__Retention__SyncLogDays=365
#COLLECTOR_API_Discord__Retention__DepartedAccountDays=730
```

- [ ] **Step 5: Document the procedure in `deploy/README.md`**

Replace:

```markdown
## Tests

`deploy/tests/test-rollback.sh` vérifie la bascule et le retour arrière avec
```

with:

````markdown
## Rosters Discord (plugin Vencord, lot 14)

Le plugin Vencord envoie les membres d'un serveur Discord à une seule route publique,
`/ingest/discord/`, que nginx relaie vers `/api/ingest/discord/`. Seules les clés
`discord:ingest` y sont acceptées, et ces clés ne le sont nulle part ailleurs. C'est la
première route de l'API ouverte sur Internet : n'ajouter la location sur le serveur
qu'une fois le lot A livré **en entier** (clés limitées et leur schéma
d'authentification, verrou, garde-fou et limites de l'ingestion, effacements).

1. **Déployer** avec `--collector` :
   `~/sc-tracker/current/deploy/deploy.sh <sha> --collector`. La migration
   `AddDiscordRosters` crée les tables `discord_*` et trois index sur des tables
   existantes (`entity_links`, `users`, `user_handle_history`) : chronométrer d'abord
   `Collector.dll --migrate` sur une copie de la base. La colonne `Scope` d'`api.db`
   (migration `AddApiKeyScope`) s'ajoute au démarrage de l'API.
2. **Empreinte du certificat** :

   ```bash
   openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256
   ```

   La commande affiche `sha256 Fingerprint=AB:CD:…`. Garder la partie après
   `Fingerprint=` : une valeur avec une espace casserait un `set -a; . api.env`. Le plugin
   accepte aussi la ligne entière.
3. **Réglages de l'API**, dans `/etc/sc-tracker/api.env` :

   ```bash
   COLLECTOR_API_Discord__Ingest__PublicUrl=https://<IP>
   COLLECTOR_API_Discord__Ingest__CertificateSha256=AB:CD:…
   ```

   puis `sudo systemctl restart sc-api`. Le panneau Paramètres → Clé d'envoi Discord
   affiche alors l'URL et l'empreinte, chacune avec un bouton copier ; tant que l'une
   manque, il renvoie vers l'administrateur. Réglages facultatifs, commentés dans
   `deploy/env/api.env.example` : `COLLECTOR_API_Api__RateLimit__DiscordIngest__*` (20
   envois par 600 s et par émetteur) et `COLLECTOR_API_Discord__Retention__*` (journal des
   envois 365 jours, comptes non liés partis 730 jours).
4. **nginx**, à la main comme au lot 2 : la version du dépôt ajoute les zones
   `sc_discord_ingest` et `sc_discord_conn`, et la location `/ingest/discord/` avant
   `location /`.

   ```bash
   sudo cp ~/sc-tracker/current/deploy/nginx/sc-tracker.conf /etc/nginx/sites-available/sc-tracker
   sudo nginx -t && sudo systemctl reload nginx
   ```

   Retour : l'ancienne version est dans l'historique git
   (`git show <sha>:deploy/nginx/sc-tracker.conf`), même copie, puis `nginx -t` et `reload`.
5. **Vérification** depuis le poste local :

   ```bash
   U=https://<IP>/ingest/discord/guilds/123456789012345678/syncs
   curl -sk -o /dev/null -w '%{http_code}\n' -X POST "$U"   # 401 : aucune clé
   curl -sk -o /dev/null -w '%{http_code}\n' "$U"           # 403 : POST seulement
   ```

**Certificat régénéré** : recalculer l'empreinte (étape 2), remplacer
`COLLECTOR_API_Discord__Ingest__CertificateSha256` dans `api.env`, puis
`sudo systemctl restart sc-api`. Tant qu'un émetteur n'a pas recopié la nouvelle empreinte
depuis le panneau dans son plugin, ses envois échouent sans rien transmettre
(« Certificat inattendu »). C'est voulu.

**Effacements avant les pages du lot C** : un admin appelle les routes depuis le VPS, en
boucle locale, avec la clé admin statique (réponse attendue : 204) :

```bash
KEY=$(sudo sed -n 's/^COLLECTOR_API_Api__AdminApiKey=//p' /etc/sc-tracker/api.env)
A=http://127.0.0.1:5000/api/discord
curl -s -o /dev/null -w '%{http_code}\n' -X DELETE -H "x-api-key: $KEY" "$A/accounts/<id du compte>"                # effacer et exclure un compte
curl -s -o /dev/null -w '%{http_code}\n' -X DELETE -H "x-api-key: $KEY" "$A/guilds/<id du serveur>?exclude=true"     # supprimer et exclure un serveur
curl -s -o /dev/null -w '%{http_code}\n' -X POST -H "x-api-key: $KEY" "$A/guilds/<id du serveur>/allow-mass-departure" # lever le garde-fou une fois
```

## Tests

`deploy/tests/test-rollback.sh` vérifie la bascule et le retour arrière avec
````

- [ ] **Step 6: Update the root `README.md`**

In the Architecture diagram, replace:

```
Internet ──HTTPS──> nginx ──> Collector.Web (Next.js, 127.0.0.1:3000)
                                   │  appels serveur avec le JWT de l'utilisateur
                                   v
                              Collector.Api (ASP.NET Core, 127.0.0.1:5000)
```

with:

```
Internet ──HTTPS──> nginx ──> Collector.Web (Next.js, 127.0.0.1:3000)
                      │            │  appels serveur avec le JWT de l'utilisateur
   /ingest/discord/   │            v
   (plugin Vencord)   └─────> Collector.Api (ASP.NET Core, 127.0.0.1:5000)
```

Then replace:

```markdown
Trois services systemd (`sc-web`, `sc-api`, `sc-collector`) sur un VPS, derrière nginx.
```

with:

```markdown
Trois services systemd (`sc-web`, `sc-api`, `sc-collector`) sur un VPS, derrière nginx.
nginx sert le front et relaie vers l'API une seule route publique, `/ingest/discord/`,
par laquelle le plugin Vencord envoie les membres des serveurs Discord suivis (voir
Sécurité).
```

Then replace:

```markdown
- `tracker.db` (≈ 25 Go) : organisations, membres, citoyens, événements, file
  d'enrichissement. Écrite par le collector, qui applique ses migrations EF au démarrage ;
  l'API y lit et y écrit les annotations (notes, adhésions manuelles, audio, liens).
```

with:

```markdown
- `tracker.db` (≈ 25 Go) : organisations, membres, citoyens, événements, file
  d'enrichissement. Écrite par le collector, qui applique ses migrations EF au démarrage ;
  l'API y lit et y écrit les annotations (notes, adhésions manuelles, audio, liens) et,
  seule, les rosters Discord (tables `discord_*`).
```

In "Sécurité", replace:

```markdown
- API en HTTP sur la boucle locale uniquement ; nginx termine TLS et écrase
  `X-Forwarded-For`. Rate limit par utilisateur, par IP et sur le login.
```

with:

```markdown
- API en HTTP sur la boucle locale uniquement ; nginx termine TLS et écrase
  `X-Forwarded-For`. Rate limit par utilisateur, par IP et sur le login.
- Une seule route publique mène à l'API : `/ingest/discord/`, que nginx relaie vers
  `/api/ingest/discord/` (POST seulement, 10 requêtes par minute et 2 connexions par
  adresse, 25 Mo au plus). Le plugin Vencord y envoie les membres d'un serveur Discord
  avec une clé `discord:ingest`, que chaque utilisateur crée dans Paramètres et qui
  expire au plus tard 365 jours après sa création. La route n'accepte que ces clés (ni
  JWT, ni clé complète, ni clé admin), et ces clés ne sont acceptées nulle part ailleurs
  (401).
```

In "Variables d'environnement", replace:

```markdown
| API | `COLLECTOR_API_Api__RateLimit__*` | limites par utilisateur, par IP et du login |
| API | `Discord__BotToken` | intégration Discord |
```

with:

```markdown
| API | `COLLECTOR_API_Api__RateLimit__*` | limites par utilisateur, par IP, du login et des envois Discord (`DiscordIngest__PermitLimit`, `DiscordIngest__WindowSeconds` : 20 envois par 600 s) |
| API | `Discord__BotToken` | intégration Discord |
| API | `COLLECTOR_API_Discord__Ingest__PublicUrl` | URL publique du tracker (`https://<IP>`) à saisir dans le plugin Vencord, affichée dans Paramètres → Clé d'envoi Discord |
| API | `COLLECTOR_API_Discord__Ingest__CertificateSha256` | empreinte SHA-256 du certificat de nginx, affichée au même endroit (calcul dans `deploy/README.md`) |
| API | `COLLECTOR_API_Discord__Retention__*` | conservation Discord en jours : `SyncLogDays` (journal des envois, 365), `DepartedAccountDays` (comptes non liés partis de tous les serveurs, 730) |
```

- [ ] **Step 7: Update `src/Collector.Web/README.md`**

Replace:

```markdown
| `/settings` | Mot de passe, jeton Discord |
```

with:

```markdown
| `/settings` | Mot de passe, jeton Discord, clé d'envoi Discord |
```

- [ ] **Step 8: Update the comments in `src/Collector.Api/Program.cs`**

Replace:

```csharp
// The only proxy in front of the API is the web front on the same host: trust the
// client IP it forwards (X-Forwarded-For) from loopback only, one hop deep, so a
// remote client can never pick the IP it is rate-limited and logged under.
```

with:

```csharp
// Both proxies in front of the API run on the same host: the web front, and nginx for
// the single public ingest route (/ingest/discord/), which overwrites X-Forwarded-For.
// Trust the client IP they forward from loopback only, one hop deep, so a remote client
// can never pick the IP it is rate-limited and logged under.
```

Then replace:

```csharp
// No HTTPS redirection / HSTS: the API only listens on 127.0.0.1 and is reached by the
// web front over plain HTTP; browsers never talk to it (TLS terminates at nginx).
```

with:

```csharp
// No HTTPS redirection / HSTS: the API only listens on 127.0.0.1 (TLS terminates at
// nginx). Browsers never talk to it: the web front calls it over plain HTTP, and nginx
// relays one public route, /ingest/discord/ to /api/ingest/discord/, where only
// discord:ingest keys are accepted (uploads from the Vencord plugin).
```

- [ ] **Step 9: Run the check to verify it passes**

Run from the repository root:

```bash
check() { if grep -qF -- "$2" "$1"; then echo "ok      $1: $2"; else echo "MISSING $1: $2"; fi; }
check deploy/nginx/sc-tracker.conf 'limit_req_zone  $binary_remote_addr zone=sc_discord_ingest:1m rate=10r/m;'
check deploy/nginx/sc-tracker.conf 'limit_conn_zone $binary_remote_addr zone=sc_discord_conn:1m;'
check deploy/nginx/sc-tracker.conf 'limit_except POST { deny all; }'
check deploy/nginx/sc-tracker.conf 'limit_req zone=sc_discord_ingest burst=10 nodelay;'
check deploy/nginx/sc-tracker.conf 'limit_conn sc_discord_conn 2;'
check deploy/nginx/sc-tracker.conf 'client_max_body_size 25m;'
check deploy/nginx/sc-tracker.conf 'proxy_pass http://127.0.0.1:5000/api/ingest/discord/;'
awk '/zone=sc_discord_ingest:1m/ { z = NR } /^server \{/ && !s { s = NR }
     /location \/ingest\/discord\/ \{/ { d = NR } /location \/ \{/ { r = NR }
     END { print ((z && z < s) ? "ok      zones at http level" : "MISSING zones at http level");
           print ((d && d < r) ? "ok      ingest location before location /" : "MISSING ingest location before location /") }' \
  deploy/nginx/sc-tracker.conf
check deploy/env/api.env.example 'COLLECTOR_API_Discord__Ingest__PublicUrl='
check deploy/env/api.env.example 'COLLECTOR_API_Discord__Ingest__CertificateSha256='
check deploy/env/api.env.example '#COLLECTOR_API_Api__RateLimit__DiscordIngest__PermitLimit=20'
check deploy/env/api.env.example '#COLLECTOR_API_Discord__Retention__DepartedAccountDays=730'
check deploy/README.md '## Rosters Discord (plugin Vencord, lot 14)'
check deploy/README.md 'openssl x509 -in /etc/ssl/certs/sc-selfsigned.crt -noout -fingerprint -sha256'
check deploy/README.md 'COLLECTOR_API_Discord__Ingest__CertificateSha256=AB:CD:…'
check deploy/README.md '**Certificat régénéré**'
check README.md '| API | `COLLECTOR_API_Discord__Ingest__PublicUrl` |'
check README.md "Une seule route publique mène à l'API"
check README.md 'seule, les rosters Discord (tables `discord_*`)'
check src/Collector.Web/README.md "| \`/settings\` | Mot de passe, jeton Discord, clé d'envoi Discord |"
check src/Collector.Api/Program.cs 'relays one public route, /ingest/discord/ to /api/ingest/discord/'
```

Expected: 22 lines, every one starting with `ok`.

Run: `git diff --stat`

Expected: exactly these 6 files, nothing else:

```
 README.md                    | …
 deploy/README.md             | …
 deploy/env/api.env.example   | …
 deploy/nginx/sc-tracker.conf | …
 src/Collector.Api/Program.cs | …
 src/Collector.Web/README.md  | …
 6 files changed, … insertions(+), … deletions(-)
```

Run: `dotnet test src/Collector.Api.Tests --filter "FullyQualifiedName~DiscordIngestConfigTests"`

Expected: `Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3` (Program.cs still compiles after the comment edits).

nginx itself is not installed locally: the syntax of the new file is checked by `sudo nginx -t` on the server, at step 4 of the new `deploy/README.md` section, before any `reload`.

- [ ] **Step 10: Commit**

```bash
git add deploy/nginx/sc-tracker.conf deploy/env/api.env.example deploy/README.md \
  README.md src/Collector.Web/README.md src/Collector.Api/Program.cs
git commit -F - <<'EOF'
feat(deploy): nginx relays /ingest/discord/ to the API for Vencord uploads

The Vencord plugin posts Discord rosters from the senders' PCs, so the API gets
its first route reachable from the Internet. nginx relays only that path, POST
only, 10 requests a minute and 2 connections per address, 25 MB at most, with
request buffering on so a slow client never holds the ingest lock; the API
accepts nothing but discord:ingest keys there.

The location is applied by hand, like every nginx change, and only once lot A is
complete. deploy/README.md gives the steps: deploy with --collector for the
tracker.db migration, the openssl fingerprint, the two Discord__Ingest__ settings
shown in the settings panel, nginx -t and reload, certificate regeneration, and
erasures from the VPS until the lot C pages exist. The READMEs, api.env.example
and the Program.cs comments no longer say the API is reached through the web
front alone.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
EOF
```
