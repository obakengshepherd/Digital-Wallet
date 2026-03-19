# Event Schema & Topic Design — Digital Wallet System

---

## Topics

### `wallet.transfers`
- **Producer:** TransferService (after DB commit)
- **Partitioned by:** `source_wallet_id` — preserves per-wallet event ordering
- **Consumers:** FraudDetectionConsumer (group: `fraud-engine`), NotificationConsumer
- **Retention:** 7 days
- **Partitions:** 8

**Message schema:**
```json
{
  "event_id": "uuid",
  "event_type": "TransferCompleted",
  "occurred_at": "2024-01-15T10:32:00Z",
  "transfer_id": "trf_01j9...",
  "source_wallet_id": "wlt_01j9...",
  "destination_wallet_id": "wlt_99z9...",
  "amount": "100.00",
  "currency": "ZAR",
  "user_id": "usr_abc123"
}
```

### `wallet.deposits`
- **Producer:** WalletService (after DB commit)
- **Partitioned by:** `wallet_id`
- **Consumers:** NotificationConsumer
- **Retention:** 7 days
- **Partitions:** 4

**Message schema:**
```json
{
  "event_id": "uuid",
  "event_type": "DepositCompleted",
  "occurred_at": "2024-01-15T10:31:00Z",
  "transaction_id": "txn_01j9...",
  "wallet_id": "wlt_01j9...",
  "amount": "250.00",
  "currency": "ZAR"
}
```

### `wallet.transfers.dlq` (Dead Letter Queue)
- Receives messages that failed processing after all retries
- Partitions: 1
- Retention: 30 days (for manual inspection and replay)

---

## Consumer Groups

| Consumer Group     | Topic             | Processing                                       |
|--------------------|-------------------|--------------------------------------------------|
| `fraud-engine`     | wallet.transfers  | Evaluate every transfer for fraud signals        |
| `notifications`    | wallet.transfers  | Trigger transfer confirmation notification       |
| `notifications`    | wallet.deposits   | Trigger deposit confirmation notification        |

Each group tracks its own offset independently. Adding a new consumer group does
not affect existing consumers' progress.

---

## What Happens When a Consumer Is Slow

Consumer group lag accumulates in the `wallet.transfers` topic. Kafka retains messages
for 7 days regardless of consumer speed — messages are never lost due to consumer
slowness alone.

**Alert threshold:** Lag > 10,000 messages on any partition → page on-call.
**Action:** Add consumer instances up to the partition count (8 max for this topic).

A slow fraud consumer does not block transfers — producers never wait for consumers.
The risk is that fraud signals are delayed, meaning recently-transferred funds might
not be reviewed before a follow-on action. This is an accepted tradeoff: blocking
the payment pipeline waiting for fraud is worse than delayed fraud detection.

---

## Producer Configuration

```csharp
Acks = Acks.All               // Wait for all in-sync replicas
EnableIdempotence = true       // Exactly-once producer delivery
MessageSendMaxRetries = 3
RetryBackoffMs = 100
```

`EnableIdempotence = true` combined with `Acks.All` provides exactly-once producer
semantics — a producer crash and retry will not create duplicate messages on the
broker side.
