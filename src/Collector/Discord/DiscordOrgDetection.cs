namespace Collector.Discord;

/// <summary>The corpo proposed for an unmapped server, with how many members carry its tag.</summary>
/// <param name="Members">Active members carrying this tag.</param>
/// <param name="TaggedMembers">Active members carrying at least one known corpo tag.</param>
public sealed record DetectedOrg(string Sid, int Members, int TaggedMembers);

/// <summary>
/// Picks the corpo an unmapped server most likely belongs to from its members' known tags
/// (spec § 10.1): the tag carried by at least <see cref="MinimumMembers"/> members and by at
/// least half of the members carrying a known tag. A tie for the top proposes nothing: a
/// person must then choose, as for any other server.
/// </summary>
public static class DiscordOrgDetection
{
    public const int MinimumMembers = 3;

    /// <param name="memberTags">Each active member's known tags (empty when untagged).</param>
    public static DetectedOrg? Pick(IEnumerable<IReadOnlyCollection<string>> memberTags)
    {
        var tagged = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tags in memberTags)
        {
            if (tags.Count == 0) continue;
            tagged++;
            foreach (var sid in tags.Distinct(StringComparer.Ordinal))
                counts[sid] = counts.GetValueOrDefault(sid) + 1;
        }
        if (counts.Count == 0) return null;

        var ranked = counts.OrderByDescending(c => c.Value).Take(2).ToList();
        var top = ranked[0];
        if (ranked.Count == 2 && ranked[1].Value == top.Value) return null;
        if (top.Value < MinimumMembers || top.Value * 2 < tagged) return null;
        return new DetectedOrg(top.Key, top.Value, tagged);
    }
}
