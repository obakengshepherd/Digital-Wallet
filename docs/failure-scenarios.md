# Failure Scenarios — Digital Wallet System

> **Status**: Skeleton — stubs defined on Day 2. Full mitigations and implementations added on Day 27.

---

## Scenario 1 — Duplicate Transfer (Client Retry)

**Trigger**: Client submits `POST /wallets/transfer` and receives a network timeout before
the response arrives. Client retries the same request.

**Component that fails**: Network / Client-server communication.

**Impact**: User-facing — if not handled, the transfer executes twice: the sender loses
double the intended amount, the receiver gains double.

**Mitigation strategy**: TBD Day 27 — involves idempotency key enforcement in
`transfer_requests` table.

---

## Scenario 2 — PostgreSQL Primary Outage

**Trigger**: The PostgreSQL primary becomes unavailable due to hardware failure, OOM kill,
or network partition.

**Component that fails**: PostgreSQL primary node.

**Impact**: All write operations (transfers, deposits) fail immediately. Read operations
depending on cache hits continue; cache misses fail.

**Mitigation strategy**: TBD Day 27 — involves read-only degradation mode, queue-backed
write buffering, and failover to a promoted replica.

---

## Scenario 3 — Redis Cache Failure

**Trigger**: Redis instance crashes or becomes unreachable.

**Component that fails**: Redis cache layer.

**Impact**: Internal — balance reads fall through to PostgreSQL, increasing database read
load significantly. No data loss. No incorrect balances.

**Mitigation strategy**: TBD Day 27 — involves graceful fallback to PostgreSQL reads,
alerting on Redis unavailability, and Redis restart/recovery procedure.

---

## Scenario 4 — Concurrent Transfer Race (Negative Balance)

**Trigger**: Two concurrent transfers from the same wallet both pass the balance validation
check before either has committed.

**Component that fails**: Application concurrency control.

**Impact**: User-facing — wallet balance goes negative; financial integrity is violated.

**Mitigation strategy**: TBD Day 27 — involves row-level locking with consistent lock
ordering to prevent both races and deadlocks.

---

## Scenario 5 — Kafka Producer Failure

**Trigger**: The Kafka broker is unreachable when TransferService attempts to publish a
`TransferCompleted` event after a successful database commit.

**Component that fails**: Kafka broker / network.

**Impact**: Internal — transfer is committed and correct in the database, but downstream
consumers (fraud detection, notifications) do not receive the event.

**Mitigation strategy**: TBD Day 27 — involves outbox pattern or producer retry with
at-least-once delivery guarantees.
