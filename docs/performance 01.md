# Performance — Digital Wallet System

---

## Current Bottlenecks (Honest Assessment)

### Bottleneck 1: Transfer write path serialisation
Every `POST /wallets/transfer` acquires two row-level locks in PostgreSQL and
writes 5 rows (2 transactions, 2 ledger entries, 1 transfer request) inside a
single transaction. This is fundamentally serial for any two wallets that share
a concurrent transfer. At 500 TPS this is the first ceiling hit.

**Mitigation:** Optimistic locking on wallets (version column), deadlock prevention
via consistent lock ordering, and connection pooling via PgBouncer.

### Bottleneck 2: Balance reads without cache warm
On a cold cache restart, every `GET /wallets/{id}` hits the PostgreSQL primary.
At 50K DAU making ~5 reads per session, that is 250K reads/day — manageable, but
the initial spike after a Redis restart can saturate the primary.

**Mitigation:** Cache stampede protection (mutex pattern), Redis AOF persistence,
pre-warming scripts after planned restarts.

### Bottleneck 3: Ledger table growth
The `ledger_entries` table is append-only and grows without bound. Queries that
scan it (reconciliation, balance reconstruction) degrade as the table grows.

**Mitigation:** Index on `(wallet_id, recorded_at DESC)`, monthly partitioning
at >100M rows, archive old partitions to cold storage.

---

## Cache Hit Rate Targets

| Cache Key              | Target Hit Rate | Acceptable Miss Rate | Action on Miss            |
|------------------------|-----------------|----------------------|---------------------------|
| `wallet:{id}:balance`  | ≥ 95%           | ≤ 5%                 | Read from PostgreSQL primary, re-cache |
| `idempotency:{k}`      | ≥ 99%           | ≤ 1%                 | Check DB unique index (fallback) |

If balance cache hit rate drops below 90% (monitor via `INFO keyspace` and custom
metrics), investigate: Redis memory pressure, TTL too short, or access pattern
spread across too many distinct wallet IDs.

---

## Database Read Replica Routing

| Operation                         | Target          | Reason                                    |
|-----------------------------------|-----------------|-------------------------------------------|
| `GET /wallets/{id}` (balance read)| Read replica    | Balance shown to user; 60s TTL acceptable |
| `GET /wallets/{id}/transactions`  | Read replica    | Historical data; eventual consistency OK  |
| `POST /wallets/transfer` (pre-check) | **Primary** | Must see latest committed balance         |
| `POST /wallets/transfer` (lock+write) | **Primary** | Requires FOR UPDATE — must be primary     |
| `POST /wallets/{id}/deposit`      | **Primary**     | Write path — must use primary             |
| Ledger reconciliation             | Read replica    | Historical audit; can be eventually consistent |

**Rule:** Any read that feeds directly into a write decision (e.g. balance check
before a transfer) must go to the primary. Display reads go to replicas.

---

## Connection Pool Sizing

Using PgBouncer in transaction mode between the application and PostgreSQL.

| Setting                       | Value | Rationale                                           |
|-------------------------------|-------|-----------------------------------------------------|
| Max pool size per instance    | 20    | Transfer holds lock for ~10ms; 20 * 10ms = 200ms backlog tolerance |
| Min pool size                 | 5     | Keep warm connections for low-traffic periods       |
| Connection timeout            | 5s    | Fail fast on pool exhaustion rather than queue indefinitely |
| PgBouncer pool mode           | Transaction | Releases connection between statements — critical for Dapper |
| Max server connections        | 100   | 5 API instances × 20 = 100 total to PgBouncer       |

**Formula:** `pool_size = (avg_query_duration_ms / target_latency_ms) × target_tps`
For transfers: `(10ms / 200ms) × 500 TPS = 25 connections` — round up to 30 per instance.

---

## Query Performance Targets

| Query                                        | Target p95 | Target p99 | Index Used                          |
|----------------------------------------------|-----------|-----------|--------------------------------------|
| `SELECT * FROM wallets WHERE id = ?`         | < 2ms     | < 5ms     | Primary key                          |
| Balance update + ledger insert (transfer)    | < 15ms    | < 30ms    | Row-level lock, PK                   |
| `SELECT transactions WHERE wallet_id = ?`   | < 10ms    | < 20ms    | `transactions_wallet_created_at_idx` |
| Idempotency key lookup                        | < 2ms     | < 5ms     | `transactions_reference_id_uniq`     |
| Ledger sum for reconciliation                 | < 50ms    | < 100ms   | `ledger_wallet_recorded_at_idx`      |

---

## Rate Limiting Configuration

**Algorithm:** Sliding window (Redis INCR + EXPIRE) — accurate enough for financial
APIs, simpler than sorted-set true sliding window for standard endpoints.
True sliding window (sorted set) used for `POST /wallets/transfer` only.

| Policy          | Limit | Window  | Endpoint                              |
|-----------------|-------|---------|---------------------------------------|
| transfer        | 10    | 1 min   | `POST /wallets/transfer`              |
| deposit         | 20    | 1 min   | `POST /wallets/{id}/deposit`          |
| reads           | 200   | 1 min   | `GET /wallets/*`                      |
| authenticated   | 100   | 1 min   | All other authenticated endpoints     |
| unauthenticated | 10    | 1 min   | Unauthenticated requests (by IP)      |

**Distributed:** Rate limit state stored in Redis (`rl:{policy}:{userId}`) so all
API instances enforce the same limit. A user cannot bypass limits by targeting
different instances.
