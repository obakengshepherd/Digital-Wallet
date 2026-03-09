# Problem Statement — Digital Wallet System

---

## Section 1 — The Problem

Modern financial platforms need a reliable way to store user balances and move money between
accounts in real time. Whether it is a peer-to-peer payment app, an e-commerce wallet, or an
in-app credit system, users expect transfers to be instant, accurate, and always reflected
correctly in their balance. When money disappears, doubles, or a transfer succeeds on one end
but not the other, trust collapses — and in financial systems, trust is the product. The business
problem is straightforward: users need to deposit funds into a wallet, transfer those funds to
other users, and see an accurate, up-to-date transaction history at all times.

---

## Section 2 — Why It Is Hard

The technical challenges arise precisely because money cannot be approximate and systems are
not perfectly reliable.

- **Consistency**: A transfer deducting from one wallet and crediting another must be atomic.
  If the system crashes between the debit and the credit, the money must not vanish or be
  double-counted. This requires ACID guarantees across multiple rows or tables.

- **Concurrency**: Two concurrent transfers from the same wallet can both pass a balance check
  and then both execute — resulting in a negative balance. The system must serialize access to
  each wallet's balance under high concurrency without becoming a bottleneck.

- **Idempotency**: Clients retry on timeout. A network failure after the server commits but
  before the response is returned means the client has no idea whether the transfer happened.
  Without idempotency protection, a retry causes a duplicate transaction.

- **Scale**: At 500 transfers per second, every additional database roundtrip per operation
  compounds. Balance reads must be fast, write contention on hot wallets must be managed, and
  the ledger must remain queryable without degrading transfer throughput.

- **Auditability**: Financial regulators and users alike require a permanent, immutable record
  of every balance movement. The system must maintain a ledger that can reconstruct any wallet's
  balance at any point in time from its transaction history.

---

## Section 3 — Scope of This Implementation

**In scope:**

- Wallet creation per user, per currency
- Balance deposits (external funding simulation)
- Peer-to-peer transfers between wallets
- Transaction history retrieval with pagination
- Idempotency-safe transfer processing
- Double-entry ledger recording for every balance movement
- Redis-backed balance caching with cache invalidation on write
- Kafka event publishing for downstream consumers (fraud detection, notifications)
- Rate limiting on write endpoints

**Out of scope:**

- Integration with real payment processors or banking rails
- Multi-currency conversion
- Scheduled or recurring transfers
- Fraud detection (handled by the separate Fraud Detection System)
- User authentication and identity management (assumed provided by an upstream service)
- Regulatory compliance, KYC, or AML processes

---

## Section 4 — Success Criteria

The system is working correctly when:

1. A transfer between two wallets either completes fully (both sides updated, ledger written,
   event published) or fails with no side effects — partial completion is never acceptable.

2. Submitting the same transfer request twice with the same idempotency key produces exactly
   one debit and one credit, with the second request returning the result of the first.

3. A wallet balance read always reflects the true committed state — it never returns a value
   that includes an in-flight or failed transaction.

4. Under concurrent load, no wallet ever reaches a negative balance regardless of how many
   simultaneous transfers are attempted against it.

5. Every balance movement has a corresponding ledger entry, and the sum of all ledger entries
   for a wallet equals its current balance at all times.

6. The p99 latency for `POST /wallets/transfer` does not exceed 200ms under the defined peak
   load of 500 transfers per second.

---
