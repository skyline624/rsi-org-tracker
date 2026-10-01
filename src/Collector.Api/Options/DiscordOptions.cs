namespace Collector.Api.Options;

/// <summary>
/// Discord roster settings, bound to <c>Discord</c> (env <c>COLLECTOR_API_Discord__*</c>).
/// The same section holds the bot token (<c>Discord:BotToken</c>), which
/// <c>DiscordTokenStore</c> reads on its own.
/// </summary>
public sealed class DiscordOptions
{
    public const string Section = "Discord";

    /// <summary>What users enter in the Vencord plugin, shown in the settings panel.</summary>
    public IngestOptions Ingest { get; set; } = new();

    /// <summary>How long the upload journal and departed, unlinked accounts are kept.</summary>
    public RetentionOptions Retention { get; set; } = new();

    public sealed class IngestOptions
    {
        /// <summary>Public base URL of the tracker, scheme and host only (<c>https://&lt;IP&gt;</c>).</summary>
        public string? PublicUrl { get; set; }

        /// <summary>SHA-256 fingerprint of the nginx certificate, as openssl prints it (the plugin normalises it).</summary>
        public string? CertificateSha256 { get; set; }
    }

    public sealed class RetentionOptions
    {
        /// <summary><c>discord_syncs</c> rows older than this many days are deleted.</summary>
        public int SyncLogDays { get; set; } = 365;

        /// <summary>An unlinked account gone from every server for this many days is purged.</summary>
        public int DepartedAccountDays { get; set; } = 730;
    }
}
