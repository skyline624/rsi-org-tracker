using System.Net.Http.Headers;
using System.Text.Json;
using Collector.Api.Dtos.Discord;
using Microsoft.Extensions.Logging;

namespace Collector.Api.Services;

/// <summary>
/// Minimal read-only client for the Discord REST API. Resolves a public user
/// profile from a numeric id using a bot token (read from <see cref="DiscordTokenStore"/>
/// on each call, so a rotated token applies immediately). No gateway / intents /
/// server membership needed.
/// </summary>
public class DiscordClient
{
    // (bit index in public_flags, human label) — only the badges worth surfacing.
    private static readonly (int Bit, string Label)[] FlagBadges =
    {
        (0, "Staff"),
        (1, "Partner"),
        (2, "HypeSquad"),
        (3, "Bug Hunter"),
        (6, "HypeSquad Bravery"),
        (7, "HypeSquad Brilliance"),
        (8, "HypeSquad Balance"),
        (9, "Early Supporter"),
        (14, "Bug Hunter II"),
        (17, "Early Verified Bot Dev"),
        (18, "Moderator Alumni"),
        (22, "Active Developer"),
    };

    private readonly HttpClient _http;
    private readonly DiscordTokenStore _tokenStore;
    private readonly ILogger<DiscordClient> _logger;

    public DiscordClient(HttpClient http, DiscordTokenStore tokenStore, ILogger<DiscordClient> logger)
    {
        _http = http;
        _tokenStore = tokenStore;
        _logger = logger;

        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SC-Org-Tracker/1.0");
    }

    /// <summary>Public profile for a Discord user id, or null if unknown / unconfigured / invalid.</summary>
    public async Task<DiscordUserDto?> GetUserAsync(string id, CancellationToken ct = default)
    {
        if (!ulong.TryParse(id, out _)) return null;
        var token = await _tokenStore.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"users/{id}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var uid = root.GetProperty("id").GetString()!;
            var avatar = Str(root, "avatar");
            var flags = root.TryGetProperty("public_flags", out var pf) && pf.ValueKind == JsonValueKind.Number
                ? pf.GetInt32()
                : 0;

            return new DiscordUserDto
            {
                Id = uid,
                Username = Str(root, "username") ?? "",
                GlobalName = Str(root, "global_name"),
                AvatarUrl = BuildAvatarUrl(uid, avatar),
                Badges = DecodeFlags(flags),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord lookup failed for {Id}", id);
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? BuildAvatarUrl(string id, string? avatar)
    {
        if (string.IsNullOrEmpty(avatar)) return null;
        var ext = avatar.StartsWith("a_") ? "gif" : "png";
        return $"https://cdn.discordapp.com/avatars/{id}/{avatar}.{ext}?size=128";
    }

    private static List<string> DecodeFlags(int flags)
    {
        var badges = new List<string>();
        foreach (var (bit, label) in FlagBadges)
            if ((flags & (1 << bit)) != 0)
                badges.Add(label);
        return badges;
    }
}
