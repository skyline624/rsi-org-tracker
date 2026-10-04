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
