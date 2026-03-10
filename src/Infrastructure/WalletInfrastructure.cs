namespace DigitalWallet.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL implementation of wallet persistence.
/// Full implementation added Days 9–11.
/// Uses Dapper for raw SQL control over transactions and locking.
/// </summary>
public class WalletRepository
{
    // FindById, FindByUserId, Save, UpdateBalance — implemented Day 9
}

/// <summary>
/// PostgreSQL implementation for transaction and ledger history.
/// </summary>
public class TransactionRepository
{
    // FindByWalletId (paginated), Save, FindByIdempotencyKey — implemented Day 9
}

namespace DigitalWallet.Infrastructure.Cache;

/// <summary>
/// Redis cache adapter for wallet balances.
/// Keys: wallet:{id}:balance — TTL 60s
/// Keys: idempotency:{key} — TTL 86400s
/// </summary>
public class WalletCacheService
{
    // GetBalance, SetBalance, InvalidateBalance, GetIdempotencyResult, SetIdempotencyResult
    // Implemented Day 17
}

namespace DigitalWallet.Infrastructure.Messaging;

/// <summary>
/// Kafka producer for wallet domain events.
/// Topic: wallet.transfers (partitioned by wallet_id)
/// Topic: wallet.deposits
/// Published AFTER database transaction commits — never speculatively.
/// </summary>
public class WalletEventPublisher
{
    // PublishTransferCompleted, PublishDepositCompleted — implemented Day 19
}
