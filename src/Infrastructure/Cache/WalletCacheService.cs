using StackExchange.Redis;

namespace DigitalWallet.Infrastructure.Cache;

/// <summary>
/// Extended Redis cache service for the Digital Wallet system.
/// Adds cache stampede protection on top of the Phase 4 base implementation.
///
/// Cache stampede (thundering herd): when a heavily-read cache key expires,
/// all concurrent readers experience a miss simultaneously, flood the database
/// with the same query, and all write the same result back to cache.
/// At scale this can cause a brief but severe DB spike.
///
/// Prevention: distributed mutex using Redis SET NX (only one reader rebuilds).
/// </summary>
public class WalletCacheServiceV2 : WalletCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<WalletCacheServiceV2> _logger;
    private static readonly TimeSpan MutexTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BalanceTtl = TimeSpan.FromSeconds(60);

    public WalletCacheServiceV2(
        IConnectionMultiplexer redis,
        ILogger<WalletCacheServiceV2> logger)
        : base(redis, logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
    }

    /// <summary>
    /// Cache-aside read with stampede protection.
    /// On cache miss, acquires a short-lived mutex so only ONE caller
    /// rebuilds the cache. Other concurrent callers either:
    ///   a) Wait briefly and get the newly cached value, or
    ///   b) Fall through to DB if the mutex holder hasn't finished (graceful)
    ///
    /// The mutex is a best-effort optimisation — correctness is never compromised.
    /// </summary>
    public async Task<decimal?> GetBalanceWithStampedeProtectionAsync(
        string walletId,
        Func<Task<decimal?>> dbFallback)
    {
        // Fast path: cache hit
        var cached = await GetBalanceAsync(walletId);
        if (cached.HasValue) return cached;

        // Cache miss: try to acquire the rebuild mutex
        var mutexKey = $"mutex:balance:{walletId}";
        var acquired = await _db.StringSetAsync(mutexKey, "1", MutexTtl, When.NotExists);

        if (acquired)
        {
            // This caller holds the mutex — rebuild the cache
            try
            {
                var balance = await dbFallback();
                if (balance.HasValue)
                    await SetBalanceAsync(walletId, balance.Value);
                return balance;
            }
            finally
            {
                await _db.KeyDeleteAsync(mutexKey);
            }
        }
        else
        {
            // Another caller is rebuilding — wait briefly then re-check cache
            await Task.Delay(50);
            var retried = await GetBalanceAsync(walletId);
            if (retried.HasValue) return retried;

            // If still not populated (mutex holder slow), fall through to DB
            _logger.LogDebug("Stampede protection: mutex not acquired for wallet {WalletId}, reading from DB", walletId);
            return await dbFallback();
        }
    }

    // ── Queue depth counter (used by monitoring) ──────────────────────────────

    public async Task<long> IncrementTransferCounterAsync(string period)
    {
        try
        {
            var key = $"counter:transfers:{period}";
            var count = await _db.StringIncrementAsync(key);
            if (count == 1) await _db.KeyExpireAsync(key, TimeSpan.FromHours(25)); // outlive the period
            return count;
        }
        catch { return 0; }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// KAFKA CONSUMER — Fraud Decision Consumer for Digital Wallet
// Consumes fraud.decisions events and updates transfer alert flags
// ════════════════════════════════════════════════════════════════════════════

using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Npgsql;

namespace DigitalWallet.Infrastructure.Messaging;

/// <summary>
/// Kafka consumer for fraud.decisions topic.
/// When fraud engine emits a BLOCK decision, this consumer flags the
/// corresponding transfer in the wallet system for analyst review.
///
/// Consumer group: wallet-fraud-consumer
/// Topic: fraud.decisions (partitioned by user_id)
/// </summary>
public class FraudDecisionConsumer : IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly string _connectionString;
    private readonly ILogger<FraudDecisionConsumer> _logger;
    private readonly IProducer<string, string> _dlqProducer;
    private const string Topic    = "fraud.decisions";
    private const string DlqTopic = "wallet.transfers.dlq";
    private const int MaxRetries  = 3;

    public FraudDecisionConsumer(IConfiguration configuration, ILogger<FraudDecisionConsumer> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");

        _consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers       = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            GroupId                = "wallet-fraud-consumer",
            AutoOffsetReset        = AutoOffsetReset.Earliest,
            EnableAutoCommit       = false,       // Manual commit — commit AFTER processing
            EnableAutoOffsetStore  = false,
            IsolationLevel         = IsolationLevel.ReadCommitted,
            MaxPollIntervalMs      = 300000,
            SessionTimeoutMs       = 30000
        }).Build();

        _dlqProducer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            Acks             = Acks.All
        }).Build();
    }

    public async Task ConsumeAsync(CancellationToken ct)
    {
        _consumer.Subscribe(Topic);
        _logger.LogInformation("FraudDecisionConsumer subscribed to {Topic}", Topic);

        while (!ct.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result = null;
            try
            {
                result = _consumer.Consume(TimeSpan.FromSeconds(1));
                if (result is null || result.IsPartitionEOF) continue;

                await ProcessWithRetryAsync(result, ct);

                // Commit offset ONLY after successful processing
                _consumer.StoreOffset(result);
                _consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error on {Topic}", Topic);
                await Task.Delay(1000, ct);
            }
            catch (Exception ex) when (result is not null)
            {
                _logger.LogError(ex, "Processing failed for message at {Partition}@{Offset} — sending to DLQ",
                    result.Partition, result.Offset);
                await SendToDlqAsync(result, ex.Message);
                _consumer.StoreOffset(result);
                _consumer.Commit(result);
            }
        }

        _consumer.Close();
    }

    private async Task ProcessWithRetryAsync(ConsumeResult<string, string> result, CancellationToken ct)
    {
        var lastEx = (Exception?)null;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                await HandleFraudDecisionAsync(result.Message.Value);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < MaxRetries - 1)
                {
                    _logger.LogWarning(ex, "Retry {Attempt}/{Max} for fraud decision message", attempt + 1, MaxRetries);
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100), ct);
                }
            }
        }
        throw lastEx!;
    }

    private async Task HandleFraudDecisionAsync(string messageValue)
    {
        var decision = JsonSerializer.Deserialize<FraudDecisionMessage>(messageValue);
        if (decision is null) return;

        if (decision.Decision == "BLOCK")
        {
            _logger.LogWarning("Fraud BLOCK decision for transaction {TxnId}", decision.TransactionId);
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            // Flag the associated transfer for analyst review
            // In production this would update a fraud_flags table or send an alert
            await conn.ExecuteAsync("""
                UPDATE transfer_requests
                SET status = 'fraud_flagged'
                WHERE id = (
                    SELECT id FROM transfer_requests
                    WHERE idempotency_key = @TransactionId
                    LIMIT 1
                )
                """, new { TransactionId = decision.TransactionId });
        }

        _logger.LogInformation("Processed FraudDecision: txn={TxnId} decision={Decision} score={Score}",
            decision.TransactionId, decision.Decision, decision.RiskScore);
    }

    private async Task SendToDlqAsync(ConsumeResult<string, string> result, string errorReason)
    {
        try
        {
            var dlqPayload = JsonSerializer.Serialize(new
            {
                OriginalTopic     = result.Topic,
                OriginalPartition = result.Partition.Value,
                OriginalOffset    = result.Offset.Value,
                OriginalKey       = result.Message.Key,
                OriginalValue     = result.Message.Value,
                ErrorReason       = errorReason,
                FailedAt          = DateTimeOffset.UtcNow
            });

            await _dlqProducer.ProduceAsync(DlqTopic, new Message<string, string>
            {
                Key   = result.Message.Key,
                Value = dlqPayload
            });

            _logger.LogWarning("Message sent to DLQ {DlqTopic}", DlqTopic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to DLQ — message may be lost");
        }
    }

    public void Dispose() { _consumer?.Dispose(); _dlqProducer?.Dispose(); }

    private record FraudDecisionMessage(string TransactionId, string Decision, int RiskScore, DateTimeOffset EvaluatedAt);
}

/// <summary>
/// Hosted background service that wraps FraudDecisionConsumer.
/// Register in Program.cs: builder.Services.AddHostedService<FraudDecisionConsumerWorker>();
/// </summary>
public class FraudDecisionConsumerWorker : BackgroundService
{
    private readonly FraudDecisionConsumer _consumer;
    private readonly ILogger<FraudDecisionConsumerWorker> _logger;

    public FraudDecisionConsumerWorker(FraudDecisionConsumer consumer, ILogger<FraudDecisionConsumerWorker> logger)
    {
        _consumer = consumer;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FraudDecisionConsumerWorker starting");
        await _consumer.ConsumeAsync(stoppingToken);
        _logger.LogInformation("FraudDecisionConsumerWorker stopping");
    }
}
