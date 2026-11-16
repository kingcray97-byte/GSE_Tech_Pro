-- GSE Tech Pro schema, ARCHITECTURE.md section 2.2.
-- Additive migration 1. Never edit after release; add a new migration instead.

CREATE TABLE shops (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  default_unit_system TEXT NOT NULL DEFAULT 'imperial',
  default_locale TEXT,
  timezone TEXT,
  address TEXT,
  phone TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE technicians (
  id TEXT PRIMARY KEY,
  shop_id TEXT NOT NULL REFERENCES shops(id),
  display_name TEXT NOT NULL,
  pin_hash TEXT,
  role TEXT,
  active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_technicians_shop ON technicians(shop_id);

CREATE TABLE app_settings (
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE equipment_models (
  id TEXT PRIMARY KEY,
  make TEXT NOT NULL,
  model TEXT NOT NULL,
  category TEXT,
  engine_make TEXT,
  engine_model TEXT,
  transmission TEXT,
  fuel_type TEXT,
  system_notes TEXT,
  default_unit_system TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_equipment_models_mm ON equipment_models(make, model);

CREATE TABLE assets (
  id TEXT PRIMARY KEY,
  asset_kind TEXT NOT NULL DEFAULT 'equipment',
  asset_number TEXT NOT NULL UNIQUE,
  name TEXT,
  model_id TEXT REFERENCES equipment_models(id),
  serial_number TEXT,
  status TEXT NOT NULL DEFAULT 'in_service',
  location_station TEXT,
  hour_meter REAL,
  hour_meter_as_of TEXT,
  configuration_json TEXT,
  notes TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_assets_model ON assets(model_id);
CREATE INDEX idx_assets_status ON assets(status);

CREATE TABLE vehicles (
  asset_id TEXT PRIMARY KEY REFERENCES assets(id),
  vin TEXT UNIQUE,
  license_plate TEXT,
  fleet_number TEXT,
  year INTEGER,
  engine TEXT,
  transmission TEXT,
  odometer REAL,
  odometer_as_of TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_vehicles_vin ON vehicles(vin);

CREATE TABLE work_orders (
  id TEXT PRIMARY KEY,
  asset_id TEXT NOT NULL REFERENCES assets(id),
  technician_id TEXT REFERENCES technicians(id),
  status TEXT NOT NULL DEFAULT 'open',
  priority TEXT,
  opened_at TEXT NOT NULL,
  closed_at TEXT,
  complaint_summary TEXT,
  symptoms_summary TEXT,
  diagnosis_summary TEXT,
  repair_summary TEXT,
  labor_hours REAL,
  hours_at_open REAL,
  mileage_at_open REAL,
  equipment_snapshot_json TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_wo_asset ON work_orders(asset_id);
CREATE INDEX idx_wo_tech ON work_orders(technician_id);
CREATE INDEX idx_wo_status ON work_orders(status);
CREATE INDEX idx_wo_opened ON work_orders(opened_at);

CREATE TABLE symptoms (
  id TEXT PRIMARY KEY,
  code TEXT UNIQUE,
  name TEXT NOT NULL,
  category TEXT,
  system TEXT,
  description TEXT,
  is_standard INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX idx_symptoms_category ON symptoms(category);

CREATE TABLE complaints (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  text TEXT NOT NULL,
  recorded_by TEXT NOT NULL DEFAULT 'text',
  recorded_at TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX idx_complaints_wo ON complaints(work_order_id);

CREATE TABLE work_order_symptoms (
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  symptom_id TEXT NOT NULL REFERENCES symptoms(id),
  noted_at TEXT NOT NULL,
  notes TEXT,
  PRIMARY KEY (work_order_id, symptom_id)
);

CREATE TABLE test_points (
  id TEXT PRIMARY KEY,
  equipment_model_id TEXT NOT NULL REFERENCES equipment_models(id),
  name TEXT NOT NULL,
  system TEXT,
  location TEXT,
  reading_type TEXT NOT NULL,
  expected_unit TEXT,
  spec_min REAL,
  spec_max REAL,
  spec_source TEXT,
  procedure_ref TEXT,
  safety_warning_ids TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_test_points_model ON test_points(equipment_model_id);

CREATE TABLE readings (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  test_point_id TEXT REFERENCES test_points(id),
  reading_type TEXT NOT NULL,
  system TEXT,
  channel_component TEXT,
  value_canonical REAL NOT NULL,
  unit_canonical TEXT NOT NULL,
  entered_value REAL,
  entered_unit TEXT,
  spec_min REAL,
  spec_max REAL,
  spec_unit TEXT,
  spec_source TEXT,
  pass_fail TEXT,
  source TEXT NOT NULL DEFAULT 'manual',
  entered_at TEXT NOT NULL,
  entered_by TEXT,
  notes TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_readings_wo ON readings(work_order_id);
CREATE INDEX idx_readings_testpoint ON readings(test_point_id);

CREATE TABLE dtc_library (
  id TEXT PRIMARY KEY,
  protocol TEXT NOT NULL,
  code TEXT NOT NULL,
  spn INTEGER,
  fmi INTEGER,
  description TEXT,
  system TEXT,
  severity TEXT,
  likely_causes TEXT,
  source_citation TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  UNIQUE (protocol, code)
);

CREATE TABLE freeze_frames (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  captured_at TEXT NOT NULL,
  pids_json TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_ff_wo ON freeze_frames(work_order_id);

CREATE TABLE dtcs (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  library_id TEXT REFERENCES dtc_library(id),
  code TEXT NOT NULL,
  protocol TEXT NOT NULL,
  spn INTEGER,
  fmi INTEGER,
  status TEXT NOT NULL DEFAULT 'active',
  description TEXT,
  severity TEXT,
  observed_at TEXT NOT NULL,
  freeze_frame_id TEXT REFERENCES freeze_frames(id),
  source TEXT NOT NULL DEFAULT 'scan',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_dtcs_wo ON dtcs(work_order_id);
CREATE INDEX idx_dtcs_code ON dtcs(code);

CREATE TABLE diagnoses (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  statement TEXT NOT NULL,
  system TEXT,
  confidence REAL,
  sources_json TEXT NOT NULL DEFAULT '[]',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_diag_wo ON diagnoses(work_order_id);

CREATE TABLE repairs (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  diagnosis_id TEXT REFERENCES diagnoses(id),
  description TEXT NOT NULL,
  system TEXT,
  labor_hours REAL,
  performed_at TEXT NOT NULL,
  performed_by TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_repairs_wo ON repairs(work_order_id);
CREATE INDEX idx_repairs_system ON repairs(system);

CREATE TABLE parts (
  id TEXT PRIMARY KEY,
  part_number TEXT NOT NULL,
  description TEXT,
  category TEXT,
  vendor TEXT,
  unit_cost REAL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX idx_parts_number ON parts(part_number);

CREATE TABLE parts_replaced (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  part_id TEXT REFERENCES parts(id),
  part_number TEXT NOT NULL,
  description TEXT,
  quantity INTEGER NOT NULL DEFAULT 1,
  vendor TEXT,
  warranty TEXT,
  installed_at TEXT NOT NULL,
  hours_at_install REAL,
  mileage_at_install REAL,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_parts_wo ON parts_replaced(work_order_id);
-- SQLite index names are database-wide, so this cannot reuse idx_parts_number.
CREATE INDEX idx_parts_replaced_number ON parts_replaced(part_number);

CREATE TABLE outcomes (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL UNIQUE REFERENCES work_orders(id),
  result TEXT NOT NULL,
  reason TEXT,
  closed_by TEXT,
  closed_at TEXT NOT NULL,
  comeback_detected INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE comebacks (
  id TEXT PRIMARY KEY,
  original_work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  comeback_work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  match_reason TEXT,
  window_days INTEGER,
  window_hours REAL,
  auto INTEGER NOT NULL DEFAULT 1,
  confirmed INTEGER NOT NULL DEFAULT 0,
  detected_at TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_comebacks_original ON comebacks(original_work_order_id);

CREATE TABLE recommendations (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  candidate_repair_id TEXT REFERENCES repairs(id),
  similarity_score REAL NOT NULL,
  success_rate REAL NOT NULL,
  evidence_json TEXT NOT NULL,
  rank INTEGER NOT NULL,
  presented INTEGER NOT NULL DEFAULT 0,
  accepted INTEGER NOT NULL DEFAULT 0,
  generated_at TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_reco_wo ON recommendations(work_order_id);

CREATE TABLE notes (
  id TEXT PRIMARY KEY,
  scope TEXT NOT NULL,
  target_id TEXT NOT NULL,
  technician_id TEXT,
  body TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_notes_scope ON notes(scope, target_id);

CREATE TABLE attachments (
  id TEXT PRIMARY KEY,
  scope TEXT NOT NULL,
  target_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  file_path TEXT NOT NULL,
  mime_type TEXT,
  size_bytes INTEGER,
  taken_at TEXT,
  caption TEXT,
  annotations_json TEXT,
  created_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_att_scope ON attachments(scope, target_id);

CREATE TABLE manuals (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  manufacturer TEXT,
  model_family TEXT,
  file_path TEXT NOT NULL,
  file_hash TEXT NOT NULL,
  page_count INTEGER,
  format TEXT NOT NULL DEFAULT 'pdf',
  status TEXT NOT NULL DEFAULT 'imported',
  imported_at TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_manuals_mfg ON manuals(manufacturer, model_family);

CREATE TABLE manual_pages (
  id INTEGER PRIMARY KEY,
  guid TEXT NOT NULL UNIQUE,
  manual_id TEXT NOT NULL REFERENCES manuals(id),
  page_number INTEGER NOT NULL,
  section TEXT,
  has_image INTEGER NOT NULL DEFAULT 0,
  thumbnail_path TEXT,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_pages_manual ON manual_pages(manual_id, page_number);

CREATE TABLE manual_chunks (
  id INTEGER PRIMARY KEY,
  guid TEXT NOT NULL UNIQUE,
  manual_id TEXT NOT NULL REFERENCES manuals(id),
  page_start INTEGER NOT NULL,
  page_end INTEGER NOT NULL,
  section TEXT,
  chunk_kind TEXT NOT NULL,
  heading TEXT,
  chunk_text TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_chunks_manual ON manual_chunks(manual_id, page_start);
CREATE INDEX idx_chunks_kind ON manual_chunks(chunk_kind);

CREATE TABLE chunk_tags (
  chunk_id INTEGER NOT NULL REFERENCES manual_chunks(id),
  tag TEXT NOT NULL,
  PRIMARY KEY (chunk_id, tag)
);

CREATE VIRTUAL TABLE manual_chunks_fts USING fts5(
  chunk_text,
  heading,
  section,
  content='manual_chunks',
  content_rowid='id',
  tokenize='unicode61'
);

CREATE TRIGGER manual_chunks_fts_insert AFTER INSERT ON manual_chunks BEGIN
  INSERT INTO manual_chunks_fts(rowid, chunk_text, heading, section)
  VALUES (new.id, new.chunk_text, new.heading, new.section);
END;

CREATE TRIGGER manual_chunks_fts_delete AFTER DELETE ON manual_chunks BEGIN
  INSERT INTO manual_chunks_fts(manual_chunks_fts, rowid, chunk_text, heading, section)
  VALUES ('delete', old.id, old.chunk_text, old.heading, old.section);
END;

CREATE TRIGGER manual_chunks_fts_update AFTER UPDATE ON manual_chunks BEGIN
  INSERT INTO manual_chunks_fts(manual_chunks_fts, rowid, chunk_text, heading, section)
  VALUES ('delete', old.id, old.chunk_text, old.heading, old.section);
  INSERT INTO manual_chunks_fts(rowid, chunk_text, heading, section)
  VALUES (new.id, new.chunk_text, new.heading, new.section);
END;

CREATE VIRTUAL TABLE symptoms_fts USING fts5(
  name,
  description,
  category,
  content='symptoms',
  content_rowid='rowid'
);

CREATE TRIGGER symptoms_fts_insert AFTER INSERT ON symptoms BEGIN
  INSERT INTO symptoms_fts(rowid, name, description, category)
  VALUES (new.rowid, new.name, new.description, new.category);
END;

CREATE TRIGGER symptoms_fts_delete AFTER DELETE ON symptoms BEGIN
  INSERT INTO symptoms_fts(symptoms_fts, rowid, name, description, category)
  VALUES ('delete', old.rowid, old.name, old.description, old.category);
END;

CREATE TRIGGER symptoms_fts_update AFTER UPDATE ON symptoms BEGIN
  INSERT INTO symptoms_fts(symptoms_fts, rowid, name, description, category)
  VALUES ('delete', old.rowid, old.name, old.description, old.category);
  INSERT INTO symptoms_fts(rowid, name, description, category)
  VALUES (new.rowid, new.name, new.description, new.category);
END;

CREATE TABLE guided_tests (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  equipment_model_id TEXT REFERENCES equipment_models(id),
  system TEXT,
  description TEXT,
  procedure TEXT,
  safety_warning_ids TEXT,
  reference_waveform_id TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE reference_waveforms (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  equipment_model_id TEXT REFERENCES equipment_models(id),
  system TEXT,
  test_name TEXT,
  expected_json TEXT,
  data_path TEXT NOT NULL,
  source TEXT NOT NULL,
  citation TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE waveforms (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  scope_device TEXT NOT NULL,
  channel_config_json TEXT NOT NULL,
  timebase REAL,
  sample_rate INTEGER,
  sample_count INTEGER,
  data_path TEXT NOT NULL,
  guided_test_id TEXT REFERENCES guided_tests(id),
  reference_waveform_id TEXT REFERENCES reference_waveforms(id),
  measurements_json TEXT,
  created_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_waveforms_wo ON waveforms(work_order_id);

CREATE TABLE troubleshooting_trees (
  id TEXT PRIMARY KEY,
  equipment_model_id TEXT NOT NULL REFERENCES equipment_models(id),
  symptom_id TEXT REFERENCES symptoms(id),
  title TEXT NOT NULL,
  system TEXT,
  status TEXT NOT NULL DEFAULT 'draft',
  source TEXT NOT NULL DEFAULT 'handwritten',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX idx_trees_model ON troubleshooting_trees(equipment_model_id, system);

CREATE TABLE troubleshooting_steps (
  id TEXT PRIMARY KEY,
  tree_id TEXT NOT NULL REFERENCES troubleshooting_trees(id),
  step_order INTEGER NOT NULL,
  instruction TEXT NOT NULL,
  question TEXT,
  reading_type TEXT,
  test_point_id TEXT REFERENCES test_points(id),
  spec_min REAL,
  spec_max REAL,
  spec_source TEXT,
  safety_warning_ids TEXT,
  next_on_pass TEXT,
  next_on_fail TEXT,
  terminal_result TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX idx_steps_tree ON troubleshooting_steps(tree_id, step_order);

CREATE TABLE sync_log (
  id TEXT PRIMARY KEY,
  entity_type TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  revision TEXT NOT NULL,
  change_type TEXT NOT NULL,
  pushed INTEGER NOT NULL DEFAULT 0,
  pushed_at TEXT,
  created_at TEXT NOT NULL
);
CREATE INDEX idx_sync_pending ON sync_log(pushed, entity_type);
