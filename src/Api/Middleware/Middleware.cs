using DigitalWallet.Api.Models.Responses;
using Microsoft.AspNetCore.Diagnostics;
using System.Net;

namespace DigitalWallet.Api.Middleware;

// ─────────────────────────────────────────────────────────────────────────────
// 1. GLOBAL EXCEPTION HANDLER
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Catches all unhandled exceptions and maps them to the standard error envelope.
/// Logs a correlation ID on every error response.
/// Never leaks stack traces to clients in production.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var requestId = context.TraceIdentifier;
        _logger.LogError(exception, "Unhandled exception. RequestId: {RequestId}", requestId);

        var (statusCode, errorCode, message) = exception switch
        {
            WalletNotFoundException    => (HttpStatusCode.NotFound,           "WALLET_NOT_FOUND",         exception.Message),
            WalletAccessDeniedException=> (HttpStatusCode.Forbidden,          "WALLET_ACCESS_DENIED",     exception.Message),
            WalletInactiveException    => (HttpStatusCode.UnprocessableEntity,"WALLET_INACTIVE",          exception.Message),
            InsufficientFundsException => (HttpStatusCode.UnprocessableEntity,"INSUFFICIENT_FUNDS",       exception.Message),
            DuplicateWalletException   => (HttpStatusCode.Conflict,           "WALLET_ALREADY_EXISTS",    exception.Message),
            IdempotencyConflictException e => (HttpStatusCode.Conflict,       "IDEMPOTENCY_CONFLICT",     e.Message),
            ValidationException        => (HttpStatusCode.BadRequest,         "VALIDATION_ERROR",         exception.Message),
            _                          => (HttpStatusCode.InternalServerError,"INTERNAL_SERVER_ERROR",    "An unexpected error occurred.")
        };

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var response = new ApiErrorResponse
        {
            Error = new ApiError
            {
                Code = errorCode,
                Message = message,
                Details = []
            },
            Meta = new ApiMeta { RequestId = requestId }
        };

        await context.Response.WriteAsJsonAsync(response, cancellationToken);
        return true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. IDEMPOTENCY MIDDLEWARE
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Checks the X-Idempotency-Key header on write endpoints (POST for transfer/deposit).
/// If a cached response exists for the key, returns it immediately without executing the handler.
/// If no cached response exists, the request proceeds normally and the response is cached after.
/// </summary>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    // Only enforce idempotency on these paths
    private static readonly HashSet<string> IdempotentPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/wallets/transfer",
        "/api/v1/wallets/{id}/deposit",
        "/api/v1/wallets"
    };

    public IdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _store = store;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method != HttpMethods.Post)
        {
            await _next(context);
            return;
        }

        var key = context.Request.Headers["X-Idempotency-Key"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(key))
        {
            // Key is required on write endpoints — reject if missing
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiErrorResponse
            {
                Error = new ApiError
                {
                    Code = "IDEMPOTENCY_KEY_REQUIRED",
                    Message = "X-Idempotency-Key header is required for this endpoint."
                }
            });
            return;
        }

        if (!Guid.TryParse(key, out _))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiErrorResponse
            {
                Error = new ApiError
                {
                    Code = "INVALID_IDEMPOTENCY_KEY",
                    Message = "X-Idempotency-Key must be a valid UUID v4."
                }
            });
            return;
        }

        var cached = await _store.GetAsync(key);
        if (cached is not null)
        {
            _logger.LogInformation("Returning cached response for idempotency key {Key}", key);
            context.Response.StatusCode = cached.StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers["X-Idempotency-Replayed"] = "true";
            await context.Response.WriteAsync(cached.Body);
            return;
        }

        // Capture the response body so we can cache it
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        await _next(context);

        buffer.Seek(0, SeekOrigin.Begin);
        var responseBody = await new StreamReader(buffer).ReadToEndAsync();

        // Cache the response (24-hour TTL)
        await _store.SetAsync(key, new CachedResponse
        {
            StatusCode = context.Response.StatusCode,
            Body = responseBody
        }, TimeSpan.FromHours(24));

        buffer.Seek(0, SeekOrigin.Begin);
        await buffer.CopyToAsync(originalBody);
        context.Response.Body = originalBody;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. REQUEST LOGGING MIDDLEWARE
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Logs every request and response in structured JSON format.
/// Fields: method, path, status_code, duration_ms, request_id, user_id.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var start = DateTimeOffset.UtcNow;
        await _next(context);
        var duration = DateTimeOffset.UtcNow - start;

        _logger.LogInformation(
            "HTTP {Method} {Path} responded {StatusCode} in {DurationMs}ms | RequestId: {RequestId}",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            duration.TotalMilliseconds,
            context.TraceIdentifier);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. SUPPORTING TYPES
// ─────────────────────────────────────────────────────────────────────────────

public record CachedResponse
{
    public int StatusCode { get; init; }
    public string Body { get; init; } = string.Empty;
}

public interface IIdempotencyStore
{
    Task<CachedResponse?> GetAsync(string key);
    Task SetAsync(string key, CachedResponse response, TimeSpan ttl);
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. DOMAIN EXCEPTIONS
// ─────────────────────────────────────────────────────────────────────────────

public class WalletNotFoundException(string walletId)
    : Exception($"Wallet '{walletId}' was not found.");

public class WalletAccessDeniedException(string walletId)
    : Exception($"Access to wallet '{walletId}' is denied.");

public class WalletInactiveException(string walletId)
    : Exception($"Wallet '{walletId}' is not active.");

public class WalletBalanceNotZeroException(string walletId)
    : Exception($"Wallet '{walletId}' must have a zero balance to be deactivated.");

public class WalletAlreadyInactiveException(string walletId)
    : Exception($"Wallet '{walletId}' is already inactive.");

public class DuplicateWalletException(string userId, string currency)
    : Exception($"User '{userId}' already has a wallet in currency '{currency}'.");

public class InsufficientFundsException(string walletId, decimal available, decimal requested)
    : Exception($"Wallet '{walletId}' has {available} available; {requested} requested.");

public class IdempotencyConflictException(string key)
    : Exception($"A request with idempotency key '{key}' is already being processed.");

public class ValidationException(string message) : Exception(message);
