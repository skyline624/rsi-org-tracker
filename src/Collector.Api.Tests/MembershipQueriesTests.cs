using System.Collections.Concurrent;
using System.Data.Common;
using Collector.Api.Auth;
using Collector.Api.Controllers;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Models;
using Collector.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Collector.Api.Tests;

/// <summary>Manual memberships load their people and org names in grouped queries, not one per row.</summary>
public sealed class MembershipQueriesTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlCapture _sql = new();
    private TrackerDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection.Open();
        _db = new TrackerDbContext(new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite(_connection).AddInterceptors(_sql).Options);
        await _db.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        _db.Organizations.Add(new Organization
        {
            Sid = "NPLUS", Name = "N plus one", Timestamp = now, ContentCollected = true, Description = "A long text",
        });
        for (var i = 0; i < 5; i++)
        {
            var entity = new TrackedEntity { CurrentHandle = $"member{i}", CreatedAt = now, UpdatedAt = now };
            _db.TrackedEntities.Add(entity);
            await _db.SaveChangesAsync();
            _db.EntityMemberships.Add(new EntityMembership
            {
                TrackedEntityId = entity.Id, OrgSid = "NPLUS", AuthorUsername = "someone", SinceDate = now, CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private MembershipsController Controller() => new(
        new UnusedResolver(),
        new TrackedEntityRepository(_db),
        new EntityMembershipRepository(_db),
        new UserRepository(_db),
        new OrganizationRepository(_db),
        new CurrentUserAccessor(new HttpContextAccessor()));

    [Fact]
    public async Task ManualMembersOfAnOrg_TakeTwoQueries_NotOnePerMember()
    {
        _sql.Commands.Clear();

        var result = await Controller().GetManualOrgMembers("NPLUS", CancellationToken.None);

        ((result.Result as OkObjectResult)!.Value as IEnumerable<object>).Should().HaveCount(5);
        _sql.Commands.Should().HaveCountLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task OrgNamesOfAPersonsMemberships_DoNotReadPageTexts()
    {
        var entityId = await _db.TrackedEntities.Where(e => e.CurrentHandle == "member0").Select(e => e.Id).SingleAsync();
        var repo = new OrganizationRepository(_db);
        _sql.Commands.Clear();

        var names = await repo.GetLatestNamesBySidsAsync(["NPLUS"]);

        names.Should().Equal(new Dictionary<string, string> { ["NPLUS"] = "N plus one" });
        _sql.Commands.Should().ContainSingle().Which.Should().NotContain("Description");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _connection.Dispose();
    }

    /// <summary>These endpoints never create people.</summary>
    private sealed class UnusedResolver : IEntityResolver
    {
        public Task<long> ResolveOrCreateAsync(int? citizenId, string? handle, string? displayName = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
