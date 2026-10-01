using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Collector.Api.Options;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// The settings panel shows what to enter in the Vencord plugin: the public URL and the
/// certificate fingerprint set in api.env (COLLECTOR_API_Discord__Ingest__*), or nothing
/// while the administrator has not set them, so the panel can send users to them.
/// </summary>
[Collection(ApiCollection.Name)]
public class DiscordIngestConfigTests(ApiFactory factory)
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task IngestConfig_ReturnsWhatApiEnvSets()
    {
        var client = await factory.SignedInClientAsync("ingest-config-reader");

        var config = await client.GetFromJsonAsync<JsonElement>("/api/discord/ingest-config");

        config.GetProperty("publicUrl").GetString().Should().Be(ApiFactory.DiscordIngestPublicUrl);
        config.GetProperty("certificateSha256").GetString().Should().Be(ApiFactory.DiscordIngestCertificateSha256);
    }

    [Fact]
    public async Task IngestConfig_IsNull_WhileTheAdministratorHasNotSetIt()
    {
        await factory.CreateAccountAsync("ingest-config-unset", Password);
        var login = await factory.LoginAsync("ingest-config-unset", Password);
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<DiscordOptions>(o =>
            {
                o.Ingest.PublicUrl = null;
                o.Ingest.CertificateSha256 = "   ";
            })));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var config = await client.GetFromJsonAsync<JsonElement>("/api/discord/ingest-config");

        config.GetProperty("publicUrl").ValueKind.Should().Be(JsonValueKind.Null);
        config.GetProperty("certificateSha256").ValueKind.Should().Be(JsonValueKind.Null, "a blank setting is not a fingerprint");
    }

    [Fact]
    public async Task IngestConfig_TrimsWhatTheAdministratorPasted()
    {
        await factory.CreateAccountAsync("ingest-config-padded", Password);
        var login = await factory.LoginAsync("ingest-config-padded", Password);
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<DiscordOptions>(o =>
            {
                o.Ingest.PublicUrl = "  https://198.51.100.7\n";
                o.Ingest.CertificateSha256 = " AB:CD:EF ";
            })));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var config = await client.GetFromJsonAsync<JsonElement>("/api/discord/ingest-config");

        config.GetProperty("publicUrl").GetString().Should().Be("https://198.51.100.7");
        config.GetProperty("certificateSha256").GetString().Should().Be("AB:CD:EF", "the plugin gets exactly what to paste");
    }
}
