-- Oscilloscope guided tests, reference expectations, and saved captures, ADR-011.
-- Non-destructive forward migration 6. Migration 1 already shipped an earlier scope table shape:
-- preserve every row in temporary, constraint-free backups while installing the ADR-011 contract.
-- DatabaseInitializer backs up an existing database before running this migration; the whole
-- rebuild is inside SqliteMigrator's transaction and rolls back together on any error.

CREATE TABLE scope_v6_waveforms_backup AS
SELECT id, work_order_id, scope_device, channel_config_json, timebase, sample_rate, sample_count,
       data_path, guided_test_id, reference_waveform_id, measurements_json, created_at, deleted_at
FROM waveforms;

CREATE TABLE scope_v6_guided_tests_backup AS
SELECT id, name, equipment_model_id, system, description, procedure, safety_warning_ids,
       reference_waveform_id, created_at, updated_at
FROM guided_tests;

CREATE TABLE scope_v6_reference_waveforms_backup AS
SELECT id, name, equipment_model_id, system, test_name, expected_json, data_path,
       source, citation, created_at, updated_at
FROM reference_waveforms;

-- Children are removed before their parents so legacy rows cannot trip a transient FK violation.
DROP TABLE waveforms;
DROP TABLE guided_tests;
DROP TABLE reference_waveforms;

CREATE TABLE reference_waveforms (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  equipment_model_id TEXT REFERENCES equipment_models(id),
  system TEXT,
  test_name TEXT,
  expected_json TEXT,
  data_path TEXT,                       -- NULL when no known-good sample exists yet (no real captures)
  source TEXT NOT NULL,                 -- 'manual' | 'real_capture' (WaveformSource code)
  citation TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);

INSERT INTO reference_waveforms (
  id, name, equipment_model_id, system, test_name, expected_json, data_path,
  source, citation, created_at, updated_at)
SELECT id, name, equipment_model_id, system, test_name, expected_json, data_path,
       source, citation, created_at, updated_at
FROM scope_v6_reference_waveforms_backup;

CREATE TABLE guided_tests (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  equipment_model_id TEXT REFERENCES equipment_models(id),
  system TEXT,
  description TEXT,
  procedure TEXT,
  safety_warning_ids TEXT,
  reference_waveform_id TEXT REFERENCES reference_waveforms(id),
  scope_setup_json TEXT,
  created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE INDEX idx_guided_tests_model ON guided_tests(equipment_model_id, system);

INSERT INTO guided_tests (
  id, name, equipment_model_id, system, description, procedure, safety_warning_ids,
  reference_waveform_id, scope_setup_json, created_at, updated_at)
-- Versions 1-5 had no FK on guided_tests.reference_waveform_id. Foreign keys are ON in this transaction
-- (SqliteConnectionFactory), so a link to a missing reference is cleared instead of aborting the upgrade.
-- (Pre-release fix, Phase 6 review: an already-applied version 6 is unaffected.)
SELECT id, name, equipment_model_id, system, description, procedure, safety_warning_ids,
       CASE WHEN reference_waveform_id IN (SELECT id FROM reference_waveforms) THEN reference_waveform_id END,
       NULL, created_at, updated_at
FROM scope_v6_guided_tests_backup;

CREATE TABLE waveforms (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  scope_device TEXT NOT NULL,           -- ScopeDeviceKind code: 'picoscope' | 'hantek' | 'simulator'
  channel_config_json TEXT NOT NULL,
  timebase REAL,
  sample_rate INTEGER,
  sample_count INTEGER,
  data_path TEXT NOT NULL,
  guided_test_id TEXT REFERENCES guided_tests(id),
  reference_waveform_id TEXT REFERENCES reference_waveforms(id),
  measurements_json TEXT,
  created_at TEXT NOT NULL, deleted_at TEXT
);
CREATE INDEX idx_waveforms_wo ON waveforms(work_order_id);

INSERT INTO waveforms (
  id, work_order_id, scope_device, channel_config_json, timebase, sample_rate, sample_count,
  data_path, guided_test_id, reference_waveform_id, measurements_json, created_at, deleted_at)
SELECT id, work_order_id, scope_device, channel_config_json, timebase, sample_rate, sample_count,
       data_path, guided_test_id, reference_waveform_id, measurements_json, created_at, deleted_at
FROM scope_v6_waveforms_backup;

DROP TABLE scope_v6_waveforms_backup;
DROP TABLE scope_v6_guided_tests_backup;
DROP TABLE scope_v6_reference_waveforms_backup;
