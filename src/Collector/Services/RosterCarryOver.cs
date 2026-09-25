using System.Collections.Concurrent;

namespace Collector.Services;

/// <summary>
/// Members a short roster read missed (see <see cref="RosterStatus.Short"/>), per
/// organization. A missing member is carried over once; missing again from the next
/// short read, it counts as a departure. Kept in memory: after a restart a member is at
/// worst carried over one more time.
/// </summary>
public sealed class RosterCarryOver
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _carried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Of the active members missing from a short read, those to carry over now: the
    /// others were already carried over by the previous read.
    /// </summary>
    public IReadOnlySet<string> ToCarry(string orgSid, IEnumerable<string> missing)
    {
        var already = _carried.TryGetValue(orgSid, out var previous) ? previous : [];
        return missing.Where(h => !already.Contains(h)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Called once the roster is written: the members carried over by this read, or
    /// none after a complete read, which ends every carry-over of the organization.
    /// </summary>
    public void Record(string orgSid, IReadOnlySet<string> carried)
    {
        if (carried.Count == 0) _carried.TryRemove(orgSid, out _);
        else _carried[orgSid] = carried.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
