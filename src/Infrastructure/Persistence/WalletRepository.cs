using Dapper;
using DigitalWallet.Domain.Entities;
using Npgsql;

namespace DigitalWallet.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL repository for wallet and ledger operations.
/// Uses Dapper for raw SQL — gives full control over the locking and
/// transaction semantics that financial operations require.
/// </summary>
public class WalletRepository
{
    private readonly string _connectionString;

    public WalletRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    // ── Read operations ───────────────────────────────────────────────────────

    public async Task<Wallet?> FindByIdAsync(string walletId, NpgsqlConnection? conn = null)
    {
        using var connection = conn ?? CreateConnection();
        const string sql = """
            SELECT id, user_id, currency, balance, status, version,
                   created_at, updated_at
            FROM wallets
            WHERE id = @WalletId
            """;
        return await connection.QuerySingleOrDefaultAsync<Wallet>(sql, new { WalletId = walletId });
    }

    public async Task<Wallet?> FindByIdForUpdateAsync(string walletId, NpgsqlConnection conn)
    {
        // Acquires a row-level lock. Must be called inside an open transaction.
        // Used by TransferService to lock both wallets atomically.
        const string sql = """
            SELECT id, user_id, currency, balance, status, version,
                   created_at, updated_at
            FROM wallets
            WHERE id = @WalletId
            FOR UPDATE
            """;
        return await conn.QuerySingleOrDefaultAsync<Wallet>(sql, new { WalletId = walletId });
    }

    public async Task<Wallet?> FindByUserAndCurrencyAsync(string userId, string currency)
    {
        using var connection = CreateConnection();
        const string sql = """
            SELECT id, user_id, currency, balance, status, version,
                   created_at, updated_at
            FROM wallets
            WHERE user_id = @UserId AND currency = @Currency
            """;
        return await connection.QuerySingleOrDefaultAsync<Wallet>(
            sql, new { UserId = userId, Currency = currency });
    }

    // ── Write operations ──────────────────────────────────────────────────────

    public async Task InsertAsync(Wallet wallet, NpgsqlConnection? conn = null)
    {
        using var connection = conn ?? CreateConnection();
        const string sql = """
            INSERT INTO wallets (id, user_id, currency, balance, status, version, created_at, updated_at)
            VALUES (@Id, @UserId, @Currency, @Balance, @Status::wallet_status, @Version, @CreatedAt, @UpdatedAt)
            """;
        await connection.ExecuteAsync(sql, new
        {
            wallet.Id, wallet.UserId, wallet.Currency,
            wallet.Balance,
            Status = wallet.Status.ToString().ToLower(),
            wallet.Version, wallet.CreatedAt, wallet.UpdatedAt
        });
    }

    /// <summary>
    /// Applies a balance change using optimistic locking.
    /// Returns false if the version check fails (concurrent modification).
    /// </summary>
    public async Task<bool> UpdateBalanceAsync(
        string walletId,
        decimal newBalance,
        int expectedVersion,
        NpgsqlConnection conn)
    {
        const string sql = """
            UPDATE wallets
            SET balance    = @NewBalance,
                version    = version + 1,
                updated_at = NOW()
            WHERE id      = @WalletId
              AND version = @ExpectedVersion
            """;
        var rows = await conn.ExecuteAsync(sql,
            new { WalletId = walletId, NewBalance = newBalance, ExpectedVersion = expectedVersion });
        return rows == 1;
    }

    public async Task UpdateStatusAsync(string walletId, string status)
    {
        using var connection = CreateConnection();
        const string sql = """
            UPDATE wallets
            SET status     = @Status::wallet_status,
                updated_at = NOW()
            WHERE id = @WalletId
            """;
        await connection.ExecuteAsync(sql, new { WalletId = walletId, Status = status });
    }
}

/// <summary>
/// PostgreSQL repository for transactions and ledger entries.
/// Transactions are append-only — no UPDATE or DELETE operations.
/// </summary>
public class TransactionRepository
{
    private readonly string _connectionString;

    public TransactionRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<Transaction?> FindByReferenceIdAsync(string referenceId)
    {
        using var connection = CreateConnection();
        const string sql = """
            SELECT id, wallet_id, type, amount, balance_after,
                   reference_id, status, created_at
            FROM transactions
            WHERE reference_id = @ReferenceId
            """;
        return await connection.QuerySingleOrDefaultAsync<Transaction>(
            sql, new { ReferenceId = referenceId });
    }

