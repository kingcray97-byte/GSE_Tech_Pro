-- GSE Tech Pro recordings and playback, ARCHITECTURE.md section 2.2.
-- Additive migration 2. Raw frames stay local; only recording metadata syncs.

CREATE TABLE recordings (
  id TEXT PRIMARY KEY,
  work_order_id TEXT NOT NULL REFERENCES work_orders(id),
  asset_id TEXT REFERENCES assets(id),
  technician_id TEXT REFERENCES technicians(id),
  kind TEXT NOT NULL DEFAULT 'live_data',
  source TEXT NOT NULL DEFAULT 'real',
  adapter_name TEXT NOT NULL,
  protocol TEXT NOT NULL,
  pid_names_json TEXT NOT NULL,
  trigger_json TEXT,
  started_at TEXT NOT NULL,
  ended_at TEXT,
  status TEXT NOT NULL DEFAULT 'recording',
  frame_count INTEGER NOT NULL DEFAULT 0,
  notes TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT
);
CREATE INDEX idx_recordings_wo ON recordings(work_order_id);
CREATE INDEX idx_recordings_asset ON recordings(asset_id);

CREATE TABLE recording_frames (
  id INTEGER PRIMARY KEY,
  guid TEXT NOT NULL UNIQUE,
  recording_id TEXT NOT NULL REFERENCES recordings(id),
  sequence INTEGER NOT NULL,
  captured_at TEXT NOT NULL,
  pids_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  UNIQUE (recording_id, sequence)
);
CREATE INDEX idx_recording_frames_recording ON recording_frames(recording_id, sequence);
