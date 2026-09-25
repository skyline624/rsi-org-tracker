using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Collector.Api.Options;
using Collector.Api.Services;
using Collector.Data.Repositories;
using Collector.Models;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class AudioTests(ApiFactory factory)
{
    private static readonly byte[] Mp3 = [.. "ID3"u8.ToArray(), 0x04, 0x00, .. new byte[500]];
    private static readonly byte[] Ogg = [.. "OggS"u8.ToArray(), .. new byte[500]];
    private static readonly byte[] M4a = [0, 0, 0, 0x20, .. "ftypM4A "u8.ToArray(), .. new byte[500]];
    private static readonly byte[] WebM = [0x1A, 0x45, 0xDF, 0xA3, .. new byte[500]];

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName, string contentType = "audio/mpeg")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private int AudioFileCount() =>
        Directory.Exists(Path.Combine(factory.DataDir, "audio"))
            ? Directory.GetFiles(Path.Combine(factory.DataDir, "audio"), "*", SearchOption.AllDirectories).Length
            : 0;

    [Theory]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.ogg", "audio/ogg")]
    [InlineData("a.m4a", "audio/mp4")]
    [InlineData("a.webm", "audio/webm")]
    public async Task RealRecordings_AreAccepted(string fileName, string contentType)
    {
        var client = await factory.SignedInClientAsync($"audio-ok-{Path.GetExtension(fileName)[1..]}");
        var bytes = Path.GetExtension(fileName) switch { ".mp3" => Mp3, ".ogg" => Ogg, ".m4a" => M4a, _ => WebM };

        var response = await client.PostAsync("/api/users/voice-ok/audio", Upload(bytes, fileName, contentType));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TextFileRenamedToMp3_IsRejected()
    {
        var client = await factory.SignedInClientAsync("audio-text");

        var response = await client.PostAsync("/api/users/voice-text/audio",
            Upload("<script>alert(1)</script> padding padding"u8.ToArray(), "evil.mp3"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ContentNotMatchingTheExtension_IsRejected()
    {
        var client = await factory.SignedInClientAsync("audio-mismatch");

        var response = await client.PostAsync("/api/users/voice-mismatch/audio", Upload(Mp3, "a.ogg", "audio/ogg"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadsBeyondTheUserQuota_AreRejected()
    {
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<AudioSettings>(o => o.MaxBytesPerUser = Mp3.Length + 100)));
        await factory.CreateAccountAsync("audio-quota", "correct horse battery");
        var login = await factory.LoginAsync("audio-quota", "correct horse battery");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        (await client.PostAsync("/api/users/voice-quota/audio", Upload(Mp3, "a.mp3"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/users/voice-quota/audio", Upload(Mp3, "b.mp3"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class FailingSaveRepository(IEntityAudioRepository inner) : IEntityAudioRepository
    {
        public Task<EntityAudio?> GetByIdAsync(long id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<EntityAudio>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
        public Task AddAsync(EntityAudio entity, CancellationToken ct = default) => inner.AddAsync(entity, ct);
        public Task AddRangeAsync(IEnumerable<EntityAudio> entities, CancellationToken ct = default) => inner.AddRangeAsync(entities, ct);
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => throw new IOException("disk full");
        public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default) => inner.BeginTransactionAsync(ct);
        public Task<IReadOnlyList<EntityAudio>> GetByEntityIdAsync(long entityId, CancellationToken ct = default) => inner.GetByEntityIdAsync(entityId, ct);
        public void Remove(EntityAudio audio) => inner.Remove(audio);
        public void ClearTrackedEntities() => inner.ClearTrackedEntities();
        public Task<long> GetTotalBytesByAuthorAsync(long id, CancellationToken ct = default) => inner.GetTotalBytesByAuthorAsync(id, ct);
        public Task<IReadOnlySet<string>> GetAllStoredPathsAsync(CancellationToken ct = default) => inner.GetAllStoredPathsAsync(ct);
    }

    [Fact]
    public async Task FailedDatabaseWrite_LeavesNoFileBehind()
    {
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<IEntityAudioRepository>(sp => new FailingSaveRepository(ActivatorUtilities.CreateInstance<EntityAudioRepository>(sp)))));
        await factory.CreateAccountAsync("audio-dbfail", "correct horse battery");
        var login = await factory.LoginAsync("audio-dbfail", "correct horse battery");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var before = AudioFileCount();

        var response = await client.PostAsync("/api/users/voice-dbfail/audio", Upload(Mp3, "a.mp3"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        AudioFileCount().Should().Be(before);
    }

    [Fact]
    public async Task Playback_ForbidsContentSniffing()
    {
        var client = await factory.SignedInClientAsync("audio-play");
        var upload = await client.PostAsync("/api/users/voice-play/audio", Upload(Mp3, "a.mp3"));
        var id = (await upload.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["id"].ToString();

        var response = await client.GetAsync($"/api/audio/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
    }

    [Fact]
    public void OrphanSweeper_DeletesOldFilesWithoutARow_Only()
    {
        var dir = Directory.CreateTempSubdirectory("audio-sweep").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "7"));
        string Make(string rel, DateTime writtenUtc)
        {
            var full = Path.Combine(dir, rel);
            File.WriteAllBytes(full, Mp3);
            File.SetLastWriteTimeUtc(full, writtenUtc);
            return full;
        }
        var now = DateTime.UtcNow;
        var kept = Make("7/known.mp3", now.AddDays(-3));
        var orphan = Make("7/orphan.mp3", now.AddDays(-3));
        var fresh = Make("7/uploading.mp3", now);

        var deleted = AudioOrphanSweeper.Sweep(dir, new HashSet<string> { "7/known.mp3" }, now.AddHours(-1));

        deleted.Should().Be(1);
        File.Exists(kept).Should().BeTrue();
        File.Exists(orphan).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue("a file may be written just before its row");
    }
}
