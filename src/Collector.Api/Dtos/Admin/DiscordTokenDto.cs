using System.ComponentModel.DataAnnotations;

namespace Collector.Api.Dtos.Admin;

public record SetDiscordTokenRequest([Required, MinLength(20)] string Token);
