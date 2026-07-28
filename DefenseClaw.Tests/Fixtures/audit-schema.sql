CREATE TABLE audit_events (
    id TEXT PRIMARY KEY,
    timestamp DATETIME NOT NULL,
    action TEXT NOT NULL,
    target TEXT,
    actor TEXT NOT NULL DEFAULT 'defenseclaw',
    details TEXT,
    structured_json TEXT,
    severity TEXT,
    run_id TEXT
, connector TEXT, step_idx INTEGER, enforced INTEGER, rule_pack_dir TEXT, bucket TEXT, event_name TEXT, trace_id TEXT, request_id TEXT, session_id TEXT, agent_name TEXT, agent_instance_id TEXT, policy_id TEXT, destination_app TEXT, tool_name TEXT, tool_id TEXT, schema_version INTEGER, content_hash TEXT, generation INTEGER, binary_version TEXT, agent_id TEXT, sidecar_instance_id TEXT, source TEXT, signal TEXT, bucket_catalog_version INTEGER, payload_json TEXT, projected_record_json TEXT, record_schema_version INTEGER, projection_hash TEXT, redaction_profile TEXT, mandatory INTEGER, turn_id TEXT, evaluation_id TEXT, scan_id TEXT, finding_id TEXT, enforcement_action_id TEXT, payload_hmac TEXT, integrity_algorithm TEXT, integrity_key_id TEXT, retention_timestamp_unix_nano INTEGER);

CREATE TABLE scan_results (
    id TEXT PRIMARY KEY,
    scanner TEXT NOT NULL,
    target TEXT NOT NULL,
    timestamp DATETIME NOT NULL,
    duration_ms INTEGER,
    finding_count INTEGER,
    max_severity TEXT,
    raw_json TEXT,
    run_id TEXT
, verdict TEXT, exit_code INTEGER, error TEXT, schema_version INTEGER, content_hash TEXT, generation INTEGER, binary_version TEXT, agent_id TEXT, agent_instance_id TEXT, sidecar_instance_id TEXT, session_id TEXT, request_id TEXT, trace_id TEXT, evaluation_id TEXT, retention_timestamp_unix_nano INTEGER);

CREATE TABLE findings (
    id TEXT PRIMARY KEY,
    scan_id TEXT NOT NULL,
    severity TEXT NOT NULL,
    title TEXT NOT NULL,
    description TEXT,
    location TEXT,
    remediation TEXT,
    scanner TEXT NOT NULL,
    tags TEXT, rule_id TEXT, line_number INTEGER, schema_version INTEGER, content_hash TEXT, generation INTEGER, binary_version TEXT, agent_id TEXT, sidecar_instance_id TEXT,
    FOREIGN KEY (scan_id) REFERENCES scan_results(id)
);

CREATE TABLE actions (
    id TEXT PRIMARY KEY,
    target_type TEXT NOT NULL,
    target_name TEXT NOT NULL,
    source_path TEXT,
    actions_json TEXT NOT NULL DEFAULT '{}',
    reason TEXT,
    updated_at DATETIME NOT NULL,
    connector TEXT NOT NULL DEFAULT ''
, schema_version INTEGER, content_hash TEXT, generation INTEGER, binary_version TEXT, sidecar_instance_id TEXT);

CREATE TABLE quarantine_records (
    id TEXT PRIMARY KEY,
    target_type TEXT NOT NULL,
    target_name TEXT NOT NULL,
    original_path TEXT NOT NULL,
    quarantine_path TEXT NOT NULL UNIQUE,
    content_hash TEXT NOT NULL,
    reason TEXT,
    state TEXT NOT NULL DEFAULT 'pending',
    ownership_json TEXT NOT NULL DEFAULT '{}',
    restore_path TEXT,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL
);

CREATE TABLE quarantine_record_connectors (
    quarantine_id TEXT NOT NULL,
    connector TEXT NOT NULL DEFAULT '',
    associated_at DATETIME NOT NULL,
    PRIMARY KEY (quarantine_id, connector),
    FOREIGN KEY (quarantine_id) REFERENCES quarantine_records(id) ON DELETE CASCADE
);

CREATE TABLE network_egress_events (
    id TEXT PRIMARY KEY,
    timestamp DATETIME NOT NULL,
    session_id TEXT,
    hostname TEXT NOT NULL,
    url TEXT,
    http_method TEXT,
    protocol TEXT,
    policy_outcome TEXT NOT NULL,
    decision_code TEXT,
    blocked INTEGER NOT NULL DEFAULT 0,
    severity TEXT NOT NULL DEFAULT 'INFO',
    details TEXT
, schema_version INTEGER, content_hash TEXT, generation INTEGER, binary_version TEXT, sidecar_instance_id TEXT, connector TEXT, agent_id TEXT, agent_lifecycle_id TEXT, agent_execution_id TEXT, user_id TEXT, tool_id TEXT, root_agent_id TEXT, parent_agent_id TEXT, root_session_id TEXT, retention_timestamp_unix_nano INTEGER);

CREATE TABLE target_snapshots (
    id TEXT PRIMARY KEY,
    target_type TEXT NOT NULL,
    target_path TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    dependency_hashes TEXT,
    config_hashes TEXT,
    network_endpoints TEXT,
    scan_id TEXT,
    captured_at DATETIME NOT NULL, schema_version INTEGER, generation INTEGER, binary_version TEXT, sidecar_instance_id TEXT, scanner_fingerprint TEXT NOT NULL DEFAULT '',
    UNIQUE(target_type, target_path)
);

CREATE INDEX idx_audit_timestamp ON audit_events(timestamp);

CREATE INDEX idx_audit_action ON audit_events(action);

CREATE INDEX idx_audit_action_timestamp ON audit_events(action, timestamp DESC);

CREATE INDEX idx_audit_severity_timestamp ON audit_events(severity, timestamp);

CREATE INDEX idx_scan_scanner ON scan_results(scanner);

CREATE INDEX idx_scan_timestamp ON scan_results(timestamp);

