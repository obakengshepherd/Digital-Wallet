# Scaling Strategy — Digital Wallet System

---

## Current Single-Node Bottlenecks

Before scaling, it is worth being honest about where a single-node deployment will struggle:

- **Balance reads**: Without caching, every `GET /wallets/{id}` hits the PostgreSQL primary.
  At 50K daily active users making multiple reads, this is manageable — until it is not.
  The cache eliminates most of this, but cold-start and cache-miss traffic still reaches
  the database.

- **Transfer write path**: Every `POST /wallets/transfer` requires two row-level locks, two
  balance updates, two ledger writes, and one event publish — all synchronous. This is a
  multi-step database transaction and will become the first throughput ceiling.

- **Ledger table growth**: The `ledger_entries` table is append-only and grows without bound.
  Queries against it (e.g. balance reconstruction, reconciliation) will degrade as the table
  grows unless properly indexed and partitioned.

- **Kafka consumer lag**: If the Fraud Detection consumer falls behind, the Kafka topic
  backlog grows. This does not block the transfer path, but it delays fraud signals — which
  is an operational concern.

---

## Horizontal Scaling Plan

### API Layer

The API layer is fully stateless. Any instance can serve any request. Scale horizontally by
adding instances behind the load balancer. Auto-scaling should be triggered on CPU utilisation
(>70%) or request queue depth. No changes to the application are required — the load balancer
distributes traffic automatically.

Target: support 500 transfers/sec with 3–5 API instances depending on instance size.

### Service Layer

WalletService and TransferService are stateless application services. They hold no in-memory
state between requests. Scale them as part of the API layer — they run in the same process.
If services are separated into independent deployments, they scale the same way: add instances
behind an internal load balancer.

### Redis

A single Redis instance handles the balance cache at the defined scale. Redis is single-threaded
for command execution but handles tens of thousands of operations per second without issue at
this workload.

If Redis becomes a bottleneck (unlikely at this scale), introduce Redis Cluster, which shards
the keyspace across multiple nodes. The `wallet:{id}:balance` key pattern distributes naturally
across shards by wallet ID hash.

### PostgreSQL

**Phase 1 — Read replicas**: Route all read-only queries (transaction history, balance lookups
on cache miss) to one or more read replicas. The write path (transfers, deposits) always targets
the primary. This is the first scaling action to take when the primary shows read saturation.

**Phase 2 — Connection pooling**: Add PgBouncer in front of the PostgreSQL primary to pool
connections. Each API instance opens a connection to PgBouncer rather than directly to
PostgreSQL. This prevents connection count from growing linearly with API instance count.

**Phase 3 — Table partitioning**: Partition `ledger_entries` and `transactions` by month or
by `wallet_id` range when table size begins to affect query performance (typically >100M rows).

**Phase 4 — Vertical scaling**: Scale the PostgreSQL primary vertically (more CPU, faster
NVMe storage) before considering horizontal write splitting. For financial workloads, a
single well-resourced primary with replicas is strongly preferable to a distributed write
topology, which introduces cross-node consistency complexity.

### Kafka

Partition the `wallet.transfers` topic by `wallet_id`. The partition count sets the maximum
parallelism for consumers. Start with 8 partitions. Scale consumer instances up to the
partition count to increase throughput. Add partitions (and corresponding consumer instances)
as throughput grows — note that partition count can only be increased, not decreased.

---

## Cache Hit Rate Targets

| Cache Key            | Target Hit Rate | TTL    | On Miss Action                        |
|----------------------|-----------------|--------|---------------------------------------|
| `wallet:{id}:balance`| ≥ 90%           | 60s    | Read from PostgreSQL primary, re-cache |
| `idempotency:{key}`  | N/A (write-once)| 24h    | Process request, store result          |

If the balance cache hit rate drops below 80%, investigate: likely cause is either TTL set
too short, cache eviction due to memory pressure, or an unusually large number of distinct
wallets being accessed within the TTL window. The correct response is to increase Redis memory
allocation before increasing TTL, as a longer TTL risks serving stale data.

On a complete Redis failure, all balance reads fall through to PostgreSQL. At 50K daily active
users this will increase PostgreSQL read load by roughly 10x. The database can absorb this
temporarily, but it should trigger an alert and Redis recovery should be prioritised.

---

## Queue Throughput Targets

| Topic               | Expected Peak Throughput | Partition Count | Consumer Instances |
|---------------------|--------------------------|-----------------|--------------------|
| `wallet.transfers`  | 500 events/sec           | 8               | Up to 8            |
| `wallet.deposits`   | 100 events/sec           | 4               | Up to 4            |

Monitor consumer group lag on all topics. Alert when lag exceeds 10,000 messages on any
partition — this indicates consumers cannot keep up and additional instances are required.
