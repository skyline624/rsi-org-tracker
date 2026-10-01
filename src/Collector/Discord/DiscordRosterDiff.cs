using System.Text.Encodings.Web;
using System.Text.Json;
using Collector.Models;

namespace Collector.Discord;

/// <summary>
/// Turns a normalized sync and the stored state of its server into the rows to write and the
/// history events to record (spec § 9.3). Pure: no clock and no database, so every rule is
/// unit-tested, and the repository writes the plan as given.
/// </summary>
public static class DiscordRosterDiff
{
    /// <summary>A complete sync with more departures than this share is highlighted.</summary>
    public const double MassDepartureRatio = 0.25;

    /// <summary>…provided it removes at least this many.</summary>
    public const int MassDepartureMinimum = 10;

    /// <summary>Whether a new role starts as a rank: Discord lists hoisted roles apart, and integration roles are never ranks.</summary>
    public static bool DefaultIsRank(NormalizedRole role) => role.Hoist && !role.Managed;

    /// <summary>RankOrder of a new role: its position when it starts as a rank, null otherwise.</summary>
    public static int? DefaultRankOrder(NormalizedRole role) => DefaultIsRank(role) ? role.Position : null;

    /// <summary>
    /// Plans one sync. <paramref name="collectedAt"/> is the server's date of the collection
    /// (ReceivedAt minus its duration). Opted-out members are dropped before anything else but
    /// still count as listed, so the sync stays complete and they never "leave".
    /// </summary>
    public static RosterPlan Compute(string guildId, NormalizedSync sync, DateTime collectedAt,
        RosterSnapshot snapshot, IReadOnlySet<string> optedOutUserIds)
    {
        var planner = new Planner(guildId, sync, collectedAt, snapshot, optedOutUserIds);
        var members = sync.Members.Where(m => !optedOutUserIds.Contains(m.UserId)).ToList();

        planner.PlanRoles();
        foreach (var member in members) planner.PlanAccount(member);
        foreach (var member in members) planner.PlanMember(member);
        planner.PlanDepartures();

        return new RosterPlan(
            IsBaseline: planner.IsBaseline,
            IsComplete: sync.IsComplete,
            MassDepartureDetected: planner.MassDepartureDetected,
            OptedOutCount: sync.Members.Count - members.Count,
            RolesToInsert: planner.RolesToInsert,
            RolesToUpdate: planner.RolesToUpdate,
            RoleIdsToMarkDeleted: planner.RoleIdsToMarkDeleted,
            AccountsToInsert: planner.AccountsToInsert,
            AccountsToUpdate: planner.AccountsToUpdate,
            MembersToInsert: planner.MembersToInsert,
            MembersToUpdate: planner.MembersToUpdate,
            Events: planner.Events,
            EventIdsToDelete: planner.EventIdsToDelete,
            PresentUserIds: members.Select(m => m.UserId).ToList());
    }

