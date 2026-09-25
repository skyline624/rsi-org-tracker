using System.Security.Cryptography;
using Collector.Api.Auth;
using FluentAssertions;
using Xunit;

namespace Collector.Api.Tests;

public sealed class JwtKeyProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jwt-key-tests").FullName;

    private string WriteKey(int bits, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        var path = Path.Combine(_dir, $"key-{bits}-{Guid.NewGuid():N}.pem");
        using var rsa = RSA.Create(bits);
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
        return path;
    }

    [Fact]
    public void ValidKey_ExposesMatchingKeyIdOnSigningAndPublicKeys()
    {
        var provider = new JwtKeyProvider(WriteKey(2048));

        provider.PublicJwk.Kid.Should().NotBeNullOrWhiteSpace();
        provider.PublicKey.KeyId.Should().Be(provider.PublicJwk.Kid);
        provider.SigningCredentials.Key.KeyId.Should().Be(provider.PublicJwk.Kid);
        provider.SigningCredentials.Algorithm.Should().Be("RS256");
    }

    [Fact]
    public void KeyShorterThan2048Bits_IsRefused()
    {
        var act = () => new JwtKeyProvider(WriteKey(1024));

        act.Should().Throw<InvalidOperationException>().WithMessage("*2048*");
    }

    [Fact]
    public void MissingFile_IsRefused()
    {
        var act = () => new JwtKeyProvider(Path.Combine(_dir, "absent.pem"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public void KeyReadableByGroupOrOthers_IsRefused()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permissions only; runs in CI (Linux).

        var path = WriteKey(2048, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var act = () => new JwtKeyProvider(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*chmod 600*");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
