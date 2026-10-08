CREATE TABLE schema_version (
		version INTEGER PRIMARY KEY,
		applied_at DATETIME NOT NULL
	);
CREATE TABLE judge_responses (
				id TEXT PRIMARY KEY,
				timestamp DATETIME NOT NULL,
				kind TEXT NOT NULL,
				direction TEXT,
				model TEXT,
				action TEXT,
				severity TEXT,
				latency_ms INTEGER,
				parse_error TEXT,
				raw_response TEXT NOT NULL,
				request_id TEXT,
				trace_id TEXT,
				run_id TEXT,
				input_hash TEXT,
				confidence REAL,
				fail_closed_applied INTEGER NOT NULL DEFAULT 0,
				inspected_model TEXT,
				prompt_template_id TEXT,
				session_id TEXT,
				agent_instance_id TEXT,
				policy_id TEXT,
				destination_app TEXT,
				tool_name TEXT,
				tool_id TEXT,
				schema_version INTEGER,
				content_hash TEXT,
				generation INTEGER,
				binary_version TEXT,
				agent_id TEXT,
				sidecar_instance_id TEXT
			, timestamp_unix_nano INTEGER);
CREATE INDEX idx_jb_timestamp  ON judge_responses(timestamp);
CREATE INDEX idx_jb_kind       ON judge_responses(kind);
CREATE INDEX idx_jb_severity   ON judge_responses(severity);
CREATE INDEX idx_jb_request_id ON judge_responses(request_id);
CREATE INDEX idx_jb_trace_id   ON judge_responses(trace_id);
CREATE INDEX idx_jb_run_id     ON judge_responses(run_id);
CREATE TABLE legacy_judge_cutover_rows (
				source_key TEXT NOT NULL,
				legacy_id TEXT NOT NULL,
				verified_at DATETIME NOT NULL,
				PRIMARY KEY (source_key, legacy_id)
			);
CREATE TABLE legacy_judge_cutover_state (
				source_key TEXT PRIMARY KEY,
				completed_at DATETIME NOT NULL,
				verified_rows INTEGER NOT NULL
			);
CREATE INDEX idx_jb_timestamp_unix_nano ON judge_responses(timestamp_unix_nano, id);
