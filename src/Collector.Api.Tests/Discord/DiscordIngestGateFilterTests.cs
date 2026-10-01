using Collector.Api.Errors;
using Collector.Api.Services.Discord;
using Collector.Data.Repositories;
using Collector.Discord;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>
/// The resource filter in front of the ingest action (spec § 9.1 step 1), driven with a
/// hand-built MVC context. It checks what is refused before the gate is taken, and that the
/// gate is always released after the action.
/// </summary>
public class DiscordIngestGateFilterTests
{
    private const string GuildId = "123456789012345678";

    [Fact]
    public async Task WaitingForTheGate_DoesNotAdvanceTheCollectionArrivalTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var arrival = time.GetUtcNow().UtcDateTime;
        var gate = new DiscordWriteGate();
        using var holder = await gate.EnterAsync(CancellationToken.None);
        var roster = new StubRoster(false);
        var filter = new DiscordIngestGateFilter(gate, roster, time);
        var (context, action) = ContextFor(GuildId);
        var run = filter.OnResourceExecutionAsync(context, () =>
        {
            context.HttpContext.Items[DiscordIngestGateFilter.ReceivedAtItemKey].Should().Be(arrival);
            return Task.FromResult(new ResourceExecutedContext(action, new List<IFilterMetadata>()));
        });
        roster.ExclusionChecks.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(5));
        holder.Dispose();
        await run;
    }

    private static (ResourceExecutingContext Executing, ActionContext Action) ContextFor(string guildId)
    {
        var routeData = new RouteData();
        routeData.Values["guildId"] = guildId;
        var action = new ActionContext(new DefaultHttpContext(), routeData, new ActionDescriptor());
        return (new ResourceExecutingContext(action, new List<IFilterMetadata>(), new List<IValueProviderFactory>()), action);
    }

    private static async Task<bool> IsFreeAsync(DiscordWriteGate gate)
    {
        using var lease = await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None);
        return lease is not null;
    }

    [Fact]
    public async Task InvalidGuildId_Is400InvalidSync_AndNothingElseRuns()
    {
        var gate = new DiscordWriteGate();
        var roster = new StubRoster(false);
        var filter = new DiscordIngestGateFilter(gate, roster);
        var (context, _) = ContextFor("not-a-snowflake");
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        (await act.Should().ThrowAsync<ValidationException>()).Which.Code.Should().Be("invalid_sync");
        actionRan.Should().BeFalse();
        roster.ExclusionChecks.Should().Be(0);
        (await IsFreeAsync(gate)).Should().BeTrue("the gate was never taken");
    }

    [Fact]
    public async Task ExcludedGuild_Is409GuildExcluded_WithoutTakingTheGate()
    {
        var gate = new DiscordWriteGate();
        var filter = new DiscordIngestGateFilter(gate, new StubRoster(true));
        var (context, _) = ContextFor(GuildId);
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        var thrown = (await act.Should().ThrowAsync<ConflictException>()).Which;
        thrown.Code.Should().Be("guild_excluded");
        thrown.StatusCode.Should().Be(409);
        actionRan.Should().BeFalse();
        (await IsFreeAsync(gate)).Should().BeTrue();
    }

    [Fact]
    public async Task GateStillHeldAfterTheWait_Is503Busy()
    {
        var gate = new DiscordWriteGate();
        using var holder = await gate.EnterAsync(CancellationToken.None);
        var roster = new StubRoster(false);
        var filter = new DiscordIngestGateFilter(gate, roster) { WaitTimeout = TimeSpan.Zero };
        var (context, _) = ContextFor(GuildId);
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        var thrown = (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which;
        thrown.StatusCode.Should().Be(503);
        thrown.Code.Should().Be("busy");
        actionRan.Should().BeFalse();
        roster.ExclusionChecks.Should().Be(1);
    }

    [Fact]
    public async Task HappyPath_HoldsTheGateDuringTheAction_AndReleasesItAfter()
    {
        var gate = new DiscordWriteGate();
        var filter = new DiscordIngestGateFilter(gate, new StubRoster(false));
        var (context, action) = ContextFor(GuildId);
        object? leaseSeen = null;
        var gateFreeDuringAction = true;

        await filter.OnResourceExecutionAsync(context, async () =>
        {
            context.HttpContext.Items.TryGetValue(DiscordIngestGateFilter.LeaseItemKey, out leaseSeen);
            gateFreeDuringAction = await IsFreeAsync(gate);
            return new ResourceExecutedContext(action, new List<IFilterMetadata>());
        });

        leaseSeen.Should().BeAssignableTo<IDisposable>();
        gateFreeDuringAction.Should().BeFalse("the gate is held while the action runs");
        context.HttpContext.Items.ContainsKey(DiscordIngestGateFilter.LeaseItemKey).Should().BeFalse();
        (await IsFreeAsync(gate)).Should().BeTrue("the lease was released after the action");
    }

    [Fact]
    public async Task ActionThatThrows_StillReleasesTheGate()
    {
        var gate = new DiscordWriteGate();
        var filter = new DiscordIngestGateFilter(gate, new StubRoster(false));
        var (context, _) = ContextFor(GuildId);

        var act = () => filter.OnResourceExecutionAsync(context, () => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await IsFreeAsync(gate)).Should().BeTrue();
    }

    [Fact]
    public async Task GuildExcludedWhileWaitingForTheGate_Is409_AndReleasesTheGate()
    {
        var gate = new DiscordWriteGate();
        var roster = new StubRoster(false, true);
        var filter = new DiscordIngestGateFilter(gate, roster);
        var (context, _) = ContextFor(GuildId);
        var actionRan = false;

        var act = () => filter.OnResourceExecutionAsync(context, () =>
        {
            actionRan = true;
            return Task.FromResult<ResourceExecutedContext>(null!);
        });

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("guild_excluded");
        actionRan.Should().BeFalse();
        roster.ExclusionChecks.Should().Be(2, "the exclusion is checked again once the gate is held");
        (await IsFreeAsync(gate)).Should().BeTrue();
    }

    /// <summary>Answers the exclusion checks in order (the last answer repeats); nothing else is used.</summary>
    private sealed class StubRoster(params bool[] excludedAnswers) : IDiscordRosterRepository
    {
        public int ExclusionChecks { get; private set; }

        public Task<bool> IsGuildExcludedAsync(string guildId, CancellationToken ct = default)
        {
            var answer = excludedAnswers[Math.Min(ExclusionChecks, excludedAnswers.Length - 1)];
            ExclusionChecks++;
            return Task.FromResult(answer);
        }

        public Task<DateTime?> GetLastSyncReceivedAtAsync(string guildId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RosterSnapshot> LoadSnapshotAsync(
            string guildId, IReadOnlyCollection<string> payloadUserIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> GetOptedOutAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DiscordSyncResult> ApplyAsync(DiscordSyncWrite write, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

/// <summary>The gate is one instance for the whole API, and MVC can build the filter from DI.</summary>
[Collection(ApiCollection.Name)]
public class DiscordIngestGateRegistrationTests(ApiFactory factory)
{
    [Fact]
    public void TheApi_SharesOneWriteGate_AndCanBuildTheIngestFilter()
    {
        var gate = factory.Services.GetRequiredService<DiscordWriteGate>();
        factory.Services.GetRequiredService<DiscordWriteGate>().Should().BeSameAs(gate);

        using var scope = factory.Services.CreateScope();
        var filter = ActivatorUtilities.CreateInstance<DiscordIngestGateFilter>(scope.ServiceProvider);

        filter.WaitTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }
}
