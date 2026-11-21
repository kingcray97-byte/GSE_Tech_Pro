-- Phase 10D.1i: technician notes saved with a scope capture.
-- Additive forward migration 7: one nullable column, no existing row is changed.
ALTER TABLE waveforms ADD COLUMN notes TEXT;
