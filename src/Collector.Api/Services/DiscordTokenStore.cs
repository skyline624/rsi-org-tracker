using Microsoft.Extensions.Configuration;

namespace Collector.Api.Services;

/// <summary>
/// Stores the Discord bot token in a file (data/discord.token, owner-only) so it can
/// be rotated at runtime from the admin dashboard, falling back to configuration
/// (env Discord__BotToken) when the file is absent. Read on every lookup, so a new
/// token takes effect immediately without restarting the service.
/// </summary>
public class DiscordTokenStore
{
    private readonly string _path;
    private readonly IConfiguration _config;

    public DiscordTokenStore(IConfiguration config)
    {
        _config = config;
        var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../..", "data"));
        _path = Path.Combine(dataDir, "discord.token");
    }

    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_path))
            {
                var token = (await File.ReadAllTextAsync(_path, ct)).Trim();
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }
        }
        catch
        {
            // fall back to configuration
        }
        return _config["Discord:BotToken"];
    }

    public async Task SetAsync(string token, CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(_path, token.Trim(), ct);
        try
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // non-fatal (e.g. non-unix filesystem)
        }
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await GetAsync(ct));
}
