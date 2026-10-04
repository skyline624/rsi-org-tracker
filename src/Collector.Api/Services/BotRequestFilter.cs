using Collector.Api.Auth;
using Collector.Api.Errors;
using Collector.Discord;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Collector.Api.Services;

/// <summary>
/// Requires the bot's headers on every /api/bot request (who asked, for which
/// subcommand) and writes each one, autocompletion aside, to the activity log (spec § 5.3).
/// </summary>
public sealed class BotRequestFilter(ActivityLogService activity, CurrentUserAccessor currentUser) : IAsyncActionFilter
{
    public const string DiscordUserHeader = "X-Discord-User";
    public const string CommandHeader = "X-Bot-Command";
    public const string Autocomplete = "autocomplete";
    public const int MaxTargetLength = 100;

    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "joueur", "historique", "recherche", "org", "membres", "mouvements", Autocomplete,
    };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;
        var discordUser = headers[DiscordUserHeader].ToString();
        var command = headers[CommandHeader].ToString();
        if (!DiscordSnowflake.IsValid(discordUser) || !Commands.Contains(command))
            throw new ValidationException(
                $"{DiscordUserHeader} (17 à 20 chiffres) et {CommandHeader} (sous-commande connue) sont requis.");

        await next();

        if (command == Autocomplete) return;
        var target = TargetOf(context.ActionArguments);
        await activity.LogAsync($"bot:{command}", currentUser.UserId, $"discord:{discordUser}", target,
            currentUser.IpAddress, context.HttpContext.RequestAborted);
    }

    /// <summary>The handle, SID or search text of the request.</summary>
    private static string? TargetOf(IDictionary<string, object?> arguments)
    {
        foreach (var name in new[] { "handle", "sid", "q" })
        {
            if (arguments.TryGetValue(name, out var value) && value is string text)
                return text.Length <= MaxTargetLength ? text : text[..MaxTargetLength];
        }
        return null;
    }
}
