using StackExchange.Redis;

namespace DigitalWallet.Infrastructure.Cache;

/// <summary>
/// Redis cache adapter for wallet balances and idempotency key results.
///
/// Key patterns:
///   wallet:{id}:balance        — cached balance, TTL 60s
///   idempotency:{userId}:{key} — cached response body, TTL 24h
///
/// Design principle: Redis is NOT the source of truth.
/// A Redis failure degrades to slower PostgreSQL reads but never causes
/// incorrect balances or data loss.
/// </summary>
public class WalletCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<WalletCacheService> _logger;

    private static readonly TimeSpan BalanceTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    public WalletCacheService(IConnectionMultiplexer redis, ILogger<WalletCacheService> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
    }

    // ── Balance cache ─────────────────────────────────────────────────────────

    public async Task<decimal?> GetBalanceAsync(string walletId)
    {
        try
        {
            var key = BalanceKey(walletId);
            var value = await _db.StringGetAsync(key);
            if (value.HasValue && decimal.TryParse(value.ToString(), out var balance))
                return balance;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis GetBalance failed for wallet {WalletId} — falling back to DB", walletId);
            return null;
        }
    }

    public async Task SetBalanceAsync(string walletId, decimal balance)
    {
        try
        {
            await _db.StringSetAsync(BalanceKey(walletId), balance.ToString("F4"), BalanceTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis SetBalance failed for wallet {WalletId} — non-fatal", walletId);
        }
    }

    public async Task InvalidateBalanceAsync(string walletId)
    {
        try
        {
            await _db.KeyDeleteAsync(BalanceKey(walletId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis InvalidateBalance failed for wallet {WalletId} — non-fatal", walletId);
        }
    }

    public async Task InvalidateManyAsync(params string[] walletIds)
    {
        try
        {
            var keys = walletIds.Select(id => (RedisKey)BalanceKey(id)).ToArray();
            await _db.KeyDeleteAsync(keys);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis batch invalidation failed — non-fatal");
        }
    }

    // ── Idempotency cache ─────────────────────────────────────────────────────

    public async Task<string?> GetIdempotencyResultAsync(string userId, string idempotencyKey)
    {
        try
        {
            var value = await _db.StringGetAsync(IdempotencyKey(userId, idempotencyKey));
            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis GetIdempotencyResult failed — non-fatal");
            return null;
        }
    }

    public async Task SetIdempotencyResultAsync(string userId, string idempotencyKey, string serialisedResult)
    {
        try
        {
            await _db.StringSetAsync(
                IdempotencyKey(userId, idempotencyKey),
                serialisedResult,
                IdempotencyTtl,
                When.NotExists); // NX — first write wins
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis SetIdempotencyResult failed — non-fatal");
        }
    }

    // ── Key builders ──────────────────────────────────────────────────────────

    private static string BalanceKey(string walletId) => $"wallet:{walletId}:balance";
    private static string IdempotencyKey(string userId, string key) => $"idempotency:{userId}:{key}";
}
