using System.Net.Http.Json;
using System.Text.Json;
using Collector.Data;
using Collector.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Collector.Api.Tests;

[Collection(ApiCollection.Name)]
public class HealthCycleTests(ApiFactory factory)
{
    [Fact]
    public async Task QueueFigures_FollowTheQueueOutcomes()
    {
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
            db.UserEnrichmentQueue.AddRange(
                new UserEnrichmentQueue { UserHandle = "hc-due", QueuedAt = now.AddHours(-1) },
                new UserEnrichmentQueue
                {
                    UserHandle = "hc-na", QueuedAt = now.AddHours(-1),
                    Outcome = EnrichmentOutcome.NoCitizenRecord, NextAttemptAt = now.AddDays(10),
                },
                new UserEnrichmentQueue
                {
                    UserHandle = "hc-abandoned-today", QueuedAt = now.AddDays(-1), AttemptCount = 3,
                    Enriched = true, EnrichedAt = now.AddHours(-2), Outcome = EnrichmentOutcome.Abandoned,
                },
                new UserEnrichmentQueue
                {
                    UserHandle = "hc-abandoned-long-ago", QueuedAt = now.AddDays(-9), AttemptCount = 3,
                    Enriched = true, EnrichedAt = now.AddDays(-8), Outcome = EnrichmentOutcome.Abandoned,
                });
            await db.SaveChangesAsync();
        }
        var client = await factory.SignedInClientAsync("health-cycle");

        var body = await client.GetFromJsonAsync<JsonElement>("/api/health/cycle");

        body.GetProperty("queue_pending").GetInt32().Should().Be(1, "rows waiting for their next attempt are not due");
        body.GetProperty("queue_stuck").GetInt32().Should().Be(1, "rows abandoned in the last 24 hours");
    }
}
