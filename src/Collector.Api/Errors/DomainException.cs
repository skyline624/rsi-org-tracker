namespace Collector.Api.Errors;

/// <summary>
/// An expected failure. Its message is written for the client and is
/// returned as the Problem Details <c>detail</c>; every other exception is a server bug
/// answered with a generic 500. Clients use the optional stable <see cref="Code"/>
/// to decide what to do without parsing the message.
/// </summary>
public abstract class DomainException(int statusCode, string title, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;

    /// <summary>Written as the Problem Details extension <c>code</c> when present.</summary>
    public string? Code { get; } = code;
}

/// <summary>The requested resource does not exist.</summary>
public sealed class NotFoundException(string message) : DomainException(404, "Not Found", message);

/// <summary>The request conflicts with the current resource state.</summary>
public sealed class ConflictException(string message, string? code = null) : DomainException(409, "Conflict", message, code);

/// <summary>The request does not satisfy the domain's validation rules.</summary>
public sealed class ValidationException(string message, string? code = null) : DomainException(400, "Bad Request", message, code);

/// <summary>The supplied credentials cannot authenticate the caller.</summary>
public sealed class AuthenticationFailedException(string message) : DomainException(401, "Unauthorized", message);

/// <summary>The caller is authenticated but cannot perform this operation.</summary>
public sealed class ForbiddenException(string message) : DomainException(403, "Forbidden", message);

/// <summary>
/// A temporary failure such as a held write lock or busy SQLite database. The response
/// carries <c>Retry-After</c> in seconds so the client knows when to try again.
/// </summary>
public sealed class ServiceUnavailableException(string message, int retryAfterSeconds, string? code = "busy")
    : DomainException(503, "Service Unavailable", message, code)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
