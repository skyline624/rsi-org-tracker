using System.Security.Claims;
using System.Text.Encodings.Web;
using Collector.Api.Data;
using Collector.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Collector.Api.Auth;

public class ApiKeyAuthHandler : AuthenticationHandler<ApiKeySchemeOptions>
{
    private readonly ApiKeyService _apiKeyService;
    private readonly IConfiguration _configuration;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeySchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService;
        _configuration = configuration;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? rawKey = null;

        if (Request.Headers.TryGetValue("x-api-key", out var headerValue))
            rawKey = headerValue.ToString();
        else if (Request.Query.TryGetValue("api_key", out var queryValue))
            rawKey = queryValue.ToString();

        if (string.IsNullOrWhiteSpace(rawKey))
            return AuthenticateResult.NoResult();

        // Check admin key first
        var adminKey = _configuration["Api:AdminApiKey"];
        if (!string.IsNullOrWhiteSpace(adminKey) && rawKey == adminKey)
        {
            var adminClaims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "0"),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "Admin"),
            };
            var adminIdentity = new ClaimsIdentity(adminClaims, Scheme.Name);
            var adminPrincipal = new ClaimsPrincipal(adminIdentity);
            return AuthenticateResult.Success(new AuthenticationTicket(adminPrincipal, Scheme.Name));
        }

        var user = await _apiKeyService.ValidateAsync(rawKey);
        if (user is null)
            return AuthenticateResult.Fail("Invalid API key");

        if (user.IsBanned)
            return AuthenticateResult.Fail("Account is banned");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email),
        };
        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
