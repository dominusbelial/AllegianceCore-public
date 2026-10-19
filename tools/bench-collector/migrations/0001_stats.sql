-- No IP, names, paths, hostnames or request headers are persisted.
CREATE TABLE results (
  event_id TEXT PRIMARY KEY,
  install_id TEXT NOT NULL,
  received_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  cpu_class TEXT NOT NULL,
  gpu_class TEXT NOT NULL,
  preset TEXT NOT NULL,
  fps REAL,
  payload TEXT NOT NULL CHECK(json_valid(payload))
);
CREATE INDEX results_install ON results(install_id);
CREATE INDEX results_received ON results(received_at);
CREATE TABLE kept (
  event_id TEXT PRIMARY KEY,
  install_id TEXT NOT NULL,
  result_id TEXT NOT NULL REFERENCES results(event_id) ON DELETE CASCADE,
  received_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  payload TEXT NOT NULL CHECK(json_valid(payload))
);
CREATE INDEX kept_install ON kept(install_id);
CREATE INDEX kept_received ON kept(received_at);
-- Expired raw rows become count histograms. No IDs; exact medians for FPS rounded to 0.1.
CREATE TABLE aggregate_bins (
  cpu_class TEXT NOT NULL, gpu_class TEXT NOT NULL, preset TEXT NOT NULL,
  fps_tenth INTEGER NOT NULL, samples INTEGER NOT NULL,
  PRIMARY KEY(cpu_class, gpu_class, preset, fps_tenth)
);
