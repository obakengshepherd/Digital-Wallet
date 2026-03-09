# Architecture — Digital Wallet System

---

## Overview

The Digital Wallet System is a layered backend architecture designed around two non-negotiable
constraints: every balance change must be atomic and durable, and no duplicate transaction may
ever be processed. The system separates concerns across a thin API layer, a stateless application
service layer, and a persistence layer composed of PostgreSQL (as the source of truth), Redis
(as the read-acceleration cache), and Kafka (as the event backbone for downstream consumers).

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────┐
│                        Clients                          │
│             (Mobile App / Web App / Partner API)        │
└──────────────────────────┬──────────────────────────────┘
                           │ HTTPS
┌──────────────────────────▼──────────────────────────────┐
│                    Load Balancer                         │
│            (Round-Robin, TLS Termination)               │
└──────────────────────────┬──────────────────────────────┘
                           │ HTTP/1.1
┌──────────────────────────▼──────────────────────────────┐
│                  API Gateway / Layer                     │
│     (Auth Middleware · Rate Limiter · Idempotency)      │
└────────┬──────────────────────────────────┬─────────────┘
         │                                  │
┌────────▼────────┐               ┌─────────▼─────────┐
│  WalletService  │               │  TransferService   │
│  (CRUD, cache)  │               │  (atomic, locked)  │
└────────┬────────┘               └─────────┬──────────┘
         │                                  │
┌────────▼──────────────────────────────────▼──────────┐
│                    PostgreSQL                          │
│       (wallets · transactions · ledger_entries)       │
└───────────────────────────┬──────────────────────────┘
                            │
         ┌──────────────────┼──────────────────┐
         │                  │                  │
┌────────▼───────┐ ┌────────▼───────┐ ┌────────▼──────┐
│     Redis      │ │     Kafka      │ │ LedgerService  │
│ (balance cache)│ │  (tx events)   │ │  (audit log)   │
└────────────────┘ └────────────────┘ └───────────────┘
```

---

## Layer-by-Layer Description

### Load Balancer

The load balancer is the single external entry point. It terminates TLS, strips and validates
authentication headers before forwarding requests downstream, and distributes traffic across
API instances using a round-robin algorithm. Because the API layer is fully stateless, no
session affinity is required — any instance can serve any request. Health checks ping each
instance's `/health` endpoint every 10 seconds; an instance failing three consecutive checks
is removed from rotation without operator intervention.

### API Layer

The API layer is intentionally thin. Its only responsibilities are parsing and validating
incoming HTTP requests, enforcing rate limits, checking idempotency keys, delegating to the
appropriate service, and serialising the response. It contains no business logic. It does not
read from or write to the database directly. Every controller method follows the same
pattern: validate input → call service interface → return typed response or error envelope.

Versioning is path-based (`/api/v1/`). Breaking changes increment the version; non-breaking
additions do not. The API layer also hosts the Swagger/OpenAPI documentation endpoint.

### Wallet Service

WalletService handles wallet lifecycle operations: creation, retrieval, and deposits. On
balance reads, it consults Redis first using the key pattern `wallet:{id}:balance`. On a
cache miss, it reads from the PostgreSQL primary, writes the result back to Redis with a
60-second TTL, and returns the value. On any write that changes a wallet's balance, it
explicitly deletes the cache key — the next read will repopulate it from the database.

### Transfer Service

TransferService is the most critical component in the system. It processes fund movements
between wallets using the following sequence:

1. Check the `transfer_requests` table for the provided idempotency key. If found, return
   the stored result immediately without re-executing.
2. Validate that both wallets exist, are active, and that the source balance covers the amount.
3. Acquire row-level locks on both wallet rows inside a database transaction, always locking
   the wallet with the lower UUID first to prevent deadlocks under concurrent transfers.
4. Execute the debit and credit, write two ledger entries, and update both wallet balances —
   all within the same PostgreSQL transaction.
5. On commit, publish a `TransferCompleted` event to Kafka for fraud detection and notification
   consumers.
6. Invalidate the Redis cache entries for both wallets.

### Cache — Redis

Redis serves as the read-acceleration layer for wallet balances. It is not the source of truth.
A Redis failure degrades to slower reads from PostgreSQL but does not cause data loss or
incorrect balances. Cache keys are simple strings (`wallet:{id}:balance`) with a 60-second TTL.
All write operations invalidate the relevant cache key immediately after the database commit —
not before, and not speculatively.

The idempotency layer also uses Redis (`idempotency:{key}`) with a 24-hour TTL to reject
duplicate in-flight requests before they reach the service layer.

### Database — PostgreSQL

PostgreSQL is the authoritative data store. All financial state lives here. The schema uses
ACID transactions for all balance modifications. The `wallets` table uses optimistic locking
via a `version` column to detect concurrent modification. The `ledger_entries` table is
append-only and never updated or deleted. Read replicas may be introduced for transaction
history queries and reporting without affecting the write path.

### Message Queue — Kafka

Kafka receives `TransferCompleted` events from TransferService and `DepositCompleted` events
from WalletService. These events are published after the database transaction commits — never
speculatively. Consumers include the Fraud Detection System (which evaluates every financial
event) and a downstream Notification Service. Kafka retains events for 7 days, enabling
consumer replay on failure. The topic is partitioned by `wallet_id` to preserve per-wallet
event ordering.

---

## Component Responsibilities Summary

| Component        | Responsibility                                        | Communicates Via       |
|------------------|-------------------------------------------------------|------------------------|
| Load Balancer    | TLS termination, health routing, round-robin dispatch | HTTPS (inbound)        |
| API Layer        | Request validation, auth, rate limiting, routing      | HTTP (internal)        |
| WalletService    | Wallet CRUD, balance reads with cache                 | PostgreSQL + Redis     |
| TransferService  | Atomic transfers, idempotency, event publishing       | PostgreSQL + Kafka     |
| TransactionService | Transaction history retrieval, pagination           | PostgreSQL (read replica) |
| Redis            | Balance cache, idempotency key store                  | In-memory              |
| PostgreSQL       | Source of truth for all financial state               | TCP                    |
| Kafka            | Event stream for downstream consumers                 | Kafka protocol         |
