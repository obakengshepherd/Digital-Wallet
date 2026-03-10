namespace DigitalWallet.Domain.Events;

/// <summary>Base record for all domain events. Published to Kafka after DB commit.</summary>
public abstract record DomainEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public abstract string EventType { get; }
}

/// <summary>Published to kafka topic: wallet.transfers after a successful transfer commit.</summary>
public record TransferCompletedEvent : DomainEvent
{
    public override string EventType => "TransferCompleted";
    public string TransferId { get; init; } = string.Empty;
    public string SourceWalletId { get; init; } = string.Empty;
    public string DestinationWalletId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
}

/// <summary>Published to kafka topic: wallet.deposits after a successful deposit commit.</summary>
public record DepositCompletedEvent : DomainEvent
{
    public override string EventType => "DepositCompleted";
    public string TransactionId { get; init; } = string.Empty;
    public string WalletId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
}

/// <summary>Published when a wallet is deactivated.</summary>
public record WalletDeactivatedEvent : DomainEvent
{
    public override string EventType => "WalletDeactivated";
    public string WalletId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
}
