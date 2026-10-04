using System.Text.Encodings.Web;
using Collector.Api.Services;
using Microsoft.Extensions.Options;

namespace Collector.Api.Auth;

/// <summary>Accepts only live bot:read keys, without their owner's other permissions.</summary>
public sealed class BotReadKeyAuthHandler : ScopedApiKeyAuthHandler
{
    public BotReadKeyAuthHandler(
        IOptionsMonitor<ApiKeySchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService)
        : base(options, logger, encoder, apiKeyService)
    {
    }

    protected override string RequiredScope => BotReadAuth.Scope;

    protected override string ScopeClaimType => BotReadAuth.ScopeClaimType;
}
