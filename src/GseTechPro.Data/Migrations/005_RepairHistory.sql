-- GSE repair-history learning data needs, ADR-010 §8. Additive migration 5.

ALTER TABLE work_orders ADD COLUMN number TEXT;
CREATE UNIQUE INDEX idx_wo_number ON work_orders(number) WHERE number IS NOT NULL;
CREATE INDEX idx_wo_closed ON work_orders(closed_at);
