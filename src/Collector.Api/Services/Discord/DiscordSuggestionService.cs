using System.Globalization;
using Collector.Api.Auth;
using Collector.Api.Dtos.Discord;
using Collector.Api.Errors;
using Collector.Data;
using Collector.Data.Repositories;
using Collector.Discord;
using Collector.Models;
using Collector.Services;
using Microsoft.EntityFrameworkCore;

namespace Collector.Api.Services.Discord;

/// <summary>
/// Link suggestions between Discord members and RSI people, and the two answers to them
/// (spec § 10.1). Suggestions are computed on read for the active, non-bot members of a
/// guild whose Discord id nobody is linked to yet. Validating one writes an entity link on
/// the person read back from the database; ignoring one writes a rejection keyed by
/// <see cref="CitizenKey"/> so the pair is not suggested again. Every write holds the Discord
/// write gate: the retention pass reads entity_links under it right before purging.
/// </summary>
public sealed class DiscordSuggestionService(
    TrackerDbContext db,
    DiscordHandleLookup lookup,
    IEntityResolver resolver,
    IEntityLinkRepository links,
    DiscordWriteGate gate,
    CurrentUserAccessor currentUser)
{
    /// <summary>Longest handle accepted, so that "h:" + handle fits the 100-character CitizenKey.</summary>
    public const int MaxHandleLength = 98;

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    public const string AutomaticAuthor = "discord-auto-link";
    public const int AutomaticBatchSize = 100;

    /// <summary>
    /// Key of an RSI person in discord_link_rejections: the citizen id when known, else "h:"
    /// followed by the lower-case handle.
    /// </summary>
    public static string CitizenKey(int? citizenId, string handle)
        => citizenId is int cid ? cid.ToString(CultureInfo.InvariantCulture) : HandleKey(handle);

    /// <summary>The handle form of <see cref="CitizenKey"/>.</summary>
    public static string HandleKey(string handle) => "h:" + handle.Trim().ToLowerInvariant();

    /// <summary>
    /// Suggestions for the guild, strong first. A match is strong when the handle is an active
    /// member of the org the guild is mapped to, or of the org the member's own corpo tag
    /// names: another member's tag never counts. Rejected pairs are left out, whichever key the
    /// rejection was saved under.
    /// </summary>
    public async Task<IReadOnlyList<DiscordSuggestionDto>> GetSuggestionsAsync(string guildId, CancellationToken ct)
    {
        var guild = await db.DiscordGuilds.AsNoTracking()
                .Where(g => g.GuildId == guildId)
                .Select(g => new { g.OrgSid })
                .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Serveur Discord inconnu.");

        var members = await (
                from m in db.DiscordMembers.AsNoTracking()
                join a in db.DiscordAccounts.AsNoTracking() on m.DiscordUserId equals a.DiscordUserId
                where m.GuildId == guildId && m.LeftAt == null && !a.IsBot
                      && !db.DiscordOptOuts.Any(o => o.DiscordUserId == m.DiscordUserId)
                      && !db.EntityLinks.Any(l => l.Provider == LinkProviders.Discord && l.Value == m.DiscordUserId)
                select new { m.DiscordUserId, m.Nick, a.GlobalName, a.Username })
            .ToListAsync(ct);
        if (members.Count == 0) return [];

        var tokens = members.ToDictionary(
            m => m.DiscordUserId,
            m => HandleTokenizer.Candidates(m.Nick, m.GlobalName, m.Username),
            StringComparer.Ordinal);
        var memberTags = await lookup.MemberTagsAsync(
            members.Select(m => (m.DiscordUserId, (string?)m.Nick, (string?)m.GlobalName)), ct);
        var rosterSids = memberTags.Values.SelectMany(t => t).ToHashSet(StringComparer.Ordinal);
        if (guild.OrgSid is not null) rosterSids.Add(guild.OrgSid);
        var matches = await lookup.FindAsync(tokens.Values.SelectMany(t => t), rosterSids, ct);
        if (matches.Count == 0) return [];
        var matchesByValue = matches.ToLookup(m => m.MatchedValue, StringComparer.OrdinalIgnoreCase);

        var rejected = (await db.DiscordLinkRejections.AsNoTracking()
                .Where(r => db.DiscordMembers.Any(m =>
                    m.GuildId == guildId && m.LeftAt == null && m.DiscordUserId == r.DiscordUserId))
                .Select(r => new { r.DiscordUserId, r.CitizenKey })
                .ToListAsync(ct))
            .Select(r => (r.DiscordUserId, r.CitizenKey))
            .ToHashSet();

        var suggestions = new List<DiscordSuggestionDto>();
        foreach (var member in members)
        {
            // One suggestion per (member, person); the first token that matched is kept,
            // unless a later one makes it stronger (the server's corpo over a tag's).
            var byPerson = new Dictionary<string, DiscordSuggestionDto>(StringComparer.Ordinal);
            foreach (var token in tokens[member.DiscordUserId])
            {
                foreach (var match in matchesByValue[token])
                {
                    string? via = null;
                    if (match.RosterOrgSid is { } org)
                    {
                        via = org == guild.OrgSid ? DiscordStrongVia.Server
                            : memberTags[member.DiscordUserId].Contains(org) ? DiscordStrongVia.Tag
                            : null;
                        // Another corpo's roster, read for another member's tag: not about this member.
                        if (via is null) continue;
                    }

                    var key = CitizenKey(match.CitizenId, match.Handle);
                    if (rejected.Contains((member.DiscordUserId, key))
                        || rejected.Contains((member.DiscordUserId, HandleKey(match.Handle))))
                        continue;

                    if (byPerson.TryGetValue(key, out var seen))
                    {
                        if (StrengthOf(via) < StrengthOf(seen.StrongVia))
                        {
                            seen.Confidence = DiscordSuggestionConfidence.Strong;
                            seen.StrongVia = via;
                            seen.StrongOrgSid = match.RosterOrgSid;
                            seen.MatchedToken = token;
                            seen.Handle = match.Handle;
                        }
                        continue;
                    }

                    byPerson[key] = new DiscordSuggestionDto
                    {
                        DiscordUserId = member.DiscordUserId,
                        DiscordName = member.Nick ?? member.GlobalName ?? member.Username,
                        MatchedToken = token,
                        Handle = match.Handle,
                        CitizenId = match.CitizenId,
                        DisplayName = match.DisplayName,
                        Confidence = via is null ? DiscordSuggestionConfidence.Medium : DiscordSuggestionConfidence.Strong,
                        StrongVia = via,
                        StrongOrgSid = via is null ? null : match.RosterOrgSid,
                    };
                }
            }
            suggestions.AddRange(byPerson.Values);
        }

        return suggestions
            .OrderBy(s => s.Confidence == DiscordSuggestionConfidence.Strong ? 0 : 1)
            .ThenBy(s => s.DiscordName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Handle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.DiscordUserId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Validates a suggestion: re-reads the person under the write gate, resolves their
    /// entity from the citizen id and the canonical current handle (never the Discord token
    /// nor a former handle, which the resolver would write into CurrentHandle), then adds the
    /// discord link if it is not there yet. 404 when no source knows the handle.
    /// </summary>
    public async Task<DiscordLinkCreatedDto> LinkAsync(CreateDiscordLinkRequest request, CancellationToken ct)
    {
        var (discordUserId, handle) = Validate(request.DiscordUserId, request.CitizenId, request.Handle);
        using var lease = await EnterGateAsync(ct);
        await RefuseOptedOutAsync(discordUserId, ct);

        var person = await lookup.ResolvePersonAsync(request.CitizenId, handle, ct)
            ?? throw new NotFoundException($"Aucun citoyen RSI connu sous le handle « {handle} ».");

        var (entityId, _) = await CreateLinkAsync(discordUserId, person,
            currentUser.UserId ?? 0, currentUser.Username ?? "unknown", ct);
        return new DiscordLinkCreatedDto { EntityId = entityId, Handle = person.Handle };
    }

    /// <summary>
    /// Applies the same suggestions as the site under the write gate. Only active members of
    /// the mapped RSI org, or of the org a member's own corpo tag names, qualify, so an
    /// unmapped guild can have strong links too; existing links, ignored pairs, bots and
    /// opt-outs are left out by GetSuggestionsAsync. Read routes stay read-only and medium
    /// matches need validation.
    /// </summary>
    public async Task<int> AutoLinkStrongAsync(string guildId, CancellationToken ct)
    {
        using var lease = await gate.EnterAsync(ct);
        // The guild may have been erased since it was scheduled.
        if (!await db.DiscordGuilds.AsNoTracking().AnyAsync(g => g.GuildId == guildId, ct))
            return 0;

        var strong = (await GetSuggestionsAsync(guildId, ct))
            .Where(s => s.Confidence == DiscordSuggestionConfidence.Strong)
            // Keep all suggestions of one account together; creating its first link hides it
            // from later suggestion reads. Release the write gate between bounded batches.
            .GroupBy(s => s.DiscordUserId).Take(AutomaticBatchSize).SelectMany(group => group).ToList();
        var created = 0;
        foreach (var suggestion in strong)
        {
            ct.ThrowIfCancellationRequested();
            var person = await lookup.ResolvePersonAsync(suggestion.CitizenId, suggestion.Handle, ct);
            if (person is null) continue;
            var (_, added) = await CreateLinkAsync(suggestion.DiscordUserId, person, 0, AutomaticAuthor, ct);
            if (added) created++;
        }
        return created;
    }

    private async Task<(long EntityId, bool Added)> CreateLinkAsync(
        string discordUserId, RsiPerson person, long authorId, string authorName, CancellationToken ct)
    {
        var entityId = await resolver.ResolveOrCreateAsync(person.CitizenId, person.Handle, person.DisplayName, ct);

        if (await links.GetByEntityProviderValueAsync(entityId, LinkProviders.Discord, discordUserId, ct) is null)
        {
            var now = DateTime.UtcNow;
            await links.AddAsync(new EntityLink
            {
                TrackedEntityId = entityId,
                Provider = LinkProviders.Discord,
                Value = discordUserId,
                AuthorApiUserId = authorId,
                AuthorUsername = authorName,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct);
            await links.SaveChangesAsync(ct);
            return (entityId, true);
        }
        return (entityId, false);
    }

    /// <summary>Ignores a suggestion. Idempotent: the same pair returns the existing rejection's id.</summary>
    public async Task<long> RejectAsync(CreateDiscordLinkRejectionRequest request, CancellationToken ct)
    {
        var (discordUserId, handle) = Validate(request.DiscordUserId, request.CitizenId, request.Handle);
        var key = CitizenKey(request.CitizenId, handle);
        using var lease = await EnterGateAsync(ct);
        await RefuseOptedOutAsync(discordUserId, ct);

        var existing = await db.DiscordLinkRejections.AsNoTracking()
            .Where(r => r.DiscordUserId == discordUserId && r.CitizenKey == key)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is long id) return id;

        var row = new DiscordLinkRejection
        {
            DiscordUserId = discordUserId,
            CitizenKey = key,
            ByApiUserId = currentUser.UserId ?? 0,
            ByUsername = currentUser.Username ?? "unknown",
            CreatedAt = DateTime.UtcNow,
        };
        db.DiscordLinkRejections.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    /// <summary>
    /// Deletes a Discord link and refuses its pair in the same save, under the write gate:
    /// without the refusal the automatic linker would create a strong link again within a
    /// minute. The pair is refused under the citizen id and under the handle, because the
    /// strong match may come from a roster row that has no citizen id. The caller checks
    /// who may delete the link.
    /// </summary>
    public async Task UnlinkAsync(long linkId, CancellationToken ct)
    {
        using var lease = await EnterGateAsync(ct);
        var link = await db.EntityLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null) return;

        var person = await db.TrackedEntities.AsNoTracking()
            .Where(e => e.Id == link.TrackedEntityId)
            .Select(e => new { e.CitizenId, e.CurrentHandle })
            .FirstOrDefaultAsync(ct);
        var keys = new List<string>();
        if (person?.CitizenId is int cid) keys.Add(CitizenKey(cid, ""));
        if (!string.IsNullOrWhiteSpace(person?.CurrentHandle) && person.CurrentHandle.Trim().Length <= MaxHandleLength)
            keys.Add(HandleKey(person.CurrentHandle));

        var existing = await db.DiscordLinkRejections.AsNoTracking()
            .Where(r => r.DiscordUserId == link.Value && keys.Contains(r.CitizenKey))
            .Select(r => r.CitizenKey)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var key in keys.Except(existing, StringComparer.Ordinal))
        {
            db.DiscordLinkRejections.Add(new DiscordLinkRejection
            {
                DiscordUserId = link.Value,
                CitizenKey = key,
                ByApiUserId = currentUser.UserId ?? 0,
                ByUsername = currentUser.Username ?? "unknown",
                CreatedAt = now,
            });
        }
        db.EntityLinks.Remove(link);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Undoes a rejection: only its author or an admin may (403 otherwise).</summary>
    public async Task DeleteRejectionAsync(long id, CancellationToken ct)
    {
        using var lease = await EnterGateAsync(ct);
        var row = await db.DiscordLinkRejections.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Rejet inconnu.");
        if (!currentUser.IsAdmin && row.ByApiUserId != (currentUser.UserId ?? -1))
            throw new ForbiddenException("Seuls l'auteur de ce rejet et les administrateurs peuvent l'annuler.");

        db.DiscordLinkRejections.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Lower is stronger: the server's corpo, then a member's tag, then a medium match.</summary>
    private static int StrengthOf(string? via) => via switch
    {
        DiscordStrongVia.Server => 0,
        DiscordStrongVia.Tag => 1,
        _ => 2,
    };

    private async Task RefuseOptedOutAsync(string discordUserId, CancellationToken ct)
    {
        if (await db.DiscordOptOuts.AsNoTracking().AnyAsync(o => o.DiscordUserId == discordUserId, ct))
            throw new ForbiddenException("Ce compte Discord est exclu du suivi.");
    }

    private static (string DiscordUserId, string Handle) Validate(string? discordUserId, int? citizenId, string? handle)
    {
        if (!DiscordSnowflake.IsValid(discordUserId))
            throw new ValidationException("discordUserId doit être un identifiant Discord (17 à 20 chiffres).");
        if (citizenId is <= 0)
            throw new ValidationException("citizenId doit être un nombre positif.");
        var trimmed = handle?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxHandleLength)
            throw new ValidationException($"handle doit faire de 1 à {MaxHandleLength} caractères.");
        return (discordUserId!, trimmed);
    }

    private async Task<IDisposable> EnterGateAsync(CancellationToken ct)
        => await gate.TryEnterAsync(GateTimeout, ct)
           ?? throw new ServiceUnavailableException("Écritures Discord en cours, réessaie dans 30 s.", 30);
}
