# Data Model — Digital Wallet System

---

## Database Technology Choices

### PostgreSQL (Primary data store)
All financial state lives in PostgreSQL. The choice is driven by three non-negotiable
requirements: ACID transactions (balance changes must be atomic), row-level locking
(`SELECT ... FOR UPDATE` to prevent concurrent overdraft), and a strong consistency model
where a read immediately following a committed write returns the new value. PostgreSQL's
`DECIMAL` type stores money without floating-point rounding errors — a property that
ruled out every document or wide-column store alternative.

### Redis (Ephemeral state only)
Redis holds wallet balance cache (`wallet:{id}:balance`, TTL 60s) and idempotency key
results (`idempotency:{key}`, TTL 24h). It is never the source of truth. A Redis restart
degrades to slower PostgreSQL reads but never causes data loss or incorrect balances.

---

## Entity Relationship Overview

A **User** (managed by an upstream identity service) owns one or more **Wallets**, each
in a single currency. A user cannot hold two wallets in the same currency — enforced by a
unique index on `(user_id, currency)`.

Every balance-changing operation creates a **Transaction** (the business-level record) and
two **LedgerEntries** (the double-entry accounting record — one debit, one credit). The
ledger is the audit trail: summing all ledger entries for a wallet must always equal the
wallet's current balance.

A **TransferRequest** ties two wallets together in a single transfer operation. It holds
the idempotency key, ensuring the same transfer cannot be processed twice regardless of
how many times the client retries.

---

## Table Definitions

### `wallets`

| Column       | Type           | Constraints                          | Description                                   |
|--------------|----------------|--------------------------------------|-----------------------------------------------|
| `id`         | `VARCHAR(36)`  | PRIMARY KEY                          | Prefixed UUID: `wlt_<uuid>`                   |
| `user_id`    | `VARCHAR(36)`  | NOT NULL                             | Owner — references upstream identity service  |
| `currency`   | `CHAR(3)`      | NOT NULL, CHECK (currency ~ '^[A-Z]{3}$') | ISO 4217 currency code                 |
| `balance`    | `DECIMAL(19,4)`| NOT NULL, DEFAULT 0, CHECK (balance >= 0) | Current confirmed balance             |
| `status`     | `wallet_status`| NOT NULL, DEFAULT 'active'           | Enum: `active`, `inactive`                    |
| `version`    | `INTEGER`      | NOT NULL, DEFAULT 0                  | Optimistic locking counter                    |
| `created_at` | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()              | Creation timestamp                            |
| `updated_at` | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()              | Last modification timestamp                   |

**Why `DECIMAL(19,4)` and not `FLOAT`:** Floating-point types (`FLOAT`, `DOUBLE`) use
binary representation that cannot exactly represent most decimal fractions. `0.10 + 0.20`
in IEEE 754 floating point equals `0.30000000000000004`. For financial data, even a
sub-cent rounding error compounds across millions of transactions and is categorically
unacceptable. `DECIMAL(19,4)` stores the exact value with 4 decimal places of precision
and up to 15 integer digits — sufficient for any realistic balance.

**Why `version` (optimistic lock):** When two concurrent requests both read a wallet's
balance and then both attempt to update it, without a lock one update silently overwrites
the other. The `version` column is incremented on every update; the `WHERE version = $expected`
clause in the UPDATE statement will match zero rows if another transaction updated first,
causing the application to detect the conflict and retry.

---

### `transactions`

| Column         | Type           | Constraints              | Description                                        |
|----------------|----------------|--------------------------|----------------------------------------------------|
| `id`           | `VARCHAR(36)`  | PRIMARY KEY              | Prefixed UUID: `txn_<uuid>`                        |
| `wallet_id`    | `VARCHAR(36)`  | NOT NULL, FK → wallets   | Wallet this transaction affects                    |
| `type`         | `txn_type`     | NOT NULL                 | Enum: `credit`, `debit`                            |
| `amount`       | `DECIMAL(19,4)`| NOT NULL, CHECK (amount > 0) | Always positive; `type` indicates direction    |
| `reference_id` | `VARCHAR(36)`  | UNIQUE                   | Idempotency key from the originating request       |
| `status`       | `txn_status`   | NOT NULL, DEFAULT 'completed' | Enum: `pending`, `completed`, `failed`        |
| `created_at`   | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()  | Immutable creation timestamp                       |

**Why `reference_id` is UNIQUE:** This column is the database-level idempotency guard.
Even if the application's Redis-based idempotency check fails (Redis down, race condition),
the unique constraint on `reference_id` ensures the database will reject a duplicate
transaction with a constraint violation rather than silently double-processing it.

---

### `ledger_entries`

