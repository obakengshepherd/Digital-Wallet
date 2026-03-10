namespace DigitalWallet.Api.Models.Responses;

public record WalletResponse
{
    public string Id { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal Balance { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public record TransactionResponse
{
    public string Id { get; init; } = string.Empty;
    public string WalletId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty; // CREDIT | DEBIT
    public decimal Amount { get; init; }
    public decimal BalanceAfter { get; init; }
    public string? ReferenceId { get; init; }
    public string? Note { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public record TransferResponse
{
    public string TransferId { get; init; } = string.Empty;
    public string SourceWalletId { get; init; } = string.Empty;
    public string DestinationWalletId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal SourceBalanceAfter { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record WalletDeactivatedResponse
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset DeactivatedAt { get; init; }
}

// --- Shared envelope types ---

public record ApiResponse<T>
{
    public T Data { get; init; } = default!;
    public ApiMeta Meta { get; init; } = new();
}

public record PagedApiResponse<T>
{
    public IEnumerable<T> Data { get; init; } = [];
    public PaginationMeta Pagination { get; init; } = new();
    public ApiMeta Meta { get; init; } = new();
}

public record ApiErrorResponse
{
    public ApiError Error { get; init; } = new();
    public ApiMeta Meta { get; init; } = new();
}

public record ApiError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IEnumerable<FieldError> Details { get; init; } = [];
}

public record FieldError
{
    public string Field { get; init; } = string.Empty;
    public string Issue { get; init; } = string.Empty;
}

public record ApiMeta
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public record PaginationMeta
{
    public string? Cursor { get; init; }
    public bool HasMore { get; init; }
    public int Limit { get; init; }
}

public static class ApiResponse
{
    public static ApiResponse<T> Success<T>(T data) => new() { Data = data };
}
