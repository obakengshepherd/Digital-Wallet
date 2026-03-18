using System.Text.Json;
using DigitalWallet.Api.Models.Requests;
using DigitalWallet.Api.Models.Responses;
using DigitalWallet.Application.Interfaces;
using DigitalWallet.Domain.Entities;
using DigitalWallet.Domain.Events;
using DigitalWallet.Infrastructure.Cache;
using DigitalWallet.Infrastructure.Messaging;
using DigitalWallet.Infrastructure.Persistence;

namespace DigitalWallet.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// WALLET SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class WalletService : IWalletService
{
    private readonly WalletRepository _walletRepo;
    private readonly TransactionRepository _txnRepo;
    private readonly WalletCacheService _cache;
    private readonly WalletEventPublisher _publisher;
    private readonly ILogger<WalletService> _logger;

    public WalletService(
        WalletRepository walletRepo,
        TransactionRepository txnRepo,
        WalletCacheService cache,
        WalletEventPublisher publisher,
        ILogger<WalletService> logger)
    {
        _walletRepo = walletRepo;
        _txnRepo    = txnRepo;
        _cache      = cache;
        _publisher  = publisher;
        _logger     = logger;
    }

    // ── CreateWallet ──────────────────────────────────────────────────────────

    public async Task<WalletResponse> CreateWalletAsync(
        string userId,
        CreateWalletRequest request,
        CancellationToken cancellationToken)
    {
        // Validate: user must not already have a wallet in this currency
        var existing = await _walletRepo.FindByUserAndCurrencyAsync(userId, request.Currency);
        if (existing is not null)
            throw new DuplicateWalletException(userId, request.Currency);

        var wallet = Wallet.Create(userId, request.Currency);
        await _walletRepo.InsertAsync(wallet);

        _logger.LogInformation("Created wallet {WalletId} for user {UserId}", wallet.Id, userId);
        return MapWallet(wallet);
    }

    // ── GetWallet ─────────────────────────────────────────────────────────────

    public async Task<WalletResponse> GetWalletAsync(
        string walletId,
        string requestingUserId,
        CancellationToken cancellationToken)
    {
        var wallet = await _walletRepo.FindByIdAsync(walletId)
            ?? throw new WalletNotFoundException(walletId);

        if (wallet.UserId != requestingUserId)
            throw new WalletAccessDeniedException(walletId);

        // Check Redis cache for balance — avoids a DB read on the hot path
        var cachedBalance = await _cache.GetBalanceAsync(walletId);
        if (cachedBalance.HasValue)
        {
            return MapWallet(wallet) with { Balance = cachedBalance.Value };
        }

        // Cache miss — balance is already on the wallet object from the DB read
        await _cache.SetBalanceAsync(walletId, wallet.Balance);
        return MapWallet(wallet);
    }

    // ── Deposit ───────────────────────────────────────────────────────────────

