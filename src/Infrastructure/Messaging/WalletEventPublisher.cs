using System.Text.Json;
using Confluent.Kafka;
using DigitalWallet.Domain.Events;

namespace DigitalWallet.Infrastructure.Messaging;

/// <summary>
/// Kafka producer for wallet domain events.
///
/// Topics:
///   wallet.transfers  — partitioned by source_wallet_id → preserves per-wallet ordering
///   wallet.deposits   — partitioned by wallet_id
///
/// CRITICAL: Events are published AFTER the database transaction commits.
/// Never publish speculatively (before commit) — a commit failure would leave
/// a published event with no corresponding database record.
/// </summary>
public class WalletEventPublisher : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<WalletEventPublisher> _logger;

    private const string TransfersTopic = "wallet.transfers";
    private const string DepositsTopic  = "wallet.deposits";

    public WalletEventPublisher(IConfiguration configuration, ILogger<WalletEventPublisher> logger)
    {
        _logger = logger;
        var config = new ProducerConfig
        {
            BootstrapServers    = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            Acks                = Acks.All,        // Wait for all replicas to acknowledge
            EnableIdempotence   = true,            // Exactly-once producer semantics
            MessageSendMaxRetries = 3,
            RetryBackoffMs      = 100
        };
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task PublishTransferCompletedAsync(TransferCompletedEvent @event)
    {
        var message = new Message<string, string>
        {
            Key   = @event.SourceWalletId, // partition by source wallet
            Value = JsonSerializer.Serialize(@event)
        };

        try
        {
            var result = await _producer.ProduceAsync(TransfersTopic, message);
            _logger.LogInformation(
                "Published {EventType} to {Topic}/{Partition}@{Offset}",
                @event.EventType, TransfersTopic, result.Partition, result.Offset);
        }
        catch (ProduceException<string, string> ex)
        {
            // Log and continue — the DB has already committed.
            // A separate outbox relay can retry failed publishes.
            _logger.LogError(ex,
                "Failed to publish TransferCompleted for transfer {TransferId}",
                @event.TransferId);
            throw;
        }
    }

    public async Task PublishDepositCompletedAsync(DepositCompletedEvent @event)
    {
        var message = new Message<string, string>
        {
            Key   = @event.WalletId,
            Value = JsonSerializer.Serialize(@event)
        };

        try
        {
            await _producer.ProduceAsync(DepositsTopic, message);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex,
                "Failed to publish DepositCompleted for transaction {TransactionId}",
                @event.TransactionId);
            throw;
        }
    }

    public void Dispose() => _producer?.Dispose();
}