CREATE INDEX idx_scan_scanner_target_timestamp
    ON scan_results(scanner, target, timestamp DESC);

CREATE INDEX idx_finding_severity ON findings(severity);

CREATE INDEX idx_finding_scan ON findings(scan_id);

CREATE INDEX idx_egress_timestamp ON network_egress_events(timestamp);

CREATE INDEX idx_quarantine_target
    ON quarantine_records(target_type, target_name, state);

CREATE INDEX idx_quarantine_connector
    ON quarantine_record_connectors(connector, quarantine_id);

CREATE INDEX idx_egress_hostname ON network_egress_events(hostname);

CREATE INDEX idx_egress_blocked ON network_egress_events(blocked);

CREATE INDEX idx_egress_session ON network_egress_events(session_id);

CREATE INDEX idx_snapshots_target ON target_snapshots(target_type, target_path);

CREATE INDEX idx_audit_run_id ON audit_events(run_id);

CREATE INDEX idx_scan_run_id ON scan_results(run_id);

CREATE INDEX idx_audit_connector ON audit_events(connector);

CREATE INDEX idx_audit_action_connector_timestamp ON audit_events(action, connector, timestamp DESC);

CREATE INDEX idx_audit_bucket_timestamp
                ON audit_events(bucket, timestamp);

CREATE INDEX idx_audit_event_name_timestamp
                ON audit_events(event_name, timestamp);

CREATE TABLE alert_acknowledgement_projection (
                alert_id TEXT PRIMARY KEY,
                disposition TEXT NOT NULL CHECK (disposition IN ('acknowledged','dismissed')),
                actor TEXT NOT NULL,
                disposition_at DATETIME NOT NULL,
                projection_version INTEGER NOT NULL CHECK (projection_version > 0),
                source TEXT NOT NULL CHECK (source IN ('modern','legacy_ack')),
                source_event_id TEXT NOT NULL,
                updated_at DATETIME NOT NULL
            );

CREATE UNIQUE INDEX idx_actions_type_name_conn ON actions(target_type, target_name, connector);

CREATE TABLE activity_events (
    id TEXT PRIMARY KEY,
    timestamp DATETIME NOT NULL,
    actor TEXT NOT NULL,
    action TEXT NOT NULL,
    target_type TEXT NOT NULL,
    target_id TEXT NOT NULL,
    reason TEXT,
    before_json TEXT,
    after_json TEXT,
    diff_json TEXT,
    version_from TEXT,
    version_to TEXT,
    request_id TEXT,
    trace_id TEXT,
    run_id TEXT,
    schema_version INTEGER,
    content_hash TEXT,
    generation INTEGER,
    binary_version TEXT,
    agent_id TEXT,
    sidecar_instance_id TEXT
, retention_timestamp_unix_nano INTEGER);

CREATE INDEX idx_activity_timestamp ON activity_events(timestamp);

CREATE INDEX idx_activity_actor ON activity_events(actor);

CREATE INDEX idx_activity_action ON activity_events(action);

CREATE INDEX idx_activity_target ON activity_events(target_type, target_id);

CREATE INDEX idx_activity_generation ON activity_events(generation);

CREATE TABLE sink_health (
    id TEXT PRIMARY KEY,
    timestamp DATETIME NOT NULL,
    sink_name TEXT NOT NULL,
    sink_kind TEXT NOT NULL,
    outcome TEXT NOT NULL,
    status_code INTEGER,
    latency_ms INTEGER,
    batch_size INTEGER,
    error TEXT,
    queue_depth INTEGER,
    dropped_count INTEGER,
    schema_version INTEGER,
    content_hash TEXT,
    generation INTEGER,
    binary_version TEXT,
    sidecar_instance_id TEXT
, retention_timestamp_unix_nano INTEGER);

CREATE INDEX idx_sink_health_timestamp ON sink_health(timestamp);

CREATE INDEX idx_sink_health_sink ON sink_health(sink_name);

CREATE INDEX idx_sink_health_outcome ON sink_health(outcome);

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
				raw_response TEXT NOT NULL
			, request_id TEXT, trace_id TEXT, run_id TEXT, input_hash TEXT, confidence REAL, fail_closed_applied INTEGER NOT NULL DEFAULT 0, inspected_model TEXT, prompt_template_id TEXT, schema_version INTEGER, content_hash TEXT, generation INTEGER, binary_version TEXT, agent_id TEXT, sidecar_instance_id TEXT, session_id TEXT, agent_instance_id TEXT, policy_id TEXT, destination_app TEXT, tool_name TEXT, tool_id TEXT, timestamp_unix_nano INTEGER);

CREATE INDEX idx_judge_timestamp ON judge_responses(timestamp);

CREATE INDEX idx_judge_kind ON judge_responses(kind);

CREATE INDEX idx_judge_severity ON judge_responses(severity);

CREATE INDEX idx_audit_trace_id ON audit_events(trace_id);

CREATE INDEX idx_audit_request_id ON audit_events(request_id);

CREATE INDEX idx_judge_request_id ON judge_responses(request_id);

CREATE INDEX idx_judge_trace_id ON judge_responses(trace_id);

CREATE INDEX idx_judge_run_id ON judge_responses(run_id);

CREATE INDEX idx_audit_session_id ON audit_events(session_id);

CREATE INDEX idx_audit_agent_instance_id ON audit_events(agent_instance_id);

CREATE INDEX idx_audit_policy_id ON audit_events(policy_id);

CREATE INDEX idx_audit_tool_name ON audit_events(tool_name);

CREATE INDEX idx_audit_agent_id ON audit_events(agent_id);

CREATE INDEX idx_audit_generation ON audit_events(generation);

CREATE INDEX idx_audit_sidecar_instance_id ON audit_events(sidecar_instance_id);

