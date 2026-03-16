-- =============================================================================
-- V001__create_wallets_table.sql
-- Digital Wallet System — Initial schema: custom types + wallets table
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS wallets CASCADE;
--   DROP TYPE IF EXISTS wallet_status;
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Custom enum types
-- Enums enforce valid values at the database level, independently of the
-- application layer. A bug that tries to write status = 'ACTIV' (typo) is
-- rejected by PostgreSQL, not silently stored.
-- -----------------------------------------------------------------------------
CREATE TYPE wallet_status AS ENUM ('active', 'inactive');
CREATE TYPE txn_type     AS ENUM ('credit', 'debit');
CREATE TYPE txn_status   AS ENUM ('pending', 'completed', 'failed');
CREATE TYPE ledger_dir   AS ENUM ('credit', 'debit');
CREATE TYPE transfer_status AS ENUM ('pending', 'completed', 'failed');

-- -----------------------------------------------------------------------------
-- wallets
-- Core financial entity. One row per user per currency.
-- -----------------------------------------------------------------------------
CREATE TABLE wallets (
    id          VARCHAR(36)      NOT NULL,
    user_id     VARCHAR(36)      NOT NULL,
    currency    CHAR(3)          NOT NULL,
    balance     DECIMAL(19, 4)   NOT NULL DEFAULT 0,
    status      wallet_status    NOT NULL DEFAULT 'active',
    version     INTEGER          NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ      NOT NULL DEFAULT NOW(),
    updated_at  TIMESTAMPTZ      NOT NULL DEFAULT NOW(),

    CONSTRAINT wallets_pkey PRIMARY KEY (id),

    -- Enforce one wallet per user per currency at the database level.
    -- Application logic checks first, but this unique index is the safety net
    -- that prevents duplicates even under concurrent inserts.
    CONSTRAINT wallets_user_currency_unique UNIQUE (user_id, currency),

    -- DECIMAL(19,4) is used — never FLOAT or DOUBLE PRECISION.
    -- Reason: IEEE 754 floating-point cannot exactly represent most decimal
    -- fractions. 0.10 + 0.20 in binary floating-point = 0.30000000000000004.
    -- For financial data, even a sub-cent rounding error compounds across
    -- millions of transactions. DECIMAL stores the exact value.
    --
    -- 19 integer digits: supports balances up to 999,999,999,999,999.9999
    -- 4 decimal places: supports sub-cent precision for currencies that need it
    CONSTRAINT wallets_balance_non_negative CHECK (balance >= 0),

    CONSTRAINT wallets_currency_format
        CHECK (currency ~ '^[A-Z]{3}$')
);

COMMENT ON TABLE wallets IS
    'Core financial entity. One wallet per user per currency. '
    'balance is the confirmed, committed balance. '
    'version is the optimistic lock counter — incremented on every balance change.';

COMMENT ON COLUMN wallets.balance IS
    'DECIMAL(19,4) — not FLOAT. Floating-point cannot represent money exactly. '
    'Always positive; zero is the minimum (enforced by CHECK constraint).';

COMMENT ON COLUMN wallets.version IS
    'Optimistic lock. UPDATE wallets SET ... WHERE id = $1 AND version = $expected '
    'returns 0 rows if another transaction has already updated this wallet, '
    'signalling a conflict that the application must handle by retrying.';
