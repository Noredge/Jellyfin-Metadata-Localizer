-- Schema 1: runtime business data lives in this database only.
CREATE TABLE media_items (
 id TEXT PRIMARY KEY, server_id TEXT NOT NULL, library_id TEXT NOT NULL, item_id TEXT NOT NULL,
 media_path TEXT NOT NULL, path_evidence TEXT NOT NULL DEFAULT '', present INTEGER NOT NULL DEFAULT 1 CHECK(present IN(0,1)),
 UNIQUE(server_id,library_id,item_id)
) STRICT;
CREATE TABLE source_versions (
 id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN('Name','Overview','Genres','Term')),
 owner_id TEXT NOT NULL, content TEXT NOT NULL, content_hash TEXT NOT NULL, evidence_json TEXT NOT NULL DEFAULT '{}',
 created_utc TEXT NOT NULL, UNIQUE(kind,owner_id,content_hash)
) STRICT;
CREATE TABLE media_fields (
 media_id TEXT NOT NULL REFERENCES media_items(id), field TEXT NOT NULL CHECK(field IN('Name','Overview','Genres')),
 source_id TEXT REFERENCES source_versions(id), source_revision INTEGER NOT NULL DEFAULT 0 CHECK(source_revision>=0),
 control_revision INTEGER NOT NULL DEFAULT 0 CHECK(control_revision>=0), render_revision INTEGER NOT NULL DEFAULT 0 CHECK(render_revision>=0),
 display_prefix TEXT NOT NULL DEFAULT '', render_json TEXT NOT NULL DEFAULT '{}', availability TEXT NOT NULL CHECK(availability IN('available','empty','unreadable')),
 observed_utc TEXT NOT NULL, observed_display_hash TEXT, observation_error TEXT,
 PRIMARY KEY(media_id,field)
) STRICT;
CREATE TABLE generation_inputs (
 id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES source_versions(id), language TEXT NOT NULL CHECK(language IN('zh-Hans','en')),
 prompt_version TEXT NOT NULL, names_json TEXT NOT NULL DEFAULT '{}', context_json TEXT NOT NULL DEFAULT '{}',
 fingerprint TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL
) STRICT;
CREATE TABLE translation_versions (
 id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES source_versions(id), language TEXT NOT NULL CHECK(language IN('zh-Hans','en')),
 text TEXT NOT NULL CHECK(length(trim(text))>0), text_hash TEXT NOT NULL,
 origin TEXT NOT NULL CHECK(origin IN('model','manual','import','dictionary')), input_id TEXT REFERENCES generation_inputs(id),
 request_id TEXT REFERENCES request_attempts(id), provenance_json TEXT NOT NULL DEFAULT '{}', created_utc TEXT NOT NULL
) STRICT;
CREATE INDEX translations_history ON translation_versions(source_id,language,created_utc DESC,id);
CREATE UNIQUE INDEX translation_request ON translation_versions(request_id) WHERE request_id IS NOT NULL;
CREATE TABLE translation_selections (
 source_id TEXT NOT NULL REFERENCES source_versions(id), language TEXT NOT NULL CHECK(language IN('zh-Hans','en')),
 translation_id TEXT NOT NULL REFERENCES translation_versions(id), revision INTEGER NOT NULL CHECK(revision>0),
 PRIMARY KEY(source_id,language)
) STRICT;
CREATE TABLE genre_terms (
 id TEXT PRIMARY KEY, original TEXT NOT NULL UNIQUE, source_id TEXT NOT NULL REFERENCES source_versions(id),
 control_revision INTEGER NOT NULL DEFAULT 0 CHECK(control_revision>=0)
) STRICT;
CREATE TABLE genre_members (
 source_id TEXT NOT NULL REFERENCES source_versions(id), term_id TEXT NOT NULL REFERENCES genre_terms(id), ordinal INTEGER NOT NULL CHECK(ordinal>=0),
 PRIMARY KEY(source_id,term_id), UNIQUE(source_id,ordinal)
) STRICT;
CREATE TABLE person_name_rules (
 id TEXT PRIMARY KEY, original TEXT NOT NULL UNIQUE, zh_name TEXT, en_name TEXT, aliases_json TEXT NOT NULL DEFAULT '[]',
 revision INTEGER NOT NULL CHECK(revision>0), provenance_json TEXT NOT NULL DEFAULT '{}'
) STRICT;
CREATE TABLE connection_versions (
 id TEXT PRIMARY KEY, revision INTEGER NOT NULL UNIQUE CHECK(revision>0), endpoint TEXT NOT NULL, model TEXT NOT NULL,
 timeout_ms INTEGER NOT NULL CHECK(timeout_ms>0), requests_per_minute INTEGER NOT NULL CHECK(requests_per_minute>0),
 credential_reference TEXT, parameters_json TEXT NOT NULL DEFAULT '{}', created_utc TEXT NOT NULL
) STRICT;
CREATE TABLE settings (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL CHECK(revision>0),
 target_language TEXT NOT NULL CHECK(target_language IN('zh-Hans','en')), target_epoch INTEGER NOT NULL DEFAULT 0 CHECK(target_epoch>=0),
 title_enabled INTEGER NOT NULL CHECK(title_enabled IN(0,1)), overview_enabled INTEGER NOT NULL CHECK(overview_enabled IN(0,1)),
 genres_enabled INTEGER NOT NULL CHECK(genres_enabled IN(0,1)), auto_enabled INTEGER NOT NULL CHECK(auto_enabled IN(0,1)),
 connection_id TEXT REFERENCES connection_versions(id), baseline_established INTEGER NOT NULL DEFAULT 0 CHECK(baseline_established IN(0,1)),
 baseline_utc TEXT
) STRICT;
CREATE TABLE managed_libraries (
 library_id TEXT PRIMARY KEY, baseline_established INTEGER NOT NULL DEFAULT 0 CHECK(baseline_established IN(0,1)),
 baseline_utc TEXT, imported_baseline INTEGER NOT NULL DEFAULT 0 CHECK(imported_baseline IN(0,1))
) STRICT;
CREATE TABLE batches (
 id TEXT PRIMARY KEY, submission_key TEXT NOT NULL UNIQUE, request_fingerprint TEXT NOT NULL DEFAULT '', kind TEXT NOT NULL, language TEXT NOT NULL CHECK(language IN('zh-Hans','en')),
 target_epoch INTEGER NOT NULL, cancelled INTEGER NOT NULL DEFAULT 0 CHECK(cancelled IN(0,1)), created_utc TEXT NOT NULL
) STRICT;
CREATE TABLE work_items (
 id TEXT PRIMARY KEY, submission_key TEXT NOT NULL UNIQUE,
 operation TEXT NOT NULL DEFAULT 'sync' CHECK(operation IN('sync','display','restore')), expected_display TEXT NOT NULL DEFAULT '',
 target_kind TEXT NOT NULL CHECK(target_kind IN('media','term')), media_id TEXT REFERENCES media_items(id),
 field TEXT NOT NULL CHECK(field IN('Name','Overview','Genres','Term')), term_id TEXT REFERENCES genre_terms(id),
 source_id TEXT NOT NULL REFERENCES source_versions(id), source_revision INTEGER NOT NULL, control_revision INTEGER NOT NULL, render_revision INTEGER NOT NULL DEFAULT 0,
 language TEXT NOT NULL CHECK(language IN('zh-Hans','en')), target_epoch INTEGER NOT NULL,
 input_id TEXT REFERENCES generation_inputs(id), translation_id TEXT REFERENCES translation_versions(id),
 generation_key TEXT NOT NULL DEFAULT 'reuse', force_generation INTEGER NOT NULL DEFAULT 0 CHECK(force_generation IN(0,1)),
 autonomous_demand INTEGER NOT NULL DEFAULT 0 CHECK(autonomous_demand IN(0,1)),
 state TEXT NOT NULL CHECK(state IN('queued','running','waiting','paused','completed','failed','expired','cancelled')),
 step TEXT NOT NULL CHECK(step IN('source','reuse','request','save','display','verify')),
 wait_reason TEXT CHECK(wait_reason IN('dependency','rate_limit','source','display')),
 issue_kind TEXT CHECK(issue_kind IN('translation','source','display')), error_code TEXT, completion_reason TEXT,
 next_run_utc TEXT, lease_owner TEXT, lease_until_utc TEXT, attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),
 request_ms INTEGER NOT NULL DEFAULT 0, save_ms INTEGER NOT NULL DEFAULT 0, display_ms INTEGER NOT NULL DEFAULT 0,
 created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
 CHECK((target_kind='media' AND media_id IS NOT NULL AND term_id IS NULL AND field<>'Term') OR
       (target_kind='term' AND term_id IS NOT NULL AND media_id IS NULL AND field='Term'))
) STRICT;
CREATE INDEX work_runnable ON work_items(state,next_run_utc,created_utc) WHERE state IN('queued','waiting','running');
CREATE INDEX work_media ON work_items(media_id,field,created_utc DESC);
CREATE UNIQUE INDEX work_active_target ON work_items(target_kind,ifnull(media_id,term_id),field,source_id,source_revision,render_revision,language,generation_key,target_epoch,control_revision)
 WHERE state IN('queued','running','waiting','paused');