CREATE TABLE scan_findings (
				id TEXT PRIMARY KEY,
				scan_id TEXT NOT NULL,
				scanner TEXT NOT NULL,
				target TEXT NOT NULL,
				rule_id TEXT,
				category TEXT,
				severity TEXT NOT NULL,
				title TEXT,
				description TEXT,
				location TEXT,
				line_number INTEGER,
				remediation TEXT,
				tags TEXT,
				timestamp DATETIME NOT NULL,
				run_id TEXT,
				request_id TEXT,
				session_id TEXT,
				agent_id TEXT,
				agent_instance_id TEXT,
				sidecar_instance_id TEXT,
				schema_version INTEGER,
				content_hash TEXT,
				generation INTEGER,
				binary_version TEXT
			, data_axis TEXT, tool_capability_class TEXT, content_fingerprint TEXT, external_endpoint TEXT, turn_id INTEGER, decision_path TEXT, confidence REAL, evaluation_id TEXT, retention_timestamp_unix_nano INTEGER, evidence_summary TEXT);

CREATE INDEX idx_scan_findings_scan_id ON scan_findings(scan_id);

CREATE INDEX idx_scan_findings_scanner ON scan_findings(scanner);

CREATE INDEX idx_scan_findings_severity ON scan_findings(severity);

CREATE INDEX idx_scan_findings_rule_id ON scan_findings(rule_id);

CREATE INDEX idx_scan_findings_timestamp ON scan_findings(timestamp);

CREATE INDEX idx_scan_findings_agent_id ON scan_findings(agent_id);

CREATE INDEX idx_scan_agent_id ON scan_results(agent_id);

CREATE INDEX idx_scan_generation ON scan_results(generation);

CREATE INDEX idx_findings_agent_id ON findings(agent_id);

CREATE INDEX idx_scan_findings_session_turn ON scan_findings(session_id, agent_instance_id, turn_id);

CREATE INDEX idx_scan_findings_evaluation_id ON scan_findings(evaluation_id);

CREATE INDEX idx_scan_results_evaluation_id ON scan_results(evaluation_id);

CREATE INDEX idx_egress_agent ON network_egress_events(agent_id);

CREATE INDEX idx_egress_lifecycle ON network_egress_events(agent_lifecycle_id);

CREATE INDEX idx_egress_user ON network_egress_events(user_id);

CREATE INDEX idx_egress_root_agent ON network_egress_events(root_agent_id);

CREATE INDEX idx_audit_source_timestamp ON audit_events(source, timestamp);

CREATE INDEX idx_audit_turn_id ON audit_events(turn_id);

CREATE INDEX idx_audit_evaluation_id ON audit_events(evaluation_id);

CREATE INDEX idx_audit_scan_id ON audit_events(scan_id);

CREATE INDEX idx_audit_finding_id ON audit_events(finding_id);

CREATE INDEX idx_audit_enforcement_action_id ON audit_events(enforcement_action_id);

CREATE INDEX idx_judge_timestamp_unix_nano ON judge_responses(timestamp_unix_nano, id);

CREATE TABLE alert_acknowledgement_operations (
				operation_id TEXT PRIMARY KEY,
				command_fingerprint TEXT NOT NULL,
				alert_id TEXT NOT NULL,
				requested_disposition TEXT NOT NULL CHECK (requested_disposition IN ('acknowledged','dismissed')),
				actor TEXT NOT NULL,
				expected_projection_version INTEGER NOT NULL CHECK (expected_projection_version >= 0),
				outcome TEXT NOT NULL CHECK (outcome IN ('applied','no_change','rejected')),
				rejection_reason TEXT,
				observed_projection_version INTEGER NOT NULL CHECK (observed_projection_version >= 0),
				projection_version_before INTEGER NOT NULL CHECK (projection_version_before >= 0),
				projection_version_after INTEGER NOT NULL CHECK (projection_version_after >= 0),
				event_id TEXT NOT NULL UNIQUE,
				created_at DATETIME NOT NULL,
				CHECK (observed_projection_version = projection_version_before),
				CHECK (
					(outcome = 'applied' AND rejection_reason IS NULL AND
					 projection_version_after = projection_version_before + 1) OR
					(outcome = 'no_change' AND rejection_reason IS NULL AND
					 projection_version_after = projection_version_before) OR
					(outcome = 'rejected' AND rejection_reason IN
					 ('stale_projection_version','idempotency_conflict') AND
					 projection_version_after = projection_version_before)
				)
			);

CREATE INDEX idx_alert_ack_operations_alert
				ON alert_acknowledgement_operations(alert_id, created_at);

CREATE INDEX idx_alert_ack_operations_replay
				ON alert_acknowledgement_operations(
					alert_id, outcome, projection_version_after, event_id
				);

CREATE TABLE alert_acknowledgement_baselines (
				alert_id TEXT PRIMARY KEY,
				baseline_version INTEGER NOT NULL CHECK (baseline_version = 1),
				disposition TEXT NOT NULL CHECK (disposition = 'acknowledged'),
				actor TEXT NOT NULL,
				disposition_at DATETIME NOT NULL,
				legacy_event_id TEXT NOT NULL UNIQUE,
				raw_legacy_severity TEXT NOT NULL CHECK (raw_legacy_severity = 'ACK'),
				legacy_original_severity TEXT NOT NULL CHECK (legacy_original_severity = 'unknown'),
				timestamp_provenance TEXT NOT NULL,
				created_at DATETIME NOT NULL
			);

CREATE TABLE alert_acknowledgement_health (
				alert_id TEXT PRIMARY KEY,
				code TEXT NOT NULL,
				health_event_id TEXT NOT NULL UNIQUE,
				detected_at DATETIME NOT NULL
			);

CREATE TRIGGER alert_ack_operations_no_update
				BEFORE UPDATE ON alert_acknowledgement_operations
				BEGIN SELECT RAISE(ABORT, 'alert acknowledgement operation history is immutable'); END;

CREATE TRIGGER alert_ack_operations_no_delete
				BEFORE DELETE ON alert_acknowledgement_operations
				BEGIN SELECT RAISE(ABORT, 'alert acknowledgement operation history is immutable'); END;

