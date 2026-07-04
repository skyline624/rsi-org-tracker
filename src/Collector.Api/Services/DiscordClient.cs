using System.Net.Http.Headers;
using System.Text.Json;
using Collector.Api.Dtos.Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Collector.Api.Services;

/// <summary>
/// Minimal read-only client for the Discord REST API. Resolves a public user
/// profile from a numeric id using a bot token (config key Discord:BotToken,
/// env Discord__BotToken). No gateway, no intents, no server membership needed.
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
    private readonly ILogger<DiscordClient> _logger;
    private readonly bool _configured;

    public DiscordClient(HttpClient http, IConfiguration config, ILogger<DiscordClient> logger)
    {
        _http = http;
        _logger = logger;

        var token = config["Discord:BotToken"];
        _configured = !string.IsNullOrWhiteSpace(token);

        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
        _http.Timeout = TimeSpan.FromSeconds(8);
        if (_configured)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SC-Org-Tracker/1.0");
    }

    public bool IsConfigured => _configured;

    /// <summary>Public profile for a Discord user id, or null if unknown / unconfigured / invalid.</summary>
    public async Task<DiscordUserDto?> GetUserAsync(string id, CancellationToken ct = default)
    {
        if (!_configured || !ulong.TryParse(id, out _)) return null;
        try
        {
            using var resp = await _http.GetAsync($"users/{id}", ct);
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