CREATE TABLE batch_items (
 batch_id TEXT NOT NULL REFERENCES batches(id), work_id TEXT NOT NULL REFERENCES work_items(id),
 active INTEGER NOT NULL DEFAULT 1 CHECK(active IN(0,1)), PRIMARY KEY(batch_id,work_id)
) STRICT;
CREATE INDEX batch_work ON batch_items(work_id,active);
CREATE TABLE work_dependencies (
 parent_id TEXT NOT NULL REFERENCES work_items(id), child_id TEXT NOT NULL REFERENCES work_items(id),
 PRIMARY KEY(parent_id,child_id), CHECK(parent_id<>child_id)
) STRICT;
CREATE INDEX dependency_child ON work_dependencies(child_id,parent_id);
CREATE TABLE request_attempts (
 id TEXT PRIMARY KEY, work_id TEXT NOT NULL REFERENCES work_items(id), sequence INTEGER NOT NULL CHECK(sequence>0),
 input_id TEXT NOT NULL REFERENCES generation_inputs(id), connection_id TEXT NOT NULL REFERENCES connection_versions(id),
 state TEXT NOT NULL CHECK(state IN('prepared','sending','succeeded','failed','unknown','not_sent')),
 uncertainty_acknowledged INTEGER NOT NULL DEFAULT 0 CHECK(uncertainty_acknowledged IN(0,1)),
 started_utc TEXT NOT NULL, send_started_utc TEXT, completed_utc TEXT, http_status INTEGER,
 error_code TEXT, diagnostics_json TEXT NOT NULL DEFAULT '{}', elapsed_ms INTEGER NOT NULL DEFAULT 0 CHECK(elapsed_ms>=0),
 usage_json TEXT NOT NULL DEFAULT '{}', translation_id TEXT REFERENCES translation_versions(id),
 late_completed_utc TEXT, late_diagnostics_json TEXT, late_usage_json TEXT, late_translation_id TEXT REFERENCES translation_versions(id),
 UNIQUE(work_id,sequence)
) STRICT;
CREATE UNIQUE INDEX request_inflight_input ON request_attempts(input_id)
 WHERE state IN('prepared','sending') OR (state='unknown' AND uncertainty_acknowledged=0);
