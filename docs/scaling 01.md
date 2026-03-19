# Scaling Strategy — Digital Wallet System

---

## Horizontal Scaling Table

| Component           | Scales Horizontally? | Notes                                                |
|---------------------|---------------------|------------------------------------------------------|
| API Layer           | ✅ Yes — stateless   | Add instances behind load balancer; no coordination  |
| WalletService       | ✅ Yes — stateless   | All state in PostgreSQL + Redis; no instance memory  |
| TransferService     | ✅ Yes — stateless   | Lock ordering prevents deadlock under any concurrency|
| TransactionService  | ✅ Yes — stateless   | Read-only; routes to PostgreSQL replica              |
| Redis               | ✅ Yes (Cluster)     | Hash-slot sharding by wallet_id at scale             |
| Kafka               | ✅ Yes               | Add brokers and partitions; consumers scale to match |
| PostgreSQL primary  | ❌ No (writes)       | Single write primary; scale reads with replicas      |
| PostgreSQL replicas | ✅ Yes               | Add replicas; routing via PgBouncer/HAProxy          |

---

## Load Balancing Configuration

### Algorithm: Round-Robin

All API instances are stateless — any instance can serve any request. No session
affinity required. Round-robin distributes load evenly across instances.

```
Client
  │
  ▼
Load Balancer (Round-Robin)
  ├── API Instance 1  :8080
  ├── API Instance 2  :8080
  └── API Instance 3  :8080
        │
        ▼
    PgBouncer :5432
        │
        ├── PostgreSQL Primary  (writes + lock reads)
        └── PostgreSQL Replica  (display reads)
```

### Health Check Configuration

```yaml
healthcheck:
  path: /health
  interval: 10s
  timeout: 2s
  healthy_threshold: 2      # 2 consecutive successes to add to rotation
  unhealthy_threshold: 3    # 3 consecutive failures to remove from rotation
```

The `/health` endpoint returns 200 immediately without DB queries — the load
balancer gets a fast, reliable signal. The DB check happens at `/health/detail`.

### Session Affinity

**Not required and explicitly avoided for the wallet API.**

Stateless design means no session-bound state lives in the application process.
JWT tokens are validated independently by each instance. Redis is the shared
state store. Any instance can serve any request, which simplifies deployment
and enables zero-downtime rolling restarts.

---

## Stateless Design Guarantees

The following invariants are maintained to ensure statelessness:

1. **No in-process wallet state.** Balances, transaction history, and idempotency
   results are never cached in application memory between requests. Every request
   reads from Redis or PostgreSQL.

2. **JWT validation is self-contained.** Each instance validates the token
   signature independently using the public key from the auth server. No
   session store or token revocation cache is maintained per-instance.

3. **Idempotency keys are stored in Redis (shared).** An idempotency check
   performed by Instance 1 is visible to Instance 2 on the next request.

4. **No sticky sessions on retry.** If an instance fails mid-request, the client
   retries with the same idempotency key and any other instance will detect the
   duplicate and return the correct result.

---

## Scaling Triggers

| Metric                          | Threshold     | Action                                      |
|---------------------------------|---------------|---------------------------------------------|
| CPU utilisation (per instance)  | > 70%         | Add API instance                            |
| p99 transfer latency            | > 200ms       | Add API instance or increase PgBouncer pool |
| PostgreSQL primary CPU          | > 60%         | Add read replica; review query routing      |
| Redis memory utilisation        | > 75%         | Increase Redis memory or enable eviction    |
| Kafka consumer lag              | > 10,000 msgs | Add consumer instances (up to partition count) |
