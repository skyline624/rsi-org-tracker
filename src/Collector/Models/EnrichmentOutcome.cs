namespace Collector.Models;

/// <summary>Values of <see cref="UserEnrichmentQueue.Outcome"/>.</summary>
public static class EnrichmentOutcome
{
    /// <summary>Profile read and stored. Terminal.</summary>
    public const string Enriched = "enriched";

    /// <summary>The profile answered 404: handle deleted or renamed. Terminal.</summary>
    public const string Gone = "gone";

    /// <summary>Live profile without a UEE citizen record ("n/a"); checked again later.</summary>
    public const string NoCitizenRecord = "na";

    /// <summary>Transient failure; retried after a backoff.</summary>
    public const string Failed = "failed";

    /// <summary>Too many failures. Terminal.</summary>
    public const string Abandoned = "abandoned";
}