CREATE INDEX requests_work ON request_attempts(work_id,sequence);
CREATE TABLE connection_blocks (
 connection_id TEXT PRIMARY KEY REFERENCES connection_versions(id), reason TEXT NOT NULL, until_utc TEXT, updated_utc TEXT NOT NULL
) STRICT;
CREATE TABLE display_intents (
 id TEXT PRIMARY KEY, media_id TEXT NOT NULL REFERENCES media_items(id), field TEXT NOT NULL CHECK(field IN('Name','Overview','Genres')),
 revision INTEGER NOT NULL CHECK(revision>0), mode TEXT NOT NULL CHECK(mode IN('translated','original','external')),
 keep_original INTEGER NOT NULL DEFAULT 0 CHECK(keep_original IN(0,1)),
 source_id TEXT NOT NULL REFERENCES source_versions(id), source_revision INTEGER NOT NULL, control_revision INTEGER NOT NULL, render_revision INTEGER NOT NULL DEFAULT 0,
 target_epoch INTEGER NOT NULL, language TEXT NOT NULL CHECK(language IN('zh-Hans','en')),
 translation_id TEXT REFERENCES translation_versions(id), desired_value TEXT, expected_value TEXT NOT NULL,
 state TEXT NOT NULL CHECK(state IN('pending','applied','blocked','conflict','expired','protected')),
 error_code TEXT, updated_utc TEXT NOT NULL, UNIQUE(media_id,field)
) STRICT;
CREATE TABLE display_writes (
 id TEXT PRIMARY KEY, intent_id TEXT NOT NULL REFERENCES display_intents(id), intent_revision INTEGER NOT NULL CHECK(intent_revision>0),
 action TEXT NOT NULL CHECK(action IN('apply','restore')), state TEXT NOT NULL CHECK(state IN('prepared','called','verified','conflict','blocked','expired')),
 source_revision INTEGER NOT NULL, control_revision INTEGER NOT NULL, target_epoch INTEGER NOT NULL,
 before_value TEXT NOT NULL, before_hash TEXT NOT NULL, target_value TEXT NOT NULL, target_hash TEXT NOT NULL,
 restores_write_id TEXT REFERENCES display_writes(id), created_utc TEXT NOT NULL, called_utc TEXT, verified_utc TEXT, error_code TEXT,
 UNIQUE(intent_id,intent_revision,action)
) STRICT;
CREATE INDEX display_pending ON display_intents(state,updated_utc);
CREATE TABLE import_runs (
 id TEXT PRIMARY KEY, manifest_hash TEXT NOT NULL UNIQUE, state TEXT NOT NULL CHECK(state IN('building','completed')),
 created_utc TEXT NOT NULL, completed_utc TEXT, summary_json TEXT NOT NULL DEFAULT '{}'
) STRICT;
CREATE TABLE import_links (
 import_id TEXT NOT NULL REFERENCES import_runs(id), legacy_store TEXT NOT NULL, legacy_kind TEXT NOT NULL, legacy_id TEXT NOT NULL,
 new_kind TEXT NOT NULL, new_id TEXT NOT NULL, PRIMARY KEY(import_id,legacy_store,legacy_kind,legacy_id,new_kind)
) STRICT;
CREATE TABLE previews (
 id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, expires_utc TEXT NOT NULL, summary_json TEXT NOT NULL
) STRICT;
CREATE INDEX preview_expiry ON previews(expires_utc);
CREATE TABLE runtime_state (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;

CREATE TRIGGER source_immutable_update BEFORE UPDATE ON source_versions BEGIN SELECT RAISE(ABORT,'source_version_immutable'); END;
CREATE TRIGGER source_immutable_delete BEFORE DELETE ON source_versions BEGIN SELECT RAISE(ABORT,'source_version_immutable'); END;
CREATE TRIGGER input_immutable_update BEFORE UPDATE ON generation_inputs BEGIN SELECT RAISE(ABORT,'generation_input_immutable'); END;
CREATE TRIGGER input_immutable_delete BEFORE DELETE ON generation_inputs BEGIN SELECT RAISE(ABORT,'generation_input_immutable'); END;
CREATE TRIGGER translation_immutable_update BEFORE UPDATE ON translation_versions BEGIN SELECT RAISE(ABORT,'translation_version_immutable'); END;
CREATE TRIGGER translation_immutable_delete BEFORE DELETE ON translation_versions BEGIN SELECT RAISE(ABORT,'translation_version_immutable'); END;
CREATE TRIGGER connection_immutable_update BEFORE UPDATE ON connection_versions BEGIN SELECT RAISE(ABORT,'connection_version_immutable'); END;
CREATE TRIGGER connection_immutable_delete BEFORE DELETE ON connection_versions BEGIN SELECT RAISE(ABORT,'connection_version_immutable'); END;
CREATE TRIGGER field_source_insert BEFORE INSERT ON media_fields WHEN NEW.source_id IS NOT NULL AND NOT EXISTS(
 SELECT 1 FROM source_versions s WHERE s.id=NEW.source_id AND s.owner_id=NEW.media_id AND s.kind=NEW.field)
 BEGIN SELECT RAISE(ABORT,'field_source_mismatch'); END;
CREATE TRIGGER field_source_update BEFORE UPDATE OF source_id ON media_fields WHEN NEW.source_id IS NOT NULL AND NOT EXISTS(
 SELECT 1 FROM source_versions s WHERE s.id=NEW.source_id AND s.owner_id=NEW.media_id AND s.kind=NEW.field)
 BEGIN SELECT RAISE(ABORT,'field_source_mismatch'); END;
CREATE TRIGGER field_monotonic BEFORE UPDATE ON media_fields WHEN NEW.source_revision<OLD.source_revision OR NEW.control_revision<OLD.control_revision
 OR NEW.render_revision<OLD.render_revision OR (NEW.source_id IS NOT OLD.source_id AND NEW.source_revision<=OLD.source_revision)
 BEGIN SELECT RAISE(ABORT,'field_revision_regression'); END;
CREATE TRIGGER selection_insert BEFORE INSERT ON translation_selections WHEN NOT EXISTS(
 SELECT 1 FROM translation_versions t WHERE t.id=NEW.translation_id AND t.source_id=NEW.source_id AND t.language=NEW.language)
 BEGIN SELECT RAISE(ABORT,'selection_mismatch'); END;
CREATE TRIGGER selection_update BEFORE UPDATE ON translation_selections WHEN NEW.source_id<>OLD.source_id OR NEW.language<>OLD.language OR NEW.revision<=OLD.revision OR NOT EXISTS(
 SELECT 1 FROM translation_versions t WHERE t.id=NEW.translation_id AND t.source_id=NEW.source_id AND t.language=NEW.language)
 BEGIN SELECT RAISE(ABORT,'selection_mismatch'); END;
CREATE TRIGGER translation_input_insert BEFORE INSERT ON translation_versions WHEN
 (NEW.input_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM generation_inputs i WHERE i.id=NEW.input_id AND i.source_id=NEW.source_id AND i.language=NEW.language)) OR
 (NEW.request_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM request_attempts r WHERE r.id=NEW.request_id AND r.input_id=NEW.input_id))
 BEGIN SELECT RAISE(ABORT,'translation_input_mismatch'); END;
