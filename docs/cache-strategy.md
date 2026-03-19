# Cache Strategy — Digital Wallet System

---

## Pattern: Cache-Aside (Lazy Loading)

Used for wallet balance reads. The application is responsible for populating and
invalidating the cache. Redis is never the source of truth.

```
Read balance:
  1. Check Redis → GET wallet:{id}:balance
  2. Cache HIT  → return value immediately (p99 ~1ms)
  3. Cache MISS → query PostgreSQL primary
              → SET wallet:{id}:balance {value} EX 60
              → return value (p99 ~10ms)

Write balance (deposit or transfer):
  1. Open DB transaction
  2. Execute balance update in PostgreSQL
  3. COMMIT
  4. DEL wallet:{id}:balance   ← invalidate AFTER commit
  5. Publish Kafka event
```

**Why invalidate-after-write and not write-through?**
Write-through would require updating Redis inside the database transaction, before
the commit. If the commit then fails and we roll back, the cache would hold a value
that was never committed to the database — serving incorrect balances to readers.
Invalidating after commit is slower on the next read (one cache miss) but is always
correct.

---

## Pattern: SETNX for Idempotency (Distributed Mutex)

```
POST /wallets/transfer {X-Idempotency-Key: key}
  1. GET idempotency:{userId}:{key}     ← fast path
  2. HIT  → return cached response (no DB touch)
  3. MISS → proceed with transfer
         → on success: SET idempotency:{userId}:{key} {response} NX EX 86400
```

`NX` (not-exists) ensures that if two concurrent requests with the same key arrive
simultaneously, exactly one writes the result and the other reads it on the next check.

---

## Key Inventory

| Key Pattern                      | Type   | TTL  | Set When              | Invalidated When     |
|----------------------------------|--------|------|-----------------------|----------------------|
| `wallet:{id}:balance`            | String | 60s  | Balance read (miss)   | Any balance change   |
| `idempotency:{userId}:{key}`     | String | 24h  | Successful write      | Never (TTL expiry)   |

---

## TTL Rationale

- **60s balance TTL**: A wallet balance that is 60 seconds stale is acceptable for
  display purposes. Transfer validation always reads from the PostgreSQL primary
  (inside the locked transaction), never from cache.
- **24h idempotency TTL**: Matches the standard idempotency window. After 24 hours,
  a client submitting the same key is treated as a new request.

---

## Failure Handling

Redis failure is non-blocking. Every Redis operation is wrapped in try/catch:
- `GetBalance` failure → returns `null` → application reads from PostgreSQL
- `SetBalance` failure → logged as warning, not re-thrown
- `InvalidateBalance` failure → logged as warning; the TTL will expire naturally (max 60s staleness)
- `GetIdempotencyResult` failure → treat as miss → re-execute the operation (idempotency relies on DB unique index as fallback)
