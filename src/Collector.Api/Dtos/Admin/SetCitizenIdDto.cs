using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Admin;

/// <summary>Manually assign a citizen id to a person who has none (e.g. a redacted account).</summary>
public record SetCitizenIdRequest([Range(1, int.MaxValue)] int CitizenId);
