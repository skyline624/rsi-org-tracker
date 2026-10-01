using Collector.Api.Models;

namespace Collector.Api.Auth;

/// <summary>Dedicated authentication boundary for externally submitted Discord rosters.</summary>
public static class DiscordIngestAuth
{
    public const string SchemeName = "DiscordIngestKey";
    public const string PolicyName = "DiscordIngest";
    public const string ScopeClaimType = "scope";
    public const string IngestScope = ApiKeyScopes.DiscordIngest;
    public const string PathPrefix = "/api/ingest/discord";
}
