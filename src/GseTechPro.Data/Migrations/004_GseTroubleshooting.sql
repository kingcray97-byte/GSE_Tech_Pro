-- GSE troubleshooting safety warnings and reading provenance, ADR-009.
-- Additive migration 4. Existing readings are real/manual by default; no user data is rewritten.

CREATE TABLE IF NOT EXISTS safety_warnings (
  id TEXT PRIMARY KEY,                  -- kebab-case slug from safety_warnings.json
  category TEXT NOT NULL,               -- SafetyWarningCategory code
  text TEXT NOT NULL,                   -- the warning text shown before the step
  source_citation TEXT,                 -- manual+page / OSHA / 'TODO: needs spec from manual'
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX IF NOT EXISTS idx_safety_warnings_category ON safety_warnings(category);

-- ADR-009 provenance rule for application writers:
-- manual, typed, voice-entered, and guided readings use is_simulated = 0. A reading recorded
-- from a recording replay or scan must set is_simulated = (recordings.source = 'simulated').
-- The flag records provenance, not measurement quality; simulated readings still compare to spec.
ALTER TABLE readings ADD COLUMN is_simulated INTEGER NOT NULL DEFAULT 0;