    /// <summary>Accumulates the plan of one sync.</summary>
    private sealed class Planner(string guildId, NormalizedSync sync, DateTime collectedAt, RosterSnapshot snapshot,
        IReadOnlySet<string> optedOutUserIds)
    {
        private readonly Dictionary<string, NormalizedRole> _payloadRoles =
            sync.Roles.ToDictionary(r => r.RoleId, StringComparer.Ordinal);

        public bool IsBaseline => snapshot.Guild is null || snapshot.Guild.BaselinePending;
        public bool MassDepartureDetected { get; private set; }

        public List<NormalizedRole> RolesToInsert { get; } = [];
        public List<NormalizedRole> RolesToUpdate { get; } = [];
        public List<string> RoleIdsToMarkDeleted { get; } = [];
        public List<AccountWrite> AccountsToInsert { get; } = [];
        public List<AccountWrite> AccountsToUpdate { get; } = [];
        public List<MemberWrite> MembersToInsert { get; } = [];
        public List<MemberWrite> MembersToUpdate { get; } = [];
        public List<PlannedEvent> Events { get; } = [];
        public List<long> EventIdsToDelete { get; } = [];

        /// <summary>
        /// The role list is always sent whole, even by a partial sync: known roles are updated
        /// (a deleted one that comes back is restored), new ones inserted, missing ones deleted.
        /// </summary>
        public void PlanRoles()
        {
            foreach (var role in sync.Roles)
            {
                (snapshot.Roles.ContainsKey(role.RoleId) ? RolesToUpdate : RolesToInsert).Add(role);
            }

            RoleIdsToMarkDeleted.AddRange(snapshot.Roles.Values
                .Where(r => !r.IsDeleted && !_payloadRoles.ContainsKey(r.RoleId))
                .Select(r => r.RoleId)
                .Order(StringComparer.Ordinal));
        }

        /// <summary>
        /// Accounts span servers: a rename is reported even by a baseline (never a silent
        /// rename), with a null GuildId.
        /// </summary>
        public void PlanAccount(NormalizedMember member)
        {
            var write = new AccountWrite(member.UserId, member.Username, member.GlobalName, member.IsBot);
            if (!snapshot.Accounts.TryGetValue(member.UserId, out var stored))
            {
                AccountsToInsert.Add(write);
                return;
            }

            // An older collection from another guild may arrive after a newer account
            // observation. Its membership still matters, but global identity must not regress.
            if (stored.LastSeenAt is { } lastSeenAt && collectedAt < lastSeenAt) return;

            var renamed = !string.Equals(stored.Username, member.Username, StringComparison.Ordinal);
            var displayRenamed = !string.Equals(stored.GlobalName, member.GlobalName, StringComparison.Ordinal);
            if (renamed)
            {
                Events.Add(new PlannedEvent(null, member.UserId, DiscordEventTypes.UsernameChanged,
                    stored.Username, member.Username, null, null, collectedAt));
            }
            if (displayRenamed)
            {
                Events.Add(new PlannedEvent(null, member.UserId, DiscordEventTypes.GlobalNameChanged,
                    stored.GlobalName, member.GlobalName, null, null, collectedAt));
            }
            if (renamed || displayRenamed || stored.IsBot != member.IsBot) AccountsToUpdate.Add(write);
        }

        /// <summary>A server event; a baseline records none.</summary>
        private void AddGuildEvent(string userId, string type, string? oldValue, string? newValue,
            DateTime? occurredAt, DateTime? notBefore)
        {
            if (IsBaseline) return;
            Events.Add(new PlannedEvent(guildId, userId, type, oldValue, newValue, occurredAt, notBefore, collectedAt));
        }

        /// <summary>A JoinedAt must move forward by more than this to mean "left and came back".</summary>
        private static readonly TimeSpan RejoinTolerance = TimeSpan.FromSeconds(1);

        /// <summary>
        /// roles_changed values keep accents and emoji readable in the database. They are
        /// stored text, re-encoded by the API serializer, never written into HTML as is.
        /// </summary>
        private static readonly JsonSerializerOptions RoleJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public void PlanMember(NormalizedMember member)
        {
            if (!snapshot.Members.TryGetValue(member.UserId, out var stored)) PlanNewcomer(member);
            else if (stored.LeftAt is null) PlanPresent(member, stored, clearsDeparture: false);
            else PlanReturn(member, stored);
        }

        /// <summary>A member with no row on this server.</summary>
        private void PlanNewcomer(NormalizedMember member)
        {
            MembersToInsert.Add(new MemberWrite(member.UserId, member.Nick, member.RoleIds, member.JoinedAt, null));

            var guild = snapshot.Guild!;
            if (IsBaseline) return; // an interrupted baseline stays silent while its remaining rows are inserted

            if (member.JoinedAt is { } joinedAt)
            {
                // Joined before tracking started: missed by a partial sync or an interrupted
                // baseline, not an arrival.
                if (joinedAt >= guild.FirstSyncAt)
                {
                    AddGuildEvent(member.UserId, DiscordEventTypes.Joined, null, DiscordFormats.Iso(joinedAt), joinedAt, null);
                }
            }
            else if (guild.LastCompleteSyncAt is { } lastComplete)
            {
                // The last complete sync did not list them: they arrived since.
                AddGuildEvent(member.UserId, DiscordEventTypes.Joined, null, null, null, lastComplete);
            }
        }

        /// <summary>
        /// A member already on the server: join-date, role and nickname changes. Also used for
        /// a false departure, which is the same stay (<paramref name="clearsDeparture"/> writes
        /// the row even when nothing else changed, to clear LeftAt).
        /// </summary>
        private void PlanPresent(NormalizedMember member, MemberSnapshot stored, bool clearsDeparture)
        {
            var changed = clearsDeparture;

            // A null never overwrites a known date, and an earlier one is ignored.
            var joinedAt = stored.JoinedAt;
            if (member.JoinedAt is { } received)
            {
                if (stored.JoinedAt is not { } known)
                {
                    joinedAt = received; // completed silently
                    changed = true;
                }
                else if (received > known + RejoinTolerance)
                {
                    // Left and came back between two syncs.
                    AddGuildEvent(member.UserId, DiscordEventTypes.Rejoined,
                        DiscordFormats.Iso(known), DiscordFormats.Iso(received), received, null);
                    joinedAt = received;
                    changed = true;
                }
            }

            // Roles deleted before or by this sync are not the member's doing: leave them out,
            // or deleting one role would log a roles_changed for every holder.
            var oldRoles = stored.RoleIds.Where(_payloadRoles.ContainsKey).Order(StringComparer.Ordinal).ToList();
            if (!oldRoles.SequenceEqual(member.RoleIds, StringComparer.Ordinal))
            {
                AddGuildEvent(member.UserId, DiscordEventTypes.RolesChanged,
                    RolesJson(oldRoles, namesOfThePast: true), RolesJson(member.RoleIds, namesOfThePast: false),
                    null, stored.LastSeenAt);
            }
            changed |= !stored.RoleIds.SequenceEqual(member.RoleIds, StringComparer.Ordinal);

            if (!string.Equals(stored.Nick, member.Nick, StringComparison.Ordinal))
            {
                AddGuildEvent(member.UserId, DiscordEventTypes.NickChanged, stored.Nick, member.Nick, null, stored.LastSeenAt);
                changed = true;
            }

            if (changed) MembersToUpdate.Add(new MemberWrite(member.UserId, member.Nick, member.RoleIds, joinedAt, null));
        }

        /// <summary>A member marked as gone who is listed again.</summary>
        private void PlanReturn(NormalizedMember member, MemberSnapshot stored)
        {
            if (stored.JoinedAt is { } known && member.JoinedAt is { } received && received <= known)
            {
                // Same stay: the departure was wrong (a complete sync missed them). Undo it by
                // removing its "left" event, and log no "rejoined".
                if (stored.LastLeftEventId is { } leftEventId) EventIdsToDelete.Add(leftEventId);
                PlanPresent(member, stored, clearsDeparture: true);
                return;
            }

            // A new stay, with its own roles and nickname: nothing is compared with the old one.
            // Its JoinedAt is the one received, null when unknown (the old stay's date would make
            // the next sync log the same return again).
            AddGuildEvent(member.UserId, DiscordEventTypes.Rejoined,
                stored.JoinedAt is { } oldJoinedAt ? DiscordFormats.Iso(oldJoinedAt) : null,
                member.JoinedAt is { } newJoinedAt ? DiscordFormats.Iso(newJoinedAt) : null,
                member.JoinedAt,
                member.JoinedAt is null ? stored.LeftAt : null);
            MembersToUpdate.Add(new MemberWrite(member.UserId, member.Nick, member.RoleIds, member.JoinedAt, null));
        }

        /// <summary>
        /// Only a complete sync proves an absence. Every absent active member has left;
        /// a large number of departures is a signal, never a reason to ignore the observation.
        /// </summary>
        public void PlanDepartures()
        {
            if (!sync.IsComplete || snapshot.Guild is null || IsBaseline) return;

            // The raw payload: an opted-out member was listed, so did not leave.
            var listed = sync.Members.Select(m => m.UserId).ToHashSet(StringComparer.Ordinal);
            var active = snapshot.Members.Values.Where(m => m.LeftAt is null && !optedOutUserIds.Contains(m.UserId)).ToList();
            var gone = active.Where(m => !listed.Contains(m.UserId)).OrderBy(m => m.UserId, StringComparer.Ordinal).ToList();

            MassDepartureDetected = gone.Count >= MassDepartureMinimum && gone.Count > active.Count * MassDepartureRatio;

            foreach (var member in gone)
            {
                MembersToUpdate.Add(new MemberWrite(member.UserId, member.Nick, member.RoleIds, member.JoinedAt, collectedAt));
                AddGuildEvent(member.UserId, DiscordEventTypes.Left,
                    member.JoinedAt is { } joinedAt ? DiscordFormats.Iso(joinedAt) : null, null, null, member.LastSeenAt);
            }
        }

        /// <summary>Compact JSON [{"id","name"}] ordered by role id.</summary>
        private string RolesJson(IEnumerable<string> roleIds, bool namesOfThePast) =>
            JsonSerializer.Serialize(
                roleIds.Order(StringComparer.Ordinal).Select(id => new { id, name = RoleName(id, namesOfThePast) }),
                RoleJson);

        /// <summary>Old values use the stored name (the name at the time), new values the payload's.</summary>
        private string RoleName(string roleId, bool namesOfThePast)
        {
            if (namesOfThePast && snapshot.Roles.TryGetValue(roleId, out var stored)) return stored.Name;
            if (_payloadRoles.TryGetValue(roleId, out var current)) return current.Name;
            return snapshot.Roles.TryGetValue(roleId, out var known) ? known.Name : roleId;
        }
    }
}
