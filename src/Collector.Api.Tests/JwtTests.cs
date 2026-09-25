using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class JwtTests(ApiFactory factory)
{
    private const string Issuer = "sc-tracker-api";
    private const string Audience = "sc-tracker-clients";

    [Fact]
    public async Task Jwks_PublishesOnlyThePublicRs256Key()
    {
        var jwks = await GetJwksAsync();

        jwks.Keys.Should().ContainSingle();
        var key = jwks.Keys[0];
        key.Kty.Should().Be("RSA");
        key.Alg.Should().Be("RS256");
        key.Use.Should().Be("sig");
        key.Kid.Should().NotBeNullOrWhiteSpace();
        key.HasPrivateKey.Should().BeFalse();
    }

    [Fact]
    public async Task AccessToken_IsRs256AndValidatesAgainstTheJwks()
    {
        await factory.CreateAccountAsync("jwt-rs256", "correct horse battery");
        var login = await factory.LoginAsync("jwt-rs256", "correct horse battery");
        var key = (await GetJwksAsync()).Keys[0];

        var token = new JsonWebToken(login.AccessToken);
        token.Alg.Should().Be("RS256");
        token.Kid.Should().Be(key.Kid);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(login.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = key,
        });
        result.IsValid.Should().BeTrue(result.Exception?.Message);
    }

    [Fact]
    public async Task Rs256AccessToken_AuthenticatesRequests()
    {
        await factory.CreateAccountAsync("jwt-me", "correct horse battery");
        var login = await factory.LoginAsync("jwt-me", "correct horse battery");

        var response = await GetMeAsync(login.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Hs256TokenSignedWithTheLegacySecret_IsRejected()
    {
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim("sub", "1"), new Claim("unique_name", "admin")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.LegacyJwtSecret)),
                SecurityAlgorithms.HmacSha256),
        });

        var response = await GetMeAsync(forged);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnsignedToken_IsRejected()
    {
        static string B64(string s) => Base64UrlEncoder.Encode(s);
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var unsigned = $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}." +
                       $"{B64($"{{\"sub\":\"1\",\"iss\":\"{Issuer}\",\"aud\":\"{Audience}\",\"exp\":{exp}}}")}.";

        var response = await GetMeAsync(unsigned);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<JsonWebKeySet> GetJwksAsync()
    {
        var response = await factory.CreateClient().GetAsync("/api/auth/jwks");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return new JsonWebKeySet(await response.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> GetMeAsync(string bearer)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.GetAsync("/api/auth/me");
    }
}
