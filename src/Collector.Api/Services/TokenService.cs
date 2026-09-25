using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Collector.Api.Auth;
using Collector.Api.Models;

namespace Collector.Api.Services;

public class TokenService
{
    private readonly IConfiguration _configuration;
    private readonly JwtKeyProvider _keys;

    public TokenService(IConfiguration configuration, JwtKeyProvider keys)
    {
        _configuration = configuration;
        _keys = keys;
    }

    public (string token, DateTime expiresAt) GenerateAccessToken(ApiUser user)
    {
        var issuer = _configuration["Api:JwtIssuer"] ?? "sc-tracker-api";
        var audience = _configuration["Api:JwtAudience"] ?? "sc-tracker-clients";
        var minutes = _configuration.GetValue("Api:AccessTokenMinutes", 15);
        var expiresAt = DateTime.UtcNow.AddMinutes(minutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: _keys.SigningCredentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