    public async Task<TransactionResponse> DepositAsync(
        string walletId,
        string userId,
        string idempotencyKey,
        DepositRequest request,
        CancellationToken cancellationToken)
    {
        // Fast-path idempotency check (Redis)
        var cached = await _cache.GetIdempotencyResultAsync(userId, idempotencyKey);
        if (cached is not null)
        {
            _logger.LogInformation("Returning cached deposit result for idempotency key {Key}", idempotencyKey);
            return JsonSerializer.Deserialize<TransactionResponse>(cached)!;
        }

        // DB-level idempotency check (fallback when Redis is cold)
        var existingTxn = await _txnRepo.FindByReferenceIdAsync(idempotencyKey);
        if (existingTxn is not null)
            return MapTransaction(existingTxn);

        var wallet = await _walletRepo.FindByIdAsync(walletId)
            ?? throw new WalletNotFoundException(walletId);

        if (wallet.UserId != userId)
            throw new WalletAccessDeniedException(walletId);

        if (wallet.Status != WalletStatus.Active)
            throw new WalletInactiveException(walletId);

        // All reads and writes inside one DB transaction
        await using var connection = _walletRepo.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var dbTx = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var newBalance  = wallet.Balance + request.Amount;
            var transaction = Transaction.CreateCredit(walletId, request.Amount, idempotencyKey, newBalance);
            var ledgerEntry = LedgerEntry.Create(transaction.Id, walletId, LedgerDirection.Credit, request.Amount, newBalance);

            await _txnRepo.InsertTransactionAsync(transaction, connection);
            await _txnRepo.InsertLedgerEntryAsync(ledgerEntry, connection);

            var updated = await _walletRepo.UpdateBalanceAsync(walletId, newBalance, wallet.Version, connection);
            if (!updated)
                throw new ConcurrentModificationException(walletId);

            await dbTx.CommitAsync(cancellationToken);

            // Post-commit: invalidate cache and publish event
            await _cache.InvalidateBalanceAsync(walletId);

            _ = _publisher.PublishDepositCompletedAsync(new DepositCompletedEvent
            {
                TransactionId = transaction.Id,
                WalletId      = walletId,
                Amount        = request.Amount,
                Currency      = wallet.Currency
            });

            var response = MapTransaction(transaction);
            await _cache.SetIdempotencyResultAsync(userId, idempotencyKey, JsonSerializer.Serialize(response));
            return response;
        }
        catch
        {
            await dbTx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ── DeactivateWallet ──────────────────────────────────────────────────────

    public async Task<WalletDeactivatedResponse> DeactivateWalletAsync(
        string walletId,
        string userId,
        CancellationToken cancellationToken)
    {
        var wallet = await _walletRepo.FindByIdAsync(walletId)
            ?? throw new WalletNotFoundException(walletId);

        if (wallet.UserId != userId) throw new WalletAccessDeniedException(walletId);
        if (wallet.Status == WalletStatus.Inactive) throw new WalletAlreadyInactiveException(walletId);
        if (wallet.Balance != 0m) throw new WalletBalanceNotZeroException(walletId);

        await _walletRepo.UpdateStatusAsync(walletId, "inactive");
        await _cache.InvalidateBalanceAsync(walletId);

        return new WalletDeactivatedResponse
        {
            Id            = walletId,
            Status        = "INACTIVE",
            DeactivatedAt = DateTimeOffset.UtcNow
        };
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static WalletResponse MapWallet(Wallet w) => new()
    {
        Id        = w.Id,
        UserId    = w.UserId,
        Currency  = w.Currency,
        Balance   = w.Balance,
        Status    = w.Status.ToString().ToUpper(),
        CreatedAt = w.CreatedAt,
        UpdatedAt = w.UpdatedAt
    };

    private static TransactionResponse MapTransaction(Transaction t) => new()
    {
        Id          = t.Id,
        WalletId    = t.WalletId,
        Type        = t.Type.ToString().ToUpper(),
        Amount      = t.Amount,
        BalanceAfter= t.BalanceAfter,
        ReferenceId = t.ReferenceId,
        Status      = t.Status.ToString().ToUpper(),
        CreatedAt   = t.CreatedAt
    };
}

// ════════════════════════════════════════════════════════════════════════════
// TRANSFER SERVICE — The critical path
// ════════════════════════════════════════════════════════════════════════════

public class TransferService : ITransferService
{
    private readonly WalletRepository _walletRepo;
    private readonly TransactionRepository _txnRepo;
    private readonly WalletCacheService _cache;
    private readonly WalletEventPublisher _publisher;
    private readonly ILogger<TransferService> _logger;

    public TransferService(
        WalletRepository walletRepo,
        TransactionRepository txnRepo,
        WalletCacheService cache,
        WalletEventPublisher publisher,
        ILogger<TransferService> logger)
    {
        _walletRepo = walletRepo;
        _txnRepo    = txnRepo;
        _cache      = cache;
        _publisher  = publisher;
        _logger     = logger;
    }

    public async Task<TransferResponse> TransferAsync(
        string requestingUserId,
        string idempotencyKey,
        Api.Models.Requests.TransferRequest request,
        CancellationToken cancellationToken)
    {
        // ── Step 1: Idempotency check ─────────────────────────────────────────
        // Check the transfer_requests table — the source of truth for idempotency.
        // If this key was already processed (even partially), return the original result.
        var existingTransfer = await _txnRepo.FindTransferByIdempotencyKeyAsync(idempotencyKey);
        if (existingTransfer is not null)
        {
            _logger.LogInformation("Idempotent transfer: returning existing result for key {Key}", idempotencyKey);
            return new TransferResponse
            {
                TransferId             = existingTransfer.Id,
                SourceWalletId         = existingTransfer.SourceWalletId,
                DestinationWalletId    = existingTransfer.DestinationWalletId,
                Amount                 = existingTransfer.Amount,
                Status                 = existingTransfer.Status.ToString().ToUpper(),
                SourceBalanceAfter     = 0m, // historical result — balance may have changed
                CreatedAt              = existingTransfer.CreatedAt
            };
        }

        // ── Step 2: Validate source and destination ───────────────────────────
        // These reads are pre-lock validation — we re-read inside the lock below.
        var source = await _walletRepo.FindByIdAsync(request.SourceWalletId)
            ?? throw new WalletNotFoundException(request.SourceWalletId);

        if (source.UserId != requestingUserId)
            throw new WalletAccessDeniedException(request.SourceWalletId);

        if (source.Status != WalletStatus.Active)
            throw new WalletInactiveException(request.SourceWalletId);

        var destination = await _walletRepo.FindByIdAsync(request.DestinationWalletId)
            ?? throw new WalletNotFoundException(request.DestinationWalletId);

        if (destination.Status != WalletStatus.Active)
            throw new WalletInactiveException(request.DestinationWalletId);

        if (source.Balance < request.Amount)
            throw new InsufficientFundsException(request.SourceWalletId, source.Balance, request.Amount);

        // ── Step 3 & 4: Lock + Execute inside a single DB transaction ─────────
        //
        // DEADLOCK PREVENTION: Always lock wallets in a consistent order.
        // If Thread A locks wlt_001 then wlt_002, and Thread B locks wlt_002 then wlt_001,
        // a deadlock occurs. By always locking the lexicographically lower ID first,
        // both threads acquire locks in the same order, making deadlock impossible.
        //
        // We sort IDs lexicographically and lock in that order.
        var (firstId, secondId) = string.Compare(
            request.SourceWalletId,
            request.DestinationWalletId,
            StringComparison.Ordinal) < 0
            ? (request.SourceWalletId, request.DestinationWalletId)
            : (request.DestinationWalletId, request.SourceWalletId);

        await using var connection = _walletRepo.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var dbTx = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            // Acquire row-level locks in consistent order
            var first  = await _walletRepo.FindByIdForUpdateAsync(firstId, connection);
            var second = await _walletRepo.FindByIdForUpdateAsync(secondId, connection);

            // Re-map to source/destination after locking
            var lockedSource = first?.Id == request.SourceWalletId ? first : second;
            var lockedDest   = first?.Id == request.DestinationWalletId ? first : second;

            if (lockedSource is null) throw new WalletNotFoundException(request.SourceWalletId);
            if (lockedDest is null)   throw new WalletNotFoundException(request.DestinationWalletId);

            // Re-validate balance inside the lock (balance may have changed since pre-check)
            if (lockedSource.Balance < request.Amount)
                throw new InsufficientFundsException(request.SourceWalletId, lockedSource.Balance, request.Amount);

            var sourceNewBalance = lockedSource.Balance - request.Amount;
            var destNewBalance   = lockedDest.Balance   + request.Amount;

            // Create transaction and ledger records for both sides
            var debitTxn  = Transaction.CreateDebit(request.SourceWalletId, request.Amount, idempotencyKey, sourceNewBalance);
            var creditTxn = Transaction.CreateCredit(request.DestinationWalletId, request.Amount, idempotencyKey + "_dst", destNewBalance);

            var debitLedger  = LedgerEntry.Create(debitTxn.Id,  request.SourceWalletId,      LedgerDirection.Debit,  request.Amount, sourceNewBalance);
            var creditLedger = LedgerEntry.Create(creditTxn.Id, request.DestinationWalletId, LedgerDirection.Credit, request.Amount, destNewBalance);

            var transferId = $"trf_{Guid.NewGuid():N}";
            var transferRequest = new TransferRequest
            {
                Id                   = transferId,
                SourceWalletId       = request.SourceWalletId,
                DestinationWalletId  = request.DestinationWalletId,
                Amount               = request.Amount,
                IdempotencyKey       = idempotencyKey,
                Status               = TransferStatus.Completed,
                CreatedAt            = DateTimeOffset.UtcNow
            };

            // All writes in one atomic transaction
            await _txnRepo.InsertTransactionAsync(debitTxn,   connection);
            await _txnRepo.InsertTransactionAsync(creditTxn,  connection);
            await _txnRepo.InsertLedgerEntryAsync(debitLedger,  connection);
            await _txnRepo.InsertLedgerEntryAsync(creditLedger, connection);
            await _txnRepo.InsertTransferRequestAsync(transferRequest, connection);

            var srcUpdated = await _walletRepo.UpdateBalanceAsync(request.SourceWalletId,      sourceNewBalance, lockedSource.Version, connection);
            var dstUpdated = await _walletRepo.UpdateBalanceAsync(request.DestinationWalletId, destNewBalance,   lockedDest.Version,   connection);

            if (!srcUpdated || !dstUpdated)
                throw new ConcurrentModificationException(request.SourceWalletId);

            // ── Step 5: Commit, then publish ──────────────────────────────────
            await dbTx.CommitAsync(cancellationToken);

            // ── Step 6: Post-commit side effects ──────────────────────────────
            // Invalidate both wallet balance caches
            await _cache.InvalidateManyAsync(request.SourceWalletId, request.DestinationWalletId);

            // Fire-and-forget Kafka publish — DB has committed, event will be replayed on failure
            _ = _publisher.PublishTransferCompletedAsync(new TransferCompletedEvent
            {
                TransferId           = transferId,
                SourceWalletId       = request.SourceWalletId,
                DestinationWalletId  = request.DestinationWalletId,
                Amount               = request.Amount,
                Currency             = lockedSource.Currency,
                UserId               = requestingUserId
            });

            _logger.LogInformation(
                "Transfer {TransferId} completed: {Amount} from {Source} to {Dest}",
                transferId, request.Amount, request.SourceWalletId, request.DestinationWalletId);

            return new TransferResponse
            {
                TransferId             = transferId,
                SourceWalletId         = request.SourceWalletId,
                DestinationWalletId    = request.DestinationWalletId,
                Amount                 = request.Amount,
                Status                 = "COMPLETED",
                SourceBalanceAfter     = sourceNewBalance,
                CreatedAt              = transferRequest.CreatedAt
            };
        }
        catch
        {
            await dbTx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// TRANSACTION SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class TransactionService : ITransactionService
{
    private readonly WalletRepository _walletRepo;
    private readonly TransactionRepository _txnRepo;
    private readonly ILogger<TransactionService> _logger;

    public TransactionService(
        WalletRepository walletRepo,
        TransactionRepository txnRepo,
        ILogger<TransactionService> logger)
    {
        _walletRepo = walletRepo;
        _txnRepo    = txnRepo;
        _logger     = logger;
    }

    public async Task<PagedApiResponse<TransactionResponse>> GetTransactionsAsync(
        string walletId,
        string requestingUserId,
        GetTransactionsRequest query,
        CancellationToken cancellationToken)
    {
        var wallet = await _walletRepo.FindByIdAsync(walletId)
            ?? throw new WalletNotFoundException(walletId);

        if (wallet.UserId != requestingUserId)
            throw new WalletAccessDeniedException(walletId);

        var (items, nextCursor) = await _txnRepo.GetTransactionPageAsync(
            walletId, query.Limit, query.Cursor, query.Type);

        return new PagedApiResponse<TransactionResponse>
        {
            Data = items.Select(t => new TransactionResponse
            {
                Id           = t.Id,
                WalletId     = t.WalletId,
                Type         = t.Type.ToString().ToUpper(),
                Amount       = t.Amount,
                BalanceAfter = t.BalanceAfter,
                ReferenceId  = t.ReferenceId,
                Status       = t.Status.ToString().ToUpper(),
                CreatedAt    = t.CreatedAt
            }),
            Pagination = new PaginationMeta
            {
                Cursor  = nextCursor,
                HasMore = nextCursor is not null,
                Limit   = query.Limit
            }
        };
    }

    /// <summary>
    /// Reconciliation: computes the expected balance from ledger history
    /// and compares to the wallet's stored balance. Discrepancies indicate
    /// a data integrity issue that should trigger an alert.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync(string walletId, CancellationToken cancellationToken)
    {
        var wallet = await _walletRepo.FindByIdAsync(walletId)
            ?? throw new WalletNotFoundException(walletId);

        var ledgerSum = await _txnRepo.ComputeLedgerSumAsync(walletId);
        var discrepancy = wallet.Balance - ledgerSum;

        if (discrepancy != 0m)
        {
            _logger.LogError(
                "RECONCILIATION DISCREPANCY: wallet {WalletId} balance={Balance}, ledgerSum={LedgerSum}, delta={Delta}",
                walletId, wallet.Balance, ledgerSum, discrepancy);
        }

        return new ReconciliationResult
        {
            WalletId         = walletId,
            StoredBalance    = wallet.Balance,
            ComputedBalance  = ledgerSum,
            Discrepancy      = discrepancy,
            IsConsistent     = discrepancy == 0m,
            CheckedAt        = DateTimeOffset.UtcNow
        };
    }
}

// ── Supporting types ──────────────────────────────────────────────────────────

public record ReconciliationResult
{
    public string WalletId        { get; init; } = string.Empty;
    public decimal StoredBalance  { get; init; }
    public decimal ComputedBalance{ get; init; }
    public decimal Discrepancy    { get; init; }
    public bool IsConsistent      { get; init; }
    public DateTimeOffset CheckedAt{ get; init; }
}

// ── Domain entity factory methods (extending Phase 2 stubs) ──────────────────

public static class TransactionFactory
{
    public static Transaction CreateCredit(string walletId, decimal amount, string referenceId, decimal balanceAfter) =>
        new()
        {
            Id          = $"txn_{Guid.NewGuid():N}",
            WalletId    = walletId,
            Type        = TransactionType.Credit,
            Amount      = amount,
            ReferenceId = referenceId,
            BalanceAfter= balanceAfter,
            Status      = TransactionStatus.Completed,
            CreatedAt   = DateTimeOffset.UtcNow
        };

    public static Transaction CreateDebit(string walletId, decimal amount, string referenceId, decimal balanceAfter) =>
        new()
        {
            Id          = $"txn_{Guid.NewGuid():N}",
            WalletId    = walletId,
            Type        = TransactionType.Debit,
            Amount      = amount,
            ReferenceId = referenceId,
            BalanceAfter= balanceAfter,
            Status      = TransactionStatus.Completed,
            CreatedAt   = DateTimeOffset.UtcNow
        };
}

// ── Exception types ───────────────────────────────────────────────────────────

public class ConcurrentModificationException(string walletId)
    : Exception($"Concurrent modification detected on wallet '{walletId}'. Retry the operation.");
