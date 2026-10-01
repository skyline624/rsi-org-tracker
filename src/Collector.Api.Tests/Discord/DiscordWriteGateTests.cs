using Collector.Api.Services.Discord;
using FluentAssertions;
using Xunit;

namespace Collector.Api.Tests.Discord;

/// <summary>Spec § 9.2: one writer at a time on the discord_* tables, and a lease that always frees the gate.</summary>
public class DiscordWriteGateTests
{
    [Fact]
    public async Task OneHolderAtATime()
    {
        var gate = new DiscordWriteGate();

        using var first = await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None);

        first.Should().NotBeNull();
        (await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeNull("the gate is held");
    }

    [Fact]
    public async Task TimeoutWhileHeld_ReturnsNull()
    {
        var gate = new DiscordWriteGate();
        using var held = await gate.EnterAsync(CancellationToken.None);

        var lease = await gate.TryEnterAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        lease.Should().BeNull();
    }

    [Fact]
    public async Task ReleasingTheLease_LetsTheNextWriterIn()
    {
        var gate = new DiscordWriteGate();
        var first = await gate.EnterAsync(CancellationToken.None);
        var second = gate.EnterAsync(CancellationToken.None);
        second.IsCompleted.Should().BeFalse("the first lease is still held");

        first.Dispose();

        using var lease = await second;
        lease.Should().NotBeNull();
    }

    [Fact]
    public async Task DisposingALeaseTwice_ReleasesOnce()
    {
        var gate = new DiscordWriteGate();
        var lease = await gate.EnterAsync(CancellationToken.None);

        lease.Dispose();
        lease.Dispose();

        using var again = await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None);
        again.Should().NotBeNull();
        (await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None))
            .Should().BeNull("a double dispose must not let two writers in");
    }

    [Fact]
    public async Task WaitingForTheGate_CanBeCancelled()
    {
        var gate = new DiscordWriteGate();
        using var held = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cts.Token);

        cts.Cancel();

        Func<Task> act = () => waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
