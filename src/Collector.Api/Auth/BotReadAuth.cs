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
