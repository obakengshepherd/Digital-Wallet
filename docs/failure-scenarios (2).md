# Failure Scenarios — Digital Wallet System

> **Status**: Complete — Days 25–27 implementation.
> Replaces the Phase 1 skeleton.

---

## Scenario 1 — Duplicate Transfer (Client Retry After Timeout)

**Trigger**
Client submits `POST /wallets/transfer` and the response is lost due to a network
timeout. The client retries the same request — possibly multiple times.

**Affected Components**
TransferService, `transfer_requests` table, Redis idempotency cache.

**User-Visible Impact**
Without mitigation: the sender's balance is debited twice and the recipient's
balance is credited twice. The sender loses double the intended amount with no
indication anything went wrong.

**System Behaviour Without Mitigation**
Both requests reach TransferService, both pass the balance check (the first
commit has not yet been seen by the second request), both execute independently,
and two rows are inserted into `transfer_requests` with different IDs but the
same economic effect.

**Mitigation**
Two-layer idempotency defence:

1. **Fast path — Redis SETNX:** On first receipt, the service stores the completed
   response in Redis under `idempotency:{userId}:{key}` with NX flag and 24h TTL.
   Duplicate requests within 24 hours hit the Redis cache and return the first
   response without touching the database.

2. **Fallback — database unique index:** The `transfer_requests.idempotency_key`
   column has a UNIQUE constraint. If Redis is unavailable and two concurrent
   requests race past the cache check, the second INSERT raises a unique constraint
   violation. TransferService catches this and returns the result of the first
   transfer.

The idempotency key is the `X-Idempotency-Key` UUID header supplied by the client.
Without this header, `POST /wallets/transfer` returns 400 Bad Request.

**Detection**
- Metric: `idempotency_cache_hits_total` counter — high values indicate retry
  traffic (normal) or a misbehaving client (worth investigating at extreme rates).
- Log: `"Idempotent transfer: returning existing result for key {Key}"` at INFO
  level on every replayed response.
- Alert: `idempotency_violations_total > 100/min` per user — may indicate a
  client bug or adversarial replay attempt.

---

## Scenario 2 — PostgreSQL Primary Outage

**Trigger**
The PostgreSQL primary becomes unavailable: hardware failure, OOM kill, planned
maintenance, or network partition between the application and the database.

**Affected Components**
WalletRepository, TransactionRepository, all write paths.

**User-Visible Impact**
All balance-changing operations (transfer, deposit, deactivation) fail immediately
with a 503 Service Unavailable. Balance reads continue from the Redis cache for
up to 60 seconds per wallet before they also fail (cache miss with no DB fallback).

**System Behaviour Without Mitigation**
Every `NpgsqlConnection.OpenAsync()` call throws after the configured connect
timeout (default: 15 seconds). At high QPS, all active requests block for 15
seconds before failing, exhausting the thread pool and making the service
completely unresponsive.

**Mitigation**

1. **Circuit breaker on PostgreSQL connections:** After 5 consecutive connection
   failures within 30 seconds, the PostgreSQL circuit breaker opens. All
   subsequent requests immediately receive a 503 without waiting for the
   connection timeout. This preserves thread pool capacity and keeps the
   API responsive (with limited functionality) during the outage.

2. **Read-only degraded mode:** Balance reads continue from Redis cache.
   WalletService returns cached balances with a `"source": "cache"` flag in
   the response. The API stays available for read-heavy clients.

3. **Fail fast with clear error:** Write operations return:
   ```json
   { "error": { "code": "SERVICE_TEMPORARILY_UNAVAILABLE",
     "message": "Write operations are temporarily unavailable. Please retry." } }
   ```
   The response includes `Retry-After: 30` so clients back off automatically.

4. **Automatic recovery:** When the PostgreSQL primary recovers (or failover
   to a promoted replica completes), the circuit breaker transitions to
   HALF-OPEN, allows one probe request, and closes on success.

**Detection**
- Alert: PostgreSQL health check at `/health/detail` transitions to Unhealthy.
- Alert: `postgresql_connection_errors_total` rate > 10/min → page on-call.
- Alert: Circuit breaker state change to OPEN logged at ERROR level.
- Dashboard: `api_requests_5xx_total` spike coincides with DB outage window.

---

## Scenario 3 — Redis Cache Failure

**Trigger**
Redis instance crashes, runs out of memory, or becomes unreachable due to network
partition.

**Affected Components**
WalletCacheService, idempotency layer, rate limiting middleware.

**User-Visible Impact**
None initially — the system degrades gracefully. Balance reads are slower
(PostgreSQL instead of Redis). Rate limiting falls back to per-instance counters,
which may allow slightly higher request rates across multiple instances.

**System Behaviour Without Mitigation**
If Redis exceptions propagate up the call stack, every balance read throws and
every POST returns a 500 even though the underlying PostgreSQL data is intact.

**Mitigation**
Every Redis call in `WalletCacheService` is wrapped in try/catch and fails open:

```csharp
public async Task<decimal?> GetBalanceAsync(string walletId)
{
    try { /* Redis read */ }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Redis GetBalance failed — falling back to DB");
        return null; // null triggers DB fallback in the service layer
    }
}
```

The service layer treats a `null` cache result as a miss and reads from
PostgreSQL. This is the **fail-open** strategy: Redis failure degrades
performance but never causes incorrect behaviour.

