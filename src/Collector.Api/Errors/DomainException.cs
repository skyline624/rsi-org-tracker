namespace Collector.Api.Errors;

/// <summary>
/// An expected, client-caused failure. Its message is written for the client and is
/// returned as the Problem Details <c>detail</c>; every other exception is a server bug
/// answered with a generic 500.
/// </summary>
public abstract class DomainException(int statusCode, string title, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}

public sealed class NotFoundException(string message) : DomainException(404, "Not Found", message);

public sealed class ConflictException(string message) : DomainException(409, "Conflict", message);

public sealed class ValidationException(string message) : DomainException(400, "Bad Request", message);

public sealed class AuthenticationFailedException(string message) : DomainException(401, "Unauthorized", message);

public sealed class ForbiddenException(string message) : DomainException(403, "Forbidden", message);
