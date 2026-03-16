-- =============================================================================
-- V005__add_indexes.sql
-- Digital Wallet System — All performance indexes
--
-- Indexes are created in a separate migration from tables for two reasons:
-- 1. Index creation on large tables can be slow. CREATE INDEX CONCURRENTLY
--    avoids locking the table during the build — useful post-launch.
-- 2. Separating concerns makes it easier to analyse and tune index strategy
--    independently of schema structure.
--
-- ROLLBACK (in reverse order):
--   DROP INDEX IF EXISTS transfer_idempotency_key_idx;
--   DROP INDEX IF EXISTS ledger_wallet_recorded_at_idx;
--   DROP INDEX IF EXISTS transactions_reference_id_idx;
--   DROP INDEX IF EXISTS transactions_wallet_created_at_idx;
--   DROP INDEX IF EXISTS wallets_user_id_idx;
--   -- Note: UNIQUE constraints (wallets_user_currency_unique etc.) were
--   -- created with the tables in V001-V003 and must be dropped via
--   -- ALTER TABLE ... DROP CONSTRAINT if they need to be removed.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- wallets indexes
-- -----------------------------------------------------------------------------

-- Query: "List all wallets belonging to user X"
-- Used by: GET /wallets (if we add that endpoint), internal balance lookup
CREATE INDEX wallets_user_id_idx
    ON wallets (user_id);

COMMENT ON INDEX wallets_user_id_idx IS
    'Supports queries: find all wallets for a given user_id.';

-- -----------------------------------------------------------------------------
-- transactions indexes
-- -----------------------------------------------------------------------------

-- Query: "Get transactions for wallet X, paginated, newest first"
-- Used by: GET /wallets/{id}/transactions
-- The DESC ordering is encoded in the index — PostgreSQL can serve DESC queries
-- from this index without a sort step.
CREATE INDEX transactions_wallet_created_at_idx
    ON transactions (wallet_id, created_at DESC);

COMMENT ON INDEX transactions_wallet_created_at_idx IS
    'Supports paginated transaction history queries ordered by newest first. '
    'Composite index allows index-only scan for wallet_id + created_at filtering.';

-- The UNIQUE constraint on reference_id (defined in V002) already creates
-- an implicit index. Documenting it here for clarity — no additional index needed.
-- Query: idempotency key lookup before processing a deposit or transfer
-- Index: transactions_reference_id_key (implicit from UNIQUE constraint in V002)

-- -----------------------------------------------------------------------------
-- ledger_entries indexes
-- -----------------------------------------------------------------------------

-- Query: "Reconstruct balance at time T for wallet X" (audit / reconciliation)
-- Query: "Get ledger history for wallet X in date range"
CREATE INDEX ledger_wallet_recorded_at_idx
    ON ledger_entries (wallet_id, recorded_at DESC);

COMMENT ON INDEX ledger_wallet_recorded_at_idx IS
    'Supports ledger history queries and balance-at-point-in-time reconstruction. '
    'Ordered DESC so newest entries are at the front of the index.';

-- -----------------------------------------------------------------------------
-- transfer_requests indexes
-- -----------------------------------------------------------------------------

-- The UNIQUE constraint on idempotency_key (defined in V003) creates an implicit
-- index that also serves as the idempotency lookup index.
-- Index name: transfer_idempotency_key_unique (implicit from UNIQUE in V003)

-- Query: "Get all transfers from source wallet X" (operational monitoring)
CREATE INDEX transfer_requests_source_wallet_idx
    ON transfer_requests (source_wallet_id, created_at DESC);

COMMENT ON INDEX transfer_requests_source_wallet_idx IS
    'Supports operational queries: list all transfers initiated from a wallet.';

-- -----------------------------------------------------------------------------
-- Analyse tables after index creation to update planner statistics
-- -----------------------------------------------------------------------------
ANALYZE wallets;
ANALYZE transactions;
ANALYZE ledger_entries;
ANALYZE transfer_requests;