CREATE TRIGGER alert_ack_baselines_no_update
				BEFORE UPDATE ON alert_acknowledgement_baselines
				BEGIN SELECT RAISE(ABORT, 'alert acknowledgement baseline history is immutable'); END;

CREATE TRIGGER alert_ack_baselines_no_delete
				BEFORE DELETE ON alert_acknowledgement_baselines
				BEGIN SELECT RAISE(ABORT, 'alert acknowledgement baseline history is immutable'); END;

CREATE TABLE observability_store_readiness (
				id INTEGER PRIMARY KEY CHECK (id = 1),
				verification_generation INTEGER NOT NULL CHECK (verification_generation >= 0),
				last_verified_at DATETIME NOT NULL
			);

CREATE INDEX idx_retention_audit_events_timestamp ON audit_events(retention_timestamp_unix_nano, id);

CREATE INDEX idx_retention_activity_events_timestamp ON activity_events(retention_timestamp_unix_nano, id);

CREATE INDEX idx_retention_network_egress_timestamp ON network_egress_events(retention_timestamp_unix_nano, id);

CREATE INDEX idx_retention_sink_health_timestamp ON sink_health(retention_timestamp_unix_nano, id);

CREATE INDEX idx_retention_scan_findings_timestamp ON scan_findings(retention_timestamp_unix_nano, id);

CREATE INDEX idx_retention_scan_results_timestamp ON scan_results(retention_timestamp_unix_nano, id);

CREATE TABLE correlation_connector_instances (
			connector_instance_id TEXT PRIMARY KEY CHECK (length(connector_instance_id) = 36),
			connector TEXT NOT NULL CHECK (length(connector) BETWEEN 1 AND 64),
			export_custody TEXT NOT NULL CHECK (export_custody IN ('defenseclaw','external','hook_only')),
			profile_version TEXT NOT NULL CHECK (length(profile_version) BETWEEN 1 AND 128),
			managed_config_digest TEXT CHECK (managed_config_digest IS NULL OR
				(length(managed_config_digest) = 64 AND managed_config_digest NOT GLOB '*[^0-9a-f]*')),
			is_default INTEGER NOT NULL CHECK (is_default IN (0, 1)),
			created_time_unix_nano INTEGER NOT NULL CHECK (created_time_unix_nano > 0),
			updated_time_unix_nano INTEGER NOT NULL CHECK (updated_time_unix_nano >= created_time_unix_nano)
		);

CREATE TABLE correlation_events (
			semantic_event_id TEXT PRIMARY KEY
				CHECK (length(semantic_event_id) = 36),
			logical_group_id TEXT NOT NULL
				CHECK (length(logical_group_id) = 36),
			connector TEXT NOT NULL
				CHECK (length(connector) BETWEEN 1 AND 64),
			connector_instance_id TEXT NOT NULL
				CHECK (length(connector_instance_id) = 36),
			source_rail TEXT NOT NULL
				CHECK (source_rail IN ('hook','native_otlp','proxy','stream','internal')),
			event_name TEXT NOT NULL
				CHECK (length(event_name) BETWEEN 1 AND 128),
			source_time_unix_nano INTEGER
				CHECK (source_time_unix_nano IS NULL OR source_time_unix_nano > 0),
			received_time_unix_nano INTEGER NOT NULL
				CHECK (received_time_unix_nano > 0),
			source_event_digest TEXT
				CHECK (source_event_digest IS NULL OR
					(length(source_event_digest) = 64 AND source_event_digest NOT GLOB '*[^0-9a-f]*')),
			fingerprint_sha256 TEXT
				CHECK (fingerprint_sha256 IS NULL OR
					(length(fingerprint_sha256) = 64 AND fingerprint_sha256 NOT GLOB '*[^0-9a-f]*')),
			first_request_id TEXT
				CHECK (first_request_id IS NULL OR length(first_request_id) BETWEEN 1 AND 512),
			first_record_id TEXT
				CHECK (first_record_id IS NULL OR length(first_record_id) BETWEEN 1 AND 512),
			profile_version TEXT NOT NULL
				CHECK (length(profile_version) BETWEEN 1 AND 128),
			completeness TEXT NOT NULL
				CHECK (completeness IN ('complete','partial','unknown')),
			FOREIGN KEY (connector_instance_id) REFERENCES correlation_connector_instances(connector_instance_id) ON DELETE RESTRICT
		);

