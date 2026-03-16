-- =============================================================================
-- V003__create_transfer_requests_table.sql
-- Digital Wallet System — transfer_requests table
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS transfer_requests CASCADE;
-- =============================================================================

-- -----------------------------------------------------------------------------
-- transfer_requests
-- Records every peer-to-peer transfer attempt, linking source and destination
-- wallets. The idempotency_key unique index is the primary duplicate-prevention
-- mechanism for the transfer endpoint.
-- -----------------------------------------------------------------------------
CREATE TABLE transfer_requests (
    id                       VARCHAR(36)      NOT NULL,
    source_wallet_id         VARCHAR(36)      NOT NULL,
    destination_wallet_id    VARCHAR(36)      NOT NULL,
    amount                   DECIMAL(19, 4)   NOT NULL,
    idempotency_key          VARCHAR(36)      NOT NULL,
    status                   transfer_status  NOT NULL DEFAULT 'completed',
    created_at               TIMESTAMPTZ      NOT NULL DEFAULT NOW(),

    CONSTRAINT transfer_requests_pkey PRIMARY KEY (id),

    CONSTRAINT transfer_source_wallet_fk
        FOREIGN KEY (source_wallet_id)
        REFERENCES wallets (id)
        ON DELETE RESTRICT,

    CONSTRAINT transfer_destination_wallet_fk
        FOREIGN KEY (destination_wallet_id)
        REFERENCES wallets (id)
        ON DELETE RESTRICT,

    -- Idempotency key must be unique — this is the database-level guarantee
    -- that the same client request cannot produce two transfer records.
    -- Combined with the application-layer Redis check (fast path), this
    -- creates a two-layer idempotency defence.
    CONSTRAINT transfer_idempotency_key_unique UNIQUE (idempotency_key),

    -- Source and destination must differ. Prevents self-transfers at the
    -- database level — the application validates this too, but the DB
    -- constraint is the authoritative enforcement.
    CONSTRAINT transfer_wallets_differ
        CHECK (source_wallet_id <> destination_wallet_id),

    CONSTRAINT transfer_amount_positive CHECK (amount > 0)
);

COMMENT ON TABLE transfer_requests IS
    'Records every peer-to-peer transfer. '
    'idempotency_key UNIQUE index prevents duplicate transfer processing. '
    'Immutable after creation — status changes are never applied retroactively.';

COMMENT ON COLUMN transfer_requests.idempotency_key IS
    'Client-supplied UUID (from X-Idempotency-Key header). '
    'UNIQUE constraint at the database level ensures exactly-once transfer execution '
    'regardless of application-layer cache availability.';
