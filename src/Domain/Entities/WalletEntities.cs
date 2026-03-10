namespace DigitalWallet.Domain.Entities;

public class Wallet
{
    public string Id { get; private set; } = string.Empty;
    public string UserId { get; private set; } = string.Empty;
    public string Currency { get; private set; } = string.Empty;
    public decimal Balance { get; private set; }
    public WalletStatus Status { get; private set; }
    public int Version { get; private set; } // optimistic lock
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Wallet() { }

    public static Wallet Create(string userId, string currency)
    {
        return new Wallet
        {
            Id = $"wlt_{Guid.NewGuid():N}",
            UserId = userId,
            Currency = currency,
            Balance = 0m,
            Status = WalletStatus.Active,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    // Domain behaviour methods — implemented Day 11
    public void Credit(decimal amount) => throw new NotImplementedException();
    public void Debit(decimal amount) => throw new NotImplementedException();
    public void Deactivate() => throw new NotImplementedException();
}

public class Transaction
{
    public string Id { get; private set; } = string.Empty;
    public string WalletId { get; private set; } = string.Empty;
    public TransactionType Type { get; private set; }
    public decimal Amount { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public string? ReferenceId { get; private set; }
    public string? Note { get; private set; }
    public TransactionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public class LedgerEntry
{
    public string Id { get; private set; } = string.Empty;
    public string TransactionId { get; private set; } = string.Empty;
    public string WalletId { get; private set; } = string.Empty;
    public LedgerDirection Direction { get; private set; }
    public decimal Amount { get; private set; }
    public decimal RunningBalance { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}

public class TransferRequest
{
    public string Id { get; private set; } = string.Empty;
    public string SourceWalletId { get; private set; } = string.Empty;
    public string DestinationWalletId { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public TransferStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

// Enumerations
public enum WalletStatus { Active, Inactive }
public enum TransactionType { Credit, Debit }
public enum TransactionStatus { Pending, Completed, Failed }
public enum LedgerDirection { Credit, Debit }
public enum TransferStatus { Pending, Completed, Failed }