CREATE TABLE correlation_identifiers (
			identifier_id TEXT PRIMARY KEY
				CHECK (length(identifier_id) = 67 AND substr(identifier_id, 1, 3) = 'id_'),
			semantic_event_id TEXT NOT NULL,
			connector_instance_id TEXT NOT NULL CHECK (length(connector_instance_id) = 36),
			namespace TEXT NOT NULL CHECK (length(namespace) BETWEEN 1 AND 128),
			identifier_kind TEXT NOT NULL CHECK (identifier_kind IN (
				'source_event','source_sequence','source_timestamp','message','thread','prompt','step',
				'session','root_session','parent_session','child_session','turn','agent','root_agent',
				'parent_agent','child_agent','lifecycle','execution','model_request','model_response',
				'action','tool_invocation','trace','span')),
			value_digest TEXT NOT NULL
				CHECK (length(value_digest) = 64 AND value_digest NOT GLOB '*[^0-9a-f]*'),
			normalized_value TEXT NOT NULL CHECK (length(normalized_value) BETWEEN 1 AND 512),
			source_field TEXT NOT NULL CHECK (length(source_field) BETWEEN 1 AND 128),
			origin TEXT NOT NULL CHECK (origin IN ('reported','defenseclaw_minted','derived','trace_exact')),
			profile_version TEXT NOT NULL CHECK (length(profile_version) BETWEEN 1 AND 128),
			created_time_unix_nano INTEGER NOT NULL CHECK (created_time_unix_nano > 0),
			last_seen_time_unix_nano INTEGER NOT NULL CHECK (last_seen_time_unix_nano >= created_time_unix_nano),
			FOREIGN KEY (semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE CASCADE,
			FOREIGN KEY (connector_instance_id) REFERENCES correlation_connector_instances(connector_instance_id) ON DELETE RESTRICT,
			UNIQUE (connector_instance_id, namespace, identifier_kind, value_digest, semantic_event_id)
		);

CREATE TABLE correlation_observations (
			record_id TEXT PRIMARY KEY
				CHECK (length(record_id) BETWEEN 1 AND 512),
			semantic_event_id TEXT NOT NULL,
			signal TEXT NOT NULL CHECK (signal IN ('logs','traces','metrics')),
			bucket TEXT NOT NULL CHECK (length(bucket) BETWEEN 1 AND 64),
			event_name TEXT NOT NULL CHECK (length(event_name) BETWEEN 1 AND 128),
			observed_time_unix_nano INTEGER NOT NULL CHECK (observed_time_unix_nano > 0),
			trace_id TEXT CHECK (trace_id IS NULL OR
				(length(trace_id) = 32 AND trace_id NOT GLOB '*[^0-9a-f]*')),
			span_id TEXT CHECK (span_id IS NULL OR
				(length(span_id) = 16 AND span_id NOT GLOB '*[^0-9a-f]*')),
			session_id TEXT CHECK (session_id IS NULL OR length(session_id) BETWEEN 1 AND 512),
			turn_id TEXT CHECK (turn_id IS NULL OR length(turn_id) BETWEEN 1 AND 512),
			agent_id TEXT CHECK (agent_id IS NULL OR length(agent_id) BETWEEN 1 AND 512),
			lifecycle_id TEXT CHECK (lifecycle_id IS NULL OR length(lifecycle_id) BETWEEN 1 AND 512),
			execution_id TEXT CHECK (execution_id IS NULL OR length(execution_id) BETWEEN 1 AND 512),
			model_request_id TEXT CHECK (model_request_id IS NULL OR length(model_request_id) BETWEEN 1 AND 512),
			model_response_id TEXT CHECK (model_response_id IS NULL OR length(model_response_id) BETWEEN 1 AND 512),
			tool_invocation_id TEXT CHECK (tool_invocation_id IS NULL OR length(tool_invocation_id) BETWEEN 1 AND 512),
			projection_hash TEXT CHECK (projection_hash IS NULL OR length(projection_hash) BETWEEN 64 AND 71),
			status TEXT NOT NULL CHECK (status IN ('constructed','export_eligible')),
			FOREIGN KEY (semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE CASCADE
		);

CREATE TABLE correlation_relationships (
			relationship_id TEXT PRIMARY KEY
				CHECK (length(relationship_id) = 68 AND substr(relationship_id, 1, 4) = 'rel_'),
			from_kind TEXT NOT NULL CHECK (from_kind IN (
				'semantic_event','logical_event','record','session','turn','agent','lifecycle',
				'execution','model_request','model_response','tool_invocation','trace','span')),
			from_id TEXT NOT NULL CHECK (length(from_id) BETWEEN 1 AND 512),
			to_kind TEXT NOT NULL CHECK (to_kind IN (
				'semantic_event','logical_event','record','session','turn','agent','lifecycle',
				'execution','model_request','model_response','tool_invocation','trace','span')),
			to_id TEXT NOT NULL CHECK (length(to_id) BETWEEN 1 AND 512),
			relationship_type TEXT NOT NULL CHECK (relationship_type IN (
				'same_as','duplicate_of','belongs_to','parent_of','delegated_by','caused_by',
				'invokes','responds_to','resumes','correlates_with')),
			method TEXT NOT NULL CHECK (method IN ('reported','trace_exact','derived','inferred')),
			confidence INTEGER NOT NULL CHECK (
				(method IN ('reported','trace_exact') AND confidence = 100) OR
				(method = 'derived' AND confidence = 95) OR
				(method = 'inferred' AND confidence = 50)),
			rule_id TEXT NOT NULL CHECK (length(rule_id) BETWEEN 1 AND 128),
			rule_version TEXT NOT NULL CHECK (length(rule_version) BETWEEN 1 AND 128),
			status TEXT NOT NULL CHECK (status IN ('active','candidate','superseded','rejected','conflicted')),
			created_time_unix_nano INTEGER NOT NULL CHECK (created_time_unix_nano > 0),
			last_seen_time_unix_nano INTEGER NOT NULL CHECK (last_seen_time_unix_nano >= created_time_unix_nano),
			UNIQUE (from_kind, from_id, to_kind, to_id, relationship_type, method, rule_id, rule_version)
		);

CREATE TABLE correlation_relationship_evidence (
			evidence_id TEXT PRIMARY KEY
				CHECK (length(evidence_id) = 67 AND substr(evidence_id, 1, 3) = 'ev_'),
			relationship_id TEXT NOT NULL,
			evidence_record_id TEXT,
			semantic_event_id TEXT,
			evidence_role TEXT NOT NULL CHECK (evidence_role IN ('source','target','corroborating','conflicting')),
			integrity_state TEXT NOT NULL CHECK (integrity_state IN ('verified','unverified','failed')),
			created_time_unix_nano INTEGER NOT NULL CHECK (created_time_unix_nano > 0),
			CHECK ((evidence_record_id IS NULL) <> (semantic_event_id IS NULL)),
			FOREIGN KEY (relationship_id) REFERENCES correlation_relationships(relationship_id) ON DELETE CASCADE,
			FOREIGN KEY (evidence_record_id) REFERENCES correlation_observations(record_id) ON DELETE CASCADE,
			FOREIGN KEY (semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE CASCADE
		);

CREATE TABLE correlation_cursors (
			connector_instance_id TEXT NOT NULL CHECK (length(connector_instance_id) = 36),
			session_id TEXT NOT NULL CHECK (length(session_id) BETWEEN 1 AND 512),
			agent_id TEXT NOT NULL CHECK (length(agent_id) BETWEEN 1 AND 512),
			lifecycle_id TEXT CHECK (lifecycle_id IS NULL OR length(lifecycle_id) BETWEEN 1 AND 512),
			execution_id TEXT CHECK (execution_id IS NULL OR length(execution_id) BETWEEN 1 AND 512),
			active_turn_id TEXT CHECK (active_turn_id IS NULL OR length(active_turn_id) BETWEEN 1 AND 512),
			active_prompt_id TEXT CHECK (active_prompt_id IS NULL OR length(active_prompt_id) BETWEEN 1 AND 512),
			phase TEXT NOT NULL CHECK (length(phase) BETWEEN 1 AND 64),
			sequence INTEGER NOT NULL CHECK (sequence >= 0),
			root_agent_id TEXT CHECK (root_agent_id IS NULL OR length(root_agent_id) BETWEEN 1 AND 512),
			parent_agent_id TEXT CHECK (parent_agent_id IS NULL OR length(parent_agent_id) BETWEEN 1 AND 512),
			root_session_id TEXT CHECK (root_session_id IS NULL OR length(root_session_id) BETWEEN 1 AND 512),
			parent_session_id TEXT CHECK (parent_session_id IS NULL OR length(parent_session_id) BETWEEN 1 AND 512),
			last_semantic_event_id TEXT,
			last_record_id TEXT CHECK (last_record_id IS NULL OR length(last_record_id) BETWEEN 1 AND 512),
			profile_version TEXT NOT NULL CHECK (length(profile_version) BETWEEN 1 AND 128),
			active INTEGER NOT NULL CHECK (active IN (0, 1)),
			updated_time_unix_nano INTEGER NOT NULL CHECK (updated_time_unix_nano > 0),
			PRIMARY KEY (connector_instance_id, session_id, agent_id),
			FOREIGN KEY (last_semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE RESTRICT
		);

CREATE TABLE correlation_pending_operations (
			connector_instance_id TEXT NOT NULL CHECK (length(connector_instance_id) = 36),
			operation_namespace TEXT NOT NULL CHECK (length(operation_namespace) BETWEEN 1 AND 128),
			operation_kind TEXT NOT NULL CHECK (operation_kind IN (
				'prompt','model_request','tool_invocation','action')),
			operation_id TEXT NOT NULL CHECK (length(operation_id) BETWEEN 1 AND 512),
			operation_type TEXT NOT NULL CHECK (operation_type IN ('model','tool')),
			scope_kind TEXT NOT NULL CHECK (scope_kind IN (
				'connector_instance','session','thread','turn','execution')),
			scope_id TEXT NOT NULL CHECK (length(scope_id) BETWEEN 1 AND 512),
			operation_name TEXT CHECK (operation_name IS NULL OR length(operation_name) BETWEEN 1 AND 256),
			session_id TEXT CHECK (session_id IS NULL OR length(session_id) BETWEEN 1 AND 512),
			turn_id TEXT CHECK (turn_id IS NULL OR length(turn_id) BETWEEN 1 AND 512),
			agent_id TEXT CHECK (agent_id IS NULL OR length(agent_id) BETWEEN 1 AND 512),
			execution_id TEXT CHECK (execution_id IS NULL OR length(execution_id) BETWEEN 1 AND 512),
			start_semantic_event_id TEXT NOT NULL,
			start_time_unix_nano INTEGER NOT NULL CHECK (start_time_unix_nano > 0),
			input_digest TEXT CHECK (input_digest IS NULL OR
				(length(input_digest) = 64 AND input_digest NOT GLOB '*[^0-9a-f]*')),
			terminal_semantic_event_id TEXT,
			terminal_time_unix_nano INTEGER CHECK (terminal_time_unix_nano IS NULL OR terminal_time_unix_nano > 0),
			status TEXT NOT NULL CHECK (status IN ('active','completed','failed','cancelled','unresolved')),
			updated_time_unix_nano INTEGER NOT NULL CHECK (updated_time_unix_nano > 0),
			CHECK ((status = 'active' AND terminal_semantic_event_id IS NULL AND terminal_time_unix_nano IS NULL) OR
				(status <> 'active' AND terminal_semantic_event_id IS NOT NULL AND terminal_time_unix_nano IS NOT NULL)),
			PRIMARY KEY (connector_instance_id, operation_namespace, operation_kind,
				operation_id, operation_type, scope_kind, scope_id),
			FOREIGN KEY (start_semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE RESTRICT,
			FOREIGN KEY (terminal_semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE RESTRICT
		);

CREATE TABLE correlation_identity_claims (
			connector_instance_id TEXT NOT NULL CHECK (length(connector_instance_id) = 36),
			namespace TEXT NOT NULL CHECK (length(namespace) BETWEEN 1 AND 128),
			identifier_kind TEXT NOT NULL CHECK (identifier_kind IN (
				'source_event','model_request','model_response','tool_invocation')),
			value_digest TEXT NOT NULL
				CHECK (length(value_digest) = 64 AND value_digest NOT GLOB '*[^0-9a-f]*'),
			event_name TEXT NOT NULL CHECK (length(event_name) BETWEEN 1 AND 128),
			rail_a TEXT NOT NULL CHECK (rail_a IN ('hook','native_otlp','proxy','stream')),
			rail_b TEXT NOT NULL CHECK (rail_b IN ('hook','native_otlp','proxy','stream')),
			rule_id TEXT NOT NULL CHECK (length(rule_id) BETWEEN 1 AND 128),
			rule_version TEXT NOT NULL CHECK (length(rule_version) BETWEEN 1 AND 128),
			source_rail TEXT NOT NULL CHECK (source_rail IN ('hook','native_otlp','proxy','stream')),
			semantic_event_id TEXT NOT NULL CHECK (length(semantic_event_id) = 36),
			logical_group_id TEXT NOT NULL CHECK (length(logical_group_id) = 36),
			created_time_unix_nano INTEGER NOT NULL CHECK (created_time_unix_nano > 0),
			CHECK (rail_a < rail_b),
			CHECK (source_rail = rail_a OR source_rail = rail_b),
			PRIMARY KEY (connector_instance_id, namespace, identifier_kind, value_digest,
				event_name, rail_a, rail_b, rule_id, rule_version, source_rail),
			FOREIGN KEY (connector_instance_id) REFERENCES correlation_connector_instances(connector_instance_id) ON DELETE RESTRICT,
			FOREIGN KEY (semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
		);

CREATE TABLE correlation_receipts (
			connector_instance_id TEXT NOT NULL CHECK (length(connector_instance_id) = 36),
			source_key_digest TEXT NOT NULL
				CHECK (length(source_key_digest) = 64 AND source_key_digest NOT GLOB '*[^0-9a-f]*'),
			fingerprint_sha256 TEXT NOT NULL
				CHECK (length(fingerprint_sha256) = 64 AND fingerprint_sha256 NOT GLOB '*[^0-9a-f]*'),
			semantic_event_id TEXT NOT NULL,
			conflicts_with_semantic_event_id TEXT,
			first_received_time_unix_nano INTEGER NOT NULL CHECK (first_received_time_unix_nano > 0),
			last_received_time_unix_nano INTEGER NOT NULL CHECK (last_received_time_unix_nano >= first_received_time_unix_nano),
			delivery_count INTEGER NOT NULL CHECK (delivery_count > 0),
			accepted_time_unix_nano INTEGER
				CHECK (accepted_time_unix_nano IS NULL OR accepted_time_unix_nano >= first_received_time_unix_nano),
			expires_time_unix_nano INTEGER NOT NULL CHECK (expires_time_unix_nano >= last_received_time_unix_nano),
			PRIMARY KEY (connector_instance_id, source_key_digest, fingerprint_sha256),
			FOREIGN KEY (semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE RESTRICT,
			FOREIGN KEY (conflicts_with_semantic_event_id) REFERENCES correlation_events(semantic_event_id) ON DELETE RESTRICT
		);

CREATE INDEX idx_correlation_events_received
			ON correlation_events(received_time_unix_nano, semantic_event_id);

CREATE INDEX idx_correlation_events_connector
			ON correlation_events(connector_instance_id, received_time_unix_nano);

CREATE INDEX idx_correlation_events_logical
			ON correlation_events(logical_group_id);

CREATE INDEX idx_correlation_connector_custody
			ON correlation_connector_instances(export_custody, connector);

CREATE UNIQUE INDEX idx_correlation_connector_default
			ON correlation_connector_instances(connector) WHERE is_default = 1;

CREATE INDEX idx_correlation_identifiers_exact
			ON correlation_identifiers(connector_instance_id, namespace, identifier_kind, value_digest);

CREATE INDEX idx_correlation_identifiers_event
			ON correlation_identifiers(semantic_event_id);

CREATE INDEX idx_correlation_observations_event
			ON correlation_observations(semantic_event_id, observed_time_unix_nano);

CREATE INDEX idx_correlation_observations_session
			ON correlation_observations(session_id, observed_time_unix_nano);

CREATE INDEX idx_correlation_observations_turn
			ON correlation_observations(turn_id, observed_time_unix_nano);

CREATE INDEX idx_correlation_observations_agent
			ON correlation_observations(agent_id, observed_time_unix_nano);

CREATE INDEX idx_correlation_observations_lifecycle
			ON correlation_observations(lifecycle_id, observed_time_unix_nano);

CREATE INDEX idx_correlation_observations_execution
			ON correlation_observations(execution_id, observed_time_unix_nano);

CREATE INDEX idx_correlation_observations_model_request
			ON correlation_observations(model_request_id);

CREATE INDEX idx_correlation_observations_model_response
			ON correlation_observations(model_response_id);

CREATE INDEX idx_correlation_observations_tool
			ON correlation_observations(tool_invocation_id);

CREATE INDEX idx_correlation_observations_trace
			ON correlation_observations(trace_id, span_id);

CREATE INDEX idx_correlation_relationships_from
			ON correlation_relationships(from_kind, from_id, status);

CREATE INDEX idx_correlation_relationships_to
			ON correlation_relationships(to_kind, to_id, status);

CREATE INDEX idx_correlation_relationships_status
			ON correlation_relationships(status, last_seen_time_unix_nano);

CREATE INDEX idx_correlation_relationships_seen
			ON correlation_relationships(last_seen_time_unix_nano, relationship_id);

CREATE INDEX idx_correlation_evidence_relationship
			ON correlation_relationship_evidence(relationship_id);

CREATE INDEX idx_correlation_evidence_semantic
			ON correlation_relationship_evidence(semantic_event_id);

CREATE INDEX idx_correlation_evidence_record
			ON correlation_relationship_evidence(evidence_record_id);

CREATE INDEX idx_correlation_cursors_active
			ON correlation_cursors(active, updated_time_unix_nano);

CREATE INDEX idx_correlation_cursors_event
			ON correlation_cursors(last_semantic_event_id);

CREATE INDEX idx_correlation_pending_status
			ON correlation_pending_operations(connector_instance_id, operation_namespace,
				operation_kind, scope_kind, scope_id, operation_type, status, updated_time_unix_nano);

CREATE INDEX idx_correlation_pending_exact
			ON correlation_pending_operations(connector_instance_id, operation_namespace,
				operation_kind, operation_id, operation_type, scope_kind, scope_id, status);

CREATE INDEX idx_correlation_pending_start_event
			ON correlation_pending_operations(start_semantic_event_id);

CREATE INDEX idx_correlation_pending_terminal_event
			ON correlation_pending_operations(terminal_semantic_event_id);

CREATE INDEX idx_correlation_receipts_expiry
			ON correlation_receipts(expires_time_unix_nano);

CREATE INDEX idx_correlation_receipts_source
			ON correlation_receipts(connector_instance_id, source_key_digest);

CREATE INDEX idx_correlation_receipts_event
			ON correlation_receipts(semantic_event_id);

CREATE INDEX idx_correlation_receipts_conflict_event
			ON correlation_receipts(conflicts_with_semantic_event_id);

CREATE INDEX idx_correlation_identity_claims_event
			ON correlation_identity_claims(semantic_event_id);

CREATE INDEX idx_correlation_identity_claims_logical
			ON correlation_identity_claims(logical_group_id);

CREATE TABLE runtime_asset_state (
			connector TEXT NOT NULL, session_id TEXT NOT NULL DEFAULT '',
			target_type TEXT NOT NULL, target_name TEXT NOT NULL,
			source_path TEXT NOT NULL DEFAULT '', runtime_surface TEXT NOT NULL,
			hook_event TEXT NOT NULL, provenance TEXT NOT NULL, state TEXT NOT NULL,
			first_observed_at DATETIME NOT NULL, last_observed_at DATETIME NOT NULL,
			PRIMARY KEY (connector, session_id, target_type, target_name)
		);

CREATE INDEX idx_runtime_asset_session
			ON runtime_asset_state(connector, session_id, last_observed_at);

CREATE INDEX idx_runtime_asset_target
			ON runtime_asset_state(target_type, target_name, connector);

CREATE TRIGGER retention_audit_events_timestamp_insert
		AFTER INSERT ON audit_events
		BEGIN
			UPDATE audit_events SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_audit_events_timestamp_update
		AFTER UPDATE OF timestamp ON audit_events
		BEGIN
			UPDATE audit_events SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_activity_events_timestamp_insert
		AFTER INSERT ON activity_events
		BEGIN
			UPDATE activity_events SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_activity_events_timestamp_update
		AFTER UPDATE OF timestamp ON activity_events
		BEGIN
			UPDATE activity_events SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_network_egress_events_timestamp_insert
		AFTER INSERT ON network_egress_events
		BEGIN
			UPDATE network_egress_events SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_network_egress_events_timestamp_update
		AFTER UPDATE OF timestamp ON network_egress_events
		BEGIN
			UPDATE network_egress_events SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_sink_health_timestamp_insert
		AFTER INSERT ON sink_health
		BEGIN
			UPDATE sink_health SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_sink_health_timestamp_update
		AFTER UPDATE OF timestamp ON sink_health
		BEGIN
			UPDATE sink_health SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_scan_findings_timestamp_insert
		AFTER INSERT ON scan_findings
		BEGIN
			UPDATE scan_findings SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_scan_findings_timestamp_update
		AFTER UPDATE OF timestamp ON scan_findings
		BEGIN
			UPDATE scan_findings SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_scan_results_timestamp_insert
		AFTER INSERT ON scan_results
		BEGIN
			UPDATE scan_results SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER retention_scan_results_timestamp_update
		AFTER UPDATE OF timestamp ON scan_results
		BEGIN
			UPDATE scan_results SET retention_timestamp_unix_nano = CASE
		WHEN timestamp GLOB '????-??-??T??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN substr(timestamp, -1) = 'Z' THEN 'Z' ELSE substr(timestamp, -6) END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 20 -
					CASE WHEN substr(timestamp, -1) = 'Z' THEN 1 ELSE 6 END) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND (instr(timestamp, ' +') > 0 OR instr(timestamp, ' -') > 0) THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) ||
				CASE WHEN instr(timestamp, ' +') > 0 THEN
					substr(timestamp, instr(timestamp, ' +') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' +') + 4, 2)
				ELSE
					substr(timestamp, instr(timestamp, ' -') + 1, 3) || ':' ||
					substr(timestamp, instr(timestamp, ' -') + 4, 2)
				END
			) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21,
					CASE WHEN instr(timestamp, ' +') > 0 THEN instr(timestamp, ' +')
					ELSE instr(timestamp, ' -') END - 21) || '000000000',
				1, 9) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*'
			AND substr(timestamp, -6, 1) IN ('+', '-') THEN
			CAST(strftime('%s', substr(timestamp, 1, 19) || substr(timestamp, -6)) AS INTEGER) *
				1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21, length(timestamp) - 26) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		WHEN timestamp GLOB '????-??-?? ??:??:??*' THEN
			CAST(strftime('%s', substr(timestamp, 1, 19)) AS INTEGER) * 1000000000 +
			CASE WHEN substr(timestamp, 20, 1) = '.' THEN CAST(substr(
				substr(timestamp, 21) || '000000000', 1, 9
			) AS INTEGER) ELSE 0 END
		ELSE NULL END WHERE id = NEW.id;
		END;

CREATE TRIGGER scan_findings_require_parent
				BEFORE INSERT ON scan_findings
				WHEN NOT EXISTS (SELECT 1 FROM scan_results WHERE id = NEW.scan_id)
				BEGIN
					SELECT RAISE(ABORT, 'scan finding requires an existing scan result');
				END;

CREATE TRIGGER scan_findings_update_require_parent
				BEFORE UPDATE OF scan_id ON scan_findings
				WHEN NOT EXISTS (SELECT 1 FROM scan_results WHERE id = NEW.scan_id)
				BEGIN
					SELECT RAISE(ABORT, 'scan finding requires an existing scan result');
				END;

CREATE TRIGGER scan_results_preserve_children
				BEFORE DELETE ON scan_results
				WHEN EXISTS (SELECT 1 FROM scan_findings WHERE scan_id = OLD.id)
				BEGIN
					SELECT RAISE(ABORT, 'scan result still has findings');
				END;

CREATE TRIGGER defenseclaw_judge_responses_no_insert
BEFORE INSERT ON judge_responses
BEGIN
	SELECT RAISE(ABORT, 'judge_responses is read-only after v8 cutover');
END;

CREATE TRIGGER defenseclaw_judge_responses_no_update
BEFORE UPDATE ON judge_responses
BEGIN
	SELECT RAISE(ABORT, 'judge_responses is read-only after v8 cutover');
END;
