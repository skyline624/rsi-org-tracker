namespace Collector.Api.Options;

/// <summary>Voice recording limits, bound to <c>Api:Audio</c>.</summary>
public sealed class AudioSettings
{
    public const string Section = "Api:Audio";

    /// <summary>Total size of the recordings one account may upload (default 500 MB).</summary>
    public long MaxBytesPerUser { get; set; } = 500L * 1024 * 1024;
}
