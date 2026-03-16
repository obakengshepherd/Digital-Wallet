-- =============================================================================
-- V004__add_foreign_key_constraints.sql
-- Digital Wallet System — Explicit foreign key documentation pass
--
-- All foreign keys were already defined inline in V001–V003.
-- This migration adds any deferred or cross-table constraints that could not
-- be defined inline due to forward references, and documents the ON DELETE
-- policy decisions for the entire schema.
--
-- ROLLBACK: No destructive changes — this migration is additive only.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Foreign Key Policy Summary (all tables)
-- -----------------------------------------------------------------------------
-- wallets → (no FK — user_id references an external identity service)
-- transactions → wallets (ON DELETE RESTRICT)
-- ledger_entries → transactions (ON DELETE RESTRICT)
-- ledger_entries → wallets (ON DELETE RESTRICT)
-- transfer_requests → wallets (source) (ON DELETE RESTRICT)
-- transfer_requests → wallets (destination) (ON DELETE RESTRICT)
--
-- All policies are RESTRICT (not CASCADE, not SET NULL) because:
-- 1. Financial records must never be silently orphaned or deleted.
-- 2. A wallet cannot be hard-deleted while it has financial history.
-- 3. Deactivate wallets (status = 'inactive') instead of deleting them.
-- -----------------------------------------------------------------------------

-- Verify all expected tables exist (will error if a prior migration failed)
DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_name = 'wallets'), 'wallets table missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_name = 'transactions'), 'transactions table missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_name = 'ledger_entries'), 'ledger_entries table missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_name = 'transfer_requests'), 'transfer_requests table missing';
END;
$$;

-- Add a trigger to auto-update wallets.updated_at on every UPDATE.
-- This removes the burden from the application layer — the DB always
-- maintains the updated_at timestamp accurately.
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER wallets_updated_at_trigger
    BEFORE UPDATE ON wallets
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

COMMENT ON TRIGGER wallets_updated_at_trigger ON wallets IS
    'Auto-updates updated_at on every row modification. '
    'Ensures the timestamp is accurate even if the application forgets to set it.';
