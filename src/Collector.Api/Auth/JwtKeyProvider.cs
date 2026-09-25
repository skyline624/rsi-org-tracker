using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Collector.Api.Auth;

/// <summary>
/// RSA key pair that signs access tokens (RS256). The private key never leaves the API;
/// the web front verifies tokens with the public key published at <c>/api/auth/jwks</c>,
/// so a compromised front cannot mint tokens.
/// </summary>
public sealed class JwtKeyProvider
{
    public const string PrivateKeyPathSetting = "Api:Jwt:PrivateKeyPath";
    private const int MinimumKeySizeBits = 2048;

    private const UnixFileMode GroupOrOtherAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public JwtKeyProvider(string privateKeyPath)
    {
        if (!File.Exists(privateKeyPath))
            throw new InvalidOperationException($"JWT private key not found at '{privateKeyPath}'.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(privateKeyPath) & GroupOrOtherAccess) != 0)
            throw new InvalidOperationException(
                $"JWT private key '{privateKeyPath}' is accessible to group/others; chmod 600 it.");

        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        if (rsa.KeySize < MinimumKeySizeBits)
            throw new InvalidOperationException($"JWT signing key must be at least {MinimumKeySizeBits} bits.");

        var publicParameters = rsa.ExportParameters(includePrivateParameters: false);
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(publicParameters));
        var keyId = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());

        SigningCredentials = new SigningCredentials(
            new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        PublicKey = new RsaSecurityKey(publicParameters) { KeyId = keyId };
        PublicJwk = new PublicJwkDto("RSA", "sig", SecurityAlgorithms.RsaSha256, keyId, jwk.N, jwk.E);
    }

    public SigningCredentials SigningCredentials { get; }

    public RsaSecurityKey PublicKey { get; }

    /// <summary>Public key in JWK form (RFC 7517), kid = RFC 7638 thumbprint.</summary>
    public PublicJwkDto PublicJwk { get; }

    public static JwtKeyProvider FromConfiguration(IConfiguration configuration)
    {
        var path = configuration[PrivateKeyPathSetting];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"{PrivateKeyPathSetting} is not configured. Generate a key with " +
                "`openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out private.pem` " +
                "and set COLLECTOR_API_Api__Jwt__PrivateKeyPath.");
        return new JwtKeyProvider(path);
    }
}

public sealed record PublicJwkDto(string Kty, string Use, string Alg, string Kid, string N, string E);
