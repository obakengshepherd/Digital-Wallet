-- =============================================================================
-- V002__create_transactions_table.sql
-- Digital Wallet System — transactions and ledger_entries tables
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS ledger_entries CASCADE;
--   DROP TABLE IF EXISTS transactions CASCADE;
-- =============================================================================

-- -----------------------------------------------------------------------------
-- transactions
-- Business-level record of every balance-affecting operation.
-- One transaction per deposit, withdrawal, or transfer side.
-- -----------------------------------------------------------------------------
CREATE TABLE transactions (
    id            VARCHAR(36)      NOT NULL,
    wallet_id     VARCHAR(36)      NOT NULL,
    type          txn_type         NOT NULL,
    amount        DECIMAL(19, 4)   NOT NULL,
    reference_id  VARCHAR(36)      NULL,
    status        txn_status       NOT NULL DEFAULT 'completed',
    created_at    TIMESTAMPTZ      NOT NULL DEFAULT NOW(),

    CONSTRAINT transactions_pkey PRIMARY KEY (id),

    CONSTRAINT transactions_wallet_fk
        FOREIGN KEY (wallet_id)
        REFERENCES wallets (id)
        ON DELETE RESTRICT,
        -- RESTRICT (not CASCADE) because deleting a wallet with transactions
        -- would destroy the audit trail. Deactivate wallets; never delete them.

    CONSTRAINT transactions_amount_positive CHECK (amount > 0),
        -- Amount is always stored as a positive value.
        -- The 'type' column (credit/debit) provides the direction.

    CONSTRAINT transactions_reference_id_unique UNIQUE (reference_id)
        -- Database-level idempotency guard.
        -- The application checks Redis first (fast path), but if Redis is down
        -- or a race condition occurs, this unique constraint ensures the DB
        -- will reject a duplicate transaction with a constraint violation
        -- rather than silently double-processing it.
        -- NULL values are not considered duplicates in PostgreSQL UNIQUE indexes.
);

COMMENT ON TABLE transactions IS
    'Immutable business-level record of every balance movement. '
    'Never updated or deleted after creation. '
    'reference_id is the client idempotency key — unique index prevents duplicates.';

COMMENT ON COLUMN transactions.reference_id IS
    'Client-supplied idempotency key (UUID). UNIQUE index provides database-level '
    'duplicate prevention independent of the application cache layer.';

-- -----------------------------------------------------------------------------
-- ledger_entries
-- Double-entry accounting record. Every transaction produces exactly two entries:
-- one debit (the source) and one credit (the destination).
-- The sum of all ledger entries for a wallet must always equal its balance.
-- -----------------------------------------------------------------------------
CREATE TABLE ledger_entries (
    id              VARCHAR(36)      NOT NULL,
    transaction_id  VARCHAR(36)      NOT NULL,
    wallet_id       VARCHAR(36)      NOT NULL,
    direction       ledger_dir       NOT NULL,
    amount          DECIMAL(19, 4)   NOT NULL,
    running_balance DECIMAL(19, 4)   NOT NULL,
    recorded_at     TIMESTAMPTZ      NOT NULL DEFAULT NOW(),

    CONSTRAINT ledger_entries_pkey PRIMARY KEY (id),

    CONSTRAINT ledger_entries_transaction_fk
        FOREIGN KEY (transaction_id)
        REFERENCES transactions (id)
        ON DELETE RESTRICT,

    CONSTRAINT ledger_entries_wallet_fk
        FOREIGN KEY (wallet_id)
        REFERENCES wallets (id)
        ON DELETE RESTRICT,

    CONSTRAINT ledger_entries_amount_positive CHECK (amount > 0),

    CONSTRAINT ledger_entries_running_balance_non_negative
        CHECK (running_balance >= 0)
);

COMMENT ON TABLE ledger_entries IS
    'Append-only double-entry accounting ledger. '
    'Never updated or deleted. '
    'Running sum of all entries for a wallet must equal wallets.balance. '
    'Used for audit, reconciliation, and balance reconstruction.';

COMMENT ON COLUMN ledger_entries.running_balance IS
    'Wallet balance immediately after this entry was applied. '
    'Enables balance-at-point-in-time queries without replaying the full ledger.';