Rate limiting: falls back to the ASP.NET built-in rate limiter (per-instance)
when Redis is unavailable. This may allow up to N×limit requests across N
instances during the outage window — acceptable for the duration of a Redis restart.

**Detection**
- Alert: `redis_errors_total` rate > 5/min → page on-call.
- Alert: `cache_miss_rate > 0.95` (sustained) — indicates Redis is down or cold.
- Metric: `postgresql_reads_total` increases when Redis is down (DB absorbs load).
- Log: Redis WARNING messages at INFO-or-above threshold.

---

## Scenario 4 — Concurrent Transfer Race (Negative Balance)

**Trigger**
Wallet A has a balance of 100. Two concurrent requests each attempt to transfer 80
from Wallet A. Both read the balance as 100 and both pass the `balance >= amount`
check. Without locking, both commits succeed and the balance becomes -60.

**Affected Components**
TransferService, wallets table (concurrent writers), balance integrity.

**User-Visible Impact**
Without mitigation: wallet balance goes negative, violating a core financial
invariant. Downstream ledger reconciliation fails. Financial loss or audit finding.

**System Behaviour Without Mitigation**
At the `READ COMMITTED` isolation level, Transaction T1 reads balance=100,
Transaction T2 reads balance=100 (T1 has not committed yet). Both execute the
debit. One commits first; the other overwrites the first's result. Wallet ends
with a negative balance.

**Mitigation**
Row-level locking with consistent lock ordering eliminates this race:

```sql
-- TransferService acquires locks in lexicographic order
SELECT id, balance, version FROM wallets WHERE id = @WalletId FOR UPDATE;
```

`FOR UPDATE` acquires an exclusive row lock. T2's `FOR UPDATE` blocks until T1
commits and releases its lock. T2 then re-reads the committed balance (now 20)
and sees that 80 > 20 — it throws `InsufficientFundsException`.

Lock ordering (always lock the wallet with the lower string-sort ID first)
prevents deadlocks when two concurrent transfers involve the same pair of wallets
in opposite directions.

**Detection**
- Metric: `insufficient_funds_rejections_total` — healthy signal; should match
  legitimate declined transfers.
- Alert: `concurrent_modification_exceptions_total > 10/min` — indicates hot
  wallet contention; may need to optimise transaction throughput.
- Monitoring: PostgreSQL `pg_locks` view — check for sustained lock waits on
  the wallets table.

---

## Scenario 5 — Kafka Producer Failure After DB Commit

**Trigger**
TransferService successfully commits the transfer to PostgreSQL (money has moved),
then attempts to publish a `TransferCompleted` event to Kafka. The Kafka broker is
unreachable at this exact moment.

**Affected Components**
WalletEventPublisher, Kafka broker, downstream fraud detection and notification consumers.

**User-Visible Impact**
None immediately. The transfer is correct in the database. The sender and recipient
balances are accurate. The user sees a success response.

Internal impact: fraud detection does not evaluate this transfer. Notification
service does not send the confirmation. Downstream systems are unaware.

**System Behaviour Without Mitigation**
The `ProduceException` is thrown, logged, and swallowed. The event is permanently
lost. The fraud engine and notification service never process this transfer.

**Mitigation**

1. **Producer retry with exponential backoff:** `WalletEventPublisher` retries
   the publish up to 3 times with 100ms → 200ms → 400ms backoff before giving up.
   `Acks.All + EnableIdempotence = true` ensures exactly-once semantics on
   the broker side — retries do not create duplicates.

2. **Fire-and-forget with logged failure:** After exhausting retries, the
   failure is logged at ERROR level. The transfer result is still returned to
   the client. A monitoring alert fires on Kafka publish error rate.

3. **Outbox pattern (long-term):** For high-reliability requirements, the
   `TransferCompleted` event payload should be written to an `outbox` table
   inside the same database transaction as the transfer. A separate relay
   process reads unpublished outbox rows and publishes them — guaranteeing
   at-least-once delivery even across Kafka outages of arbitrary duration.

**Detection**
- Alert: `kafka_publish_errors_total > 0` sustained for > 60s → Kafka unreachable.
- Alert: Circuit breaker on Kafka producer transitions to OPEN.
- Metric: Downstream fraud consumer lag spike when Kafka recovers (events replay).

---

## Universal Scenarios (shared across all systems)

### U1 — Database Connection Pool Exhaustion

**Trigger**
All PgBouncer connections are in use. New requests cannot acquire a connection
within the configured timeout.

**Mitigation**
Circuit breaker on connection acquisition: after 5 consecutive pool timeouts in
30 seconds, the circuit opens and all requests immediately return 503 without
waiting for a connection. Pool sizing is tuned to `(avg_query_ms / target_p99_ms) × target_tps`.

**Detection**
Alert: `pgbouncer_pool_wait_time_p99 > 100ms`; `connection_pool_timeouts_total > 5/min`.

### U2 — Downstream Service Timeout

**Trigger**
A synchronous call to an external service (payment processor, identity service)
exceeds the configured deadline.

**Mitigation**
Hard timeout enforced by `CancellationTokenSource` with a 5-second deadline.
On timeout: operation fails fast, circuit breaker increments failure counter,
retry policy may attempt up to 3 times before returning a 503 to the caller.

**Detection**
Alert: `external_call_timeout_rate > 5%` → service is degraded or overloaded.