CREATE TRIGGER request_input_insert BEFORE INSERT ON request_attempts WHEN NOT EXISTS(
 SELECT 1 FROM work_items w JOIN generation_inputs i ON i.id=NEW.input_id
 WHERE w.id=NEW.work_id AND w.input_id=NEW.input_id AND w.source_id=i.source_id AND w.language=i.language)
 BEGIN SELECT RAISE(ABORT,'request_input_mismatch'); END;
CREATE TRIGGER attempt_transition BEFORE UPDATE OF state ON request_attempts WHEN
 (OLD.state='prepared' AND NEW.state NOT IN('prepared','sending','not_sent')) OR
 (OLD.state='sending' AND NEW.state NOT IN('sending','succeeded','failed','unknown','not_sent'))
 BEGIN SELECT RAISE(ABORT,'attempt_invalid_transition'); END;
CREATE TRIGGER attempt_terminal BEFORE UPDATE ON request_attempts WHEN OLD.state IN('succeeded','failed','not_sent') OR
 (OLD.state='unknown' AND (NEW.state<>'unknown' OR NEW.id<>OLD.id OR NEW.work_id<>OLD.work_id OR NEW.sequence<>OLD.sequence
 OR NEW.input_id<>OLD.input_id OR NEW.connection_id<>OLD.connection_id OR NEW.started_utc<>OLD.started_utc
 OR NEW.send_started_utc IS NOT OLD.send_started_utc OR NEW.completed_utc IS NOT OLD.completed_utc OR NEW.http_status IS NOT OLD.http_status
 OR NEW.error_code IS NOT OLD.error_code OR NEW.diagnostics_json<>OLD.diagnostics_json OR NEW.elapsed_ms<>OLD.elapsed_ms
 OR NEW.usage_json<>OLD.usage_json OR NEW.translation_id IS NOT OLD.translation_id
 OR (OLD.late_completed_utc IS NOT NULL AND (NEW.late_completed_utc IS NOT OLD.late_completed_utc
 OR NEW.late_diagnostics_json IS NOT OLD.late_diagnostics_json OR NEW.late_usage_json IS NOT OLD.late_usage_json OR NEW.late_translation_id IS NOT OLD.late_translation_id))
 OR NEW.uncertainty_acknowledged<OLD.uncertainty_acknowledged))
 BEGIN SELECT RAISE(ABORT,'attempt_history_immutable'); END;
CREATE TRIGGER attempt_no_delete BEFORE DELETE ON request_attempts BEGIN SELECT RAISE(ABORT,'attempt_history_immutable'); END;
INSERT INTO settings(singleton,revision,target_language,target_epoch,title_enabled,overview_enabled,genres_enabled,auto_enabled)
 VALUES(1,1,'zh-Hans',0,1,1,1,0);
PRAGMA application_id=1246571857;
PRAGMA user_version=1;