| Column           | Type           | Constraints                      | Description                             |
|------------------|----------------|----------------------------------|-----------------------------------------|
| `id`             | `VARCHAR(36)`  | PRIMARY KEY                      | Prefixed UUID: `led_<uuid>`             |
| `transaction_id` | `VARCHAR(36)`  | NOT NULL, FK → transactions      | The originating transaction             |
| `wallet_id`      | `VARCHAR(36)`  | NOT NULL, FK → wallets           | The wallet this entry belongs to        |
| `direction`      | `ledger_dir`   | NOT NULL                         | Enum: `credit`, `debit`                 |
| `amount`         | `DECIMAL(19,4)`| NOT NULL, CHECK (amount > 0)     | Always positive                         |
| `running_balance`| `DECIMAL(19,4)`| NOT NULL                         | Wallet balance after this entry applied |
| `recorded_at`    | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()          | Immutable — never updated               |

**Append-only table:** Ledger entries are never updated or deleted. They form the
authoritative audit trail from which any wallet's balance can be fully reconstructed.

---

### `transfer_requests`

| Column                  | Type           | Constraints                   | Description                               |
|-------------------------|----------------|-------------------------------|-------------------------------------------|
| `id`                    | `VARCHAR(36)`  | PRIMARY KEY                   | Prefixed UUID: `trf_<uuid>`               |
| `source_wallet_id`      | `VARCHAR(36)`  | NOT NULL, FK → wallets        | Debit side                                |
| `destination_wallet_id` | `VARCHAR(36)`  | NOT NULL, FK → wallets        | Credit side                               |
| `amount`                | `DECIMAL(19,4)`| NOT NULL, CHECK (amount > 0)  | Transfer amount                           |
| `idempotency_key`       | `VARCHAR(36)`  | NOT NULL, UNIQUE              | Client-supplied UUID — prevents duplicates|
| `status`                | `transfer_status`| NOT NULL, DEFAULT 'completed'| Enum: `pending`, `completed`, `failed`    |
| `created_at`            | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()       | Immutable                                 |

---

## Index Strategy

| Index Name                              | Table              | Columns                     | Type    | Query Pattern                              |
|-----------------------------------------|--------------------|-----------------------------|---------|--------------------------------------------|
| `wallets_user_id_currency_uniq`         | `wallets`          | `(user_id, currency)`       | UNIQUE  | Enforce one wallet per user per currency   |
| `wallets_user_id_idx`                   | `wallets`          | `(user_id)`                 | B-tree  | `GET /wallets` — list user's wallets       |
| `transactions_wallet_id_created_at_idx` | `transactions`     | `(wallet_id, created_at DESC)` | B-tree | `GET /wallets/{id}/transactions` paginated |
| `transactions_reference_id_uniq`        | `transactions`     | `(reference_id)`            | UNIQUE  | Idempotency check on deposit/transfer      |
| `ledger_wallet_recorded_at_idx`         | `ledger_entries`   | `(wallet_id, recorded_at DESC)` | B-tree | Balance reconstruction + audit queries    |
| `transfer_idempotency_uniq`             | `transfer_requests`| `(idempotency_key)`         | UNIQUE  | Prevent duplicate transfer processing      |

---

## Relationship Types

- **User → Wallets**: one-to-many. One user can have one wallet per currency (enforced by unique index on `user_id, currency`).
- **Wallet → Transactions**: one-to-many. Every deposit, debit, or credit creates a transaction.
- **Transaction → LedgerEntries**: one-to-two. Every transaction creates exactly two ledger entries (debit + credit).
- **Wallet → LedgerEntries**: one-to-many. A wallet's full history is its ordered ledger entries.
- **TransferRequest → Wallets**: many-to-one (both source and destination). One transfer touches exactly two wallets.

---

## Soft Delete Strategy

Wallets are **not** soft-deleted. They are deactivated by setting `status = 'inactive'`.
The wallet record and all its transaction history remain permanently accessible. Hard
deletion of financial records is prohibited — regulatory requirements mandate retention.

Transactions and ledger entries are **never deleted** under any circumstance. They are
append-only and form the immutable audit trail.

---

## Audit Trail

| Table              | `created_at` | `updated_at` | `version` | Notes                                  |
|--------------------|-------------|-------------|----------|----------------------------------------|
| `wallets`          | ✓           | ✓           | ✓        | Updated on every balance change        |
| `transactions`     | ✓           | ✗           | ✗        | Immutable after creation               |
| `ledger_entries`   | recorded_at | ✗           | ✗        | Append-only, never modified            |
| `transfer_requests`| ✓           | ✗           | ✗        | Immutable after creation               |
