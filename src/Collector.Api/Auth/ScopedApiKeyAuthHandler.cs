using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Collector.Api.Auth;

/// <summary>
/// Accepts only live API keys carrying exactly one capability scope, without inheriting their
/// owner's other permissions. The single place where a scoped-key security gate is implemented.
/// </summary>
public abstract class ScopedApiKeyAuthHandler : AuthenticationHandler<ApiKeySchemeOptions>
{
    private readonly ApiKeyService _apiKeyService;

    protected ScopedApiKeyAuthHandler(
        IOptionsMonitor<ApiKeySchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService;
    }

    /// <summary>The only key scope this scheme accepts.</summary>
    protected abstract string RequiredScope { get; }

    /// <summary>The claim type under which the scope is exposed to authorization policies.</summary>
    protected abstract string ScopeClaimType { get; }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The static admin key and bearer tokens have no standing in a scoped scheme.
        var rawKey = Request.Headers["x-api-key"].ToString();
        if (string.IsNullOrWhiteSpace(rawKey))
            return AuthenticateResult.NoResult();

        var validation = await _apiKeyService.ValidateAsync(rawKey, Context.RequestAborted);
        if (validation is null)
            return AuthenticateResult.Fail("Invalid API key");
        if (validation.Scope != RequiredScope)
            return AuthenticateResult.Fail($"Not a {RequiredScope} key");
        if (validation.User.IsBanned)
            return AuthenticateResult.Fail("Account is banned");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, validation.User.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, validation.User.Username),
            new Claim(ScopeClaimType, RequiredScope),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