    public async Task InsertTransactionAsync(Transaction transaction, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO transactions (id, wallet_id, type, amount, reference_id, status, created_at)
            VALUES (@Id, @WalletId, @Type::txn_type, @Amount, @ReferenceId, @Status::txn_status, @CreatedAt)
            """;
        await conn.ExecuteAsync(sql, new
        {
            transaction.Id, transaction.WalletId,
            Type = transaction.Type.ToString().ToLower(),
            transaction.Amount, transaction.ReferenceId,
            Status = transaction.Status.ToString().ToLower(),
            transaction.CreatedAt
        });
    }

    public async Task InsertLedgerEntryAsync(LedgerEntry entry, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO ledger_entries (id, transaction_id, wallet_id, direction, amount, running_balance, recorded_at)
            VALUES (@Id, @TransactionId, @WalletId, @Direction::ledger_dir, @Amount, @RunningBalance, @RecordedAt)
            """;
        await conn.ExecuteAsync(sql, new
        {
            entry.Id, entry.TransactionId, entry.WalletId,
            Direction = entry.Direction.ToString().ToLower(),
            entry.Amount, entry.RunningBalance, entry.RecordedAt
        });
    }

    public async Task InsertTransferRequestAsync(TransferRequest request, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO transfer_requests
                (id, source_wallet_id, destination_wallet_id, amount, idempotency_key, status, created_at)
            VALUES
                (@Id, @SourceWalletId, @DestinationWalletId, @Amount, @IdempotencyKey, @Status::transfer_status, @CreatedAt)
            """;
        await conn.ExecuteAsync(sql, new
        {
            request.Id, request.SourceWalletId, request.DestinationWalletId,
            request.Amount, request.IdempotencyKey,
            Status = request.Status.ToString().ToLower(),
            request.CreatedAt
        });
    }

    public async Task<TransferRequest?> FindTransferByIdempotencyKeyAsync(string idempotencyKey)
    {
        using var connection = CreateConnection();
        const string sql = """
            SELECT id, source_wallet_id, destination_wallet_id, amount,
                   idempotency_key, status, created_at
            FROM transfer_requests
            WHERE idempotency_key = @Key
            """;
        return await connection.QuerySingleOrDefaultAsync<TransferRequest>(
            sql, new { Key = idempotencyKey });
    }

    public async Task<(IEnumerable<Transaction> Items, string? NextCursor)> GetTransactionPageAsync(
        string walletId,
        int limit,
        string? cursor,
        string? typeFilter)
    {
        using var connection = CreateConnection();

        // Decode cursor — it encodes the last-seen created_at + id
        DateTimeOffset? cursorTime = null;
        string? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split('|');
            if (parts.Length == 2)
            {
                cursorTime = DateTimeOffset.Parse(parts[0]);
                cursorId = parts[1];
            }
        }

        var typeClause = typeFilter?.ToLower() switch
        {
            "credit" => "AND type = 'credit'::txn_type",
            "debit"  => "AND type = 'debit'::txn_type",
            _        => string.Empty
        };

        var cursorClause = cursorTime.HasValue
            ? "AND (created_at, id) < (@CursorTime, @CursorId)"
            : string.Empty;

        var sql = $"""
            SELECT id, wallet_id, type, amount, reference_id, status, created_at
            FROM transactions
            WHERE wallet_id = @WalletId
              {typeClause}
              {cursorClause}
            ORDER BY created_at DESC, id DESC
            LIMIT @Limit
            """;

        var items = (await connection.QueryAsync<Transaction>(sql, new
        {
            WalletId = walletId,
            Limit = limit + 1,
            CursorTime = cursorTime,
            CursorId = cursorId
        })).ToList();

        var hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            var raw = $"{last.CreatedAt:O}|{last.Id}";
            nextCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        }

        return (items, nextCursor);
    }

    public async Task<decimal> ComputeLedgerSumAsync(string walletId)
    {
        using var connection = CreateConnection();
        const string sql = """
            SELECT COALESCE(
                SUM(CASE WHEN direction = 'credit' THEN amount ELSE -amount END),
                0
            )
            FROM ledger_entries
            WHERE wallet_id = @WalletId
            """;
        return await connection.QuerySingleAsync<decimal>(sql, new { WalletId = walletId });
    }
}
