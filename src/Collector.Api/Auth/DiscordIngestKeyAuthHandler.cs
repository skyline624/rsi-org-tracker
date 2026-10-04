using System.Text.Encodings.Web;
using Collector.Api.Services;
using Microsoft.Extensions.Options;

namespace Collector.Api.Auth;

/// <summary>Accepts only live ingest-scoped keys, without inheriting their owner's other permissions.</summary>
public sealed class DiscordIngestKeyAuthHandler : ScopedApiKeyAuthHandler
{
    public DiscordIngestKeyAuthHandler(
        IOptionsMonitor<ApiKeySchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService)
        : base(options, logger, encoder, apiKeyService)
    {
    }

    protected override string RequiredScope => DiscordIngestAuth.IngestScope;

    protected override string ScopeClaimType => DiscordIngestAuth.ScopeClaimType;
}
