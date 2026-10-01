namespace Collector.Api.Models;

/// <summary>Known API-key scopes. Scoped credentials are refused by the general API-key scheme.</summary>
public static class ApiKeyScopes
{
    /// <summary>Allows Discord roster uploads only.</summary>
    public const string DiscordIngest = "discord:ingest";

    /// <summary>Caps the lifetime of a credential stored on a plugin user's computer.</summary>
    public const int MaxDiscordIngestLifetimeDays = 365;

    /// <summary>Null denotes a full key; named scopes are case-sensitive.</summary>
    public static bool IsValid(string? scope) => scope is null or DiscordIngest;
}
