using Collector.Api.Services;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests.Bot;

/// <summary>How a handle typed in Discord is matched to the one the tracker stores (spec § 5.2).</summary>
[Collection(ApiCollection.Name)]
public class UserLookupServiceTests(ApiFactory factory)
{
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    private async Task<string?> ResolveAsync(string input, Action<TrackerDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
        seed(db);
        await db.SaveChangesAsync();
        return await scope.ServiceProvider.GetRequiredService<UserLookupService>().ResolveHandleAsync(input, default);
    }

    [Fact]
    public async Task ACitizenHandle_IsFound_WhateverItsCase()
    {
        var handle = Unique("Pilot");
        // Users row only (no roster row): proves the case-insensitive match on `users` by itself.
        var resolved = await ResolveAsync(handle.ToLowerInvariant(), db =>
            db.Users.Add(new User { CitizenId = Random.Shared.Next(1, int.MaxValue), UserHandle = handle, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }));

        resolved.Should().Be(handle);
    }

    [Fact]
    public async Task ARosterOnlyHandle_IsFound_WhateverItsCase()
    {
        var handle = Unique("Roster");
        var resolved = await ResolveAsync(handle.ToUpperInvariant(), db =>
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "LOOKUP", UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true }));

        resolved.Should().Be(handle);
    }

    [Fact]
    public async Task AFormerHandle_LeadsToTheCurrentOne()
    {
        var current = Unique("Now");
        var former = Unique("Was");
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        var resolved = await ResolveAsync(former, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = current, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = former, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-1) });
        });

        resolved.Should().Be(current);
    }

    [Fact]
    public async Task AFormerHandle_LeadsToTheCurrentOne_EvenWhenItIsStillInARoster()
    {
        var current = Unique("Now");
        var former = Unique("Was");
        var citizenId = Random.Shared.Next(1, int.MaxValue);
        var resolved = await ResolveAsync(former, db =>
        {
            db.Users.Add(new User { CitizenId = citizenId, UserHandle = current, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = citizenId, UserHandle = former, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-1) });
            // A renamed citizen's old handle normally stays in the roster snapshots.
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "LOOKUP", UserHandle = former, Timestamp = DateTime.UtcNow, IsActive = true });
        });

        resolved.Should().Be(current);
    }

    [Fact]
    public async Task AFormerHandleOfACitizenWithoutProfile_FallsBackToTheRoster()
    {
        var handle = Unique("Ghost");
        var resolved = await ResolveAsync(handle.ToLowerInvariant(), db =>
        {
            // History knows the citizen, but `users` has no row for them.
            db.UserHandleHistories.Add(new UserHandleHistory { CitizenId = Random.Shared.Next(1, int.MaxValue), UserHandle = handle, FirstSeen = DateTime.UtcNow.AddYears(-1), LastSeen = DateTime.UtcNow.AddMonths(-1) });
            db.OrganizationMembers.Add(new OrganizationMember { OrgSid = "LOOKUP", UserHandle = handle, Timestamp = DateTime.UtcNow, IsActive = true });
        });

        resolved.Should().Be(handle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NobodyByThatName0000")]
    public async Task AnUnknownHandle_IsNull(string input)
    {
        (await ResolveAsync(input, _ => { })).Should().BeNull();
    }
}
