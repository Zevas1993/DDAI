var script_class = "tool"

const MOD_VERSION = "0.2.1"
const TARGET_DUNGEONDRAFT_VERSION = "1.2.0.1"
const TARGET_DUNGEONDRAFT_EXECUTABLE_SHA256 = "c14ddddbaada43610e763f73f0c2786983ca4440578acde0ac9668cda358af02"
const MAILBOX_SCHEMA_VERSION = "1.0"
const MAILBOX_ROOT = "user://ddai"
const MAP_DATA_KEY = "org.ddai.status_bridge"
const MAXIMUM_MESSAGE_BYTES = 1048576
const POLL_INTERVAL_SECONDS = 0.25
const HEARTBEAT_INTERVAL_SECONDS = 10.0
const SUPPORTED_COMMANDS = ["status", "apply_plan", "inspect_map", "undo_last_job"]
const MAXIMUM_INSPECTION_LIMIT = 500
const MAXIMUM_INSPECTION_CURSOR_OFFSET = 10000
const MAXIMUM_INSPECTION_STATE_ITEMS = 10000
const MAXIMUM_INSPECTION_LEVELS = 128
const MAXIMUM_INSPECTION_LABEL_LENGTH = 256
const MAXIMUM_UNIVERSAL_OPERATIONS = 500
const MAXIMUM_UNIVERSAL_POINTS = 10000
const MAXIMUM_SURFACE_SAMPLES = 10000
const MAXIMUM_TERRAIN_BLEND_PIXELS = 262144
const MINIMUM_UNIVERSAL_NONZERO_MAGNITUDE = 1e-300
const MAXIMUM_CATALOG_CHUNKS = 4096
const MAXIMUM_CATALOG_ENTRIES = 100000
const MAXIMUM_CATALOG_SNAPSHOTS = 4096
const MAXIMUM_PROCESSING_CLAIMS = 4096
const CATALOG_SCHEMA_VERSION = "1.0"
const CATALOG_CATEGORIES = [
	"Terrain",
	"Patterns",
	"Patterns Colorable",
	"Caves",
	"Roofs",
	"Objects",
	"Walls",
	"Materials",
	"Portals",
	"Paths",
	"Lights",
	"Simple Tiles",
	"Smart Tiles",
	"Smart Tiles Double",
]
const RUNTIME_RECEIPT_SLOT_PATHS = ["user://ddai/runtime-receipt-slot-0.json", "user://ddai/runtime-receipt-slot-1.json"]

var _poll_elapsed = POLL_INTERVAL_SECONDS
var _heartbeat_elapsed = HEARTBEAT_INTERVAL_SECONDS
var _session_id = ""
var _heartbeat_slot = 0
var _prepared_mutation_keys = {}
var _mutation_active = false
var _map_job_revision = 0
var _map_job_state_fingerprint = ""
var _newly_prepared_job_keys = {}
var _newly_reversing_job_keys = {}
var _pending_operation_results = {}
var _pending_observation_results = {}
var _pending_reversal_results = {}
var _resolved_job_assets = {}
var _processing_claim_cursor = 0
var _certified_operation_executors = {}
var _runtime_certified_operation_types = ["wall_polyline"]
var _operation_certifications = []
var _resolved_map_uuid = ""
var _map_identity_state = "unbound"
var _resolved_map_world_id = 0
var _asset_list_provider = null
var _texture_loader = null


# Called by Dungeondraft after the mod is loaded.
func start():
	_ensure_mailbox_directories()
	_session_id = str(OS.get_unix_time()) + "-" + str(OS.get_ticks_msec())
	_certified_operation_executors = {
		"terrain_stroke": funcref(self, "_execute_terrain_stroke"),
		"pattern_region": funcref(self, "_execute_pattern_region"),
		"colorable_pattern_region": funcref(self, "_execute_pattern_region"),
		"cave_region": funcref(self, "_execute_cave_region"),
		"roof_region": funcref(self, "_execute_roof_region"),
		"wall_polyline": funcref(self, "_execute_wall_polyline"),
	}
	_operation_certifications = _certify_operation_routes()
	if _asset_list_provider == null:
		_asset_list_provider = funcref(self, "_get_live_asset_list")
	if _texture_loader == null:
		_texture_loader = funcref(self, "_load_live_texture")
	_write_runtime_receipt()
	_write_heartbeat()
	_probe_managed_adapter()


func _probe_managed_adapter():
	var available = false
	var reason = "csharp_script_unavailable"
	if ClassDB.class_exists("CSharpScript"):
		var script = ClassDB.instance("CSharpScript")
		if script != null:
			script.source_code = "using Godot; public class DdaiManagedAdapter : Reference { public string Ping() { return \"ddai-managed-adapter-v1\"; } }"
			if script.reload(false) == OK:
				var instance = script.new()
				if instance != null and instance.call("Ping") == "ddai-managed-adapter-v1":
					available = true
					reason = "available"
	var path = MAILBOX_ROOT + "/runtime-receipts/managed-adapter.json"
	var record = {"schema_version": MAILBOX_SCHEMA_VERSION, "available": available, "reason": reason}
	var directory = Directory.new()
	return (_replace_json_recoverably(path, record) if directory.file_exists(path) else _write_json_atomically(path, record)) != "write_failed"


# Called by Dungeondraft every frame. This is a filesystem poller, never a network listener.
func update(delta):
	_heartbeat_elapsed += delta
	if _heartbeat_elapsed >= HEARTBEAT_INTERVAL_SECONDS:
		_heartbeat_elapsed = 0.0
		_write_heartbeat()
	_poll_elapsed += delta
	if _poll_elapsed < POLL_INTERVAL_SECONDS:
		return
	_poll_elapsed = 0.0
	_process_one_request()


func _ensure_mailbox_directories():
	var directory = Directory.new()
	for name in ["requests", "processing", "responses", "failed", "journal", "mutation-intents", "map-jobs", "map-completed", "map-undoable", "map-undone", "undo-jobs", "surface-rollbacks", "runtime-receipts", "runtime-heartbeats"]:
		directory.make_dir_recursive(MAILBOX_ROOT + "/" + name)


func _process_one_request():
	var claim = _claim_next_request()
	if not claim.has("path"):
		claim = _claim_next_processing()
	if not claim.has("path"):
		return
	_run_claim_state_machine(claim)


func _run_claim_state_machine(claim):
	# Each iteration crosses at most one durable boundary. Re-entry after any
	# boundary follows the same route during ordinary polling and startup recovery.
	for _step in range(8):
		var transition = _advance_claim_state(claim)
		if str(transition).begins_with("map_job_") or str(transition).begins_with("undo_job_"):
			return transition
		if transition == "blocked" or transition == "no_work" or transition == "invalid_claim_failed":
			return transition
	return "bounded_transition_limit"


func _advance_claim_state(claim):
	var directory = Directory.new()
	var key = claim.file_name.get_basename()
	var journal_path = MAILBOX_ROOT + "/journal/" + claim.file_name
	var response_path = MAILBOX_ROOT + "/responses/" + claim.file_name
	if not directory.file_exists(claim.path):
		return _advance_without_claim(claim.file_name)

	var read_result = _read_bounded_text(claim.path)
	if not read_result.ok:
		return _fail_claim_without_loss(claim, read_result.error)
	var parsed = JSON.parse(read_result.text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		return _fail_claim_without_loss(claim, _error("malformed_request", "Request JSON must contain an object envelope.", ""))
	var request = parsed.result
	var validation_error = _validate_request(request, claim.file_name)
	if validation_error.has("code"):
		return _fail_claim_without_loss(claim, validation_error)

	var canonical_request_text = _canonical_request_text(request)
	var reconciliation_result = _reconcile_request_duplicate(claim.file_name, canonical_request_text)
	if reconciliation_result != "reconciled":
		return "blocked"
	var request_fingerprint = canonical_request_text.sha256_text()
	if request.command == "apply_plan":
		return _advance_apply_plan_claim(claim, request, request_fingerprint, journal_path, response_path, key)
	if request.command == "undo_last_job":
		return _advance_undo_last_job_claim(claim, request, request_fingerprint, response_path, key)
	if not directory.file_exists(journal_path):
		var response = _prepare_response(request)
		var response_text = to_json(response)
		var journal_result = _write_json_atomically(journal_path, {
			"schema_version": MAILBOX_SCHEMA_VERSION,
			"request_id": request.request_id,
			"request_fingerprint": request_fingerprint,
			"response_text": response_text,
		})
		if journal_result == "created":
			return "journal_created"
		if journal_result != "existing":
			var journal_failure_result = _write_failed_record(claim, _error("journal_write_failed", "Prepared response journal could not be published; claim retained.", "request_id"))
			if journal_failure_result != "created":
				return "blocked"
			return "blocked"

	var journal_result = _read_validated_journal(journal_path, request, request_fingerprint)
	if not journal_result.ok:
		var invalid_journal_result = _move_to_unique_failed(journal_path, key, "invalid-journal")
		if invalid_journal_result != "moved" and invalid_journal_result != "missing":
			return "blocked"
		return "invalid_journal_failed"
	var journal = journal_result.journal
	if not directory.file_exists(response_path):
		if _write_text_atomically(response_path, journal.response_text) == "created":
			return "response_published"
		return "blocked"
	if not _response_text_matches(response_path, journal.response_text):
		var response_conflict_result = _write_failed_record(claim, _error("response_conflict", "Existing response differs from the exact journaled response; claim retained.", "request_id"))
		if response_conflict_result != "created":
			return "blocked"
		return "blocked"

	var claim_remove_result = _remove_file(claim.path)
	return "claim_deleted" if claim_remove_result == "removed" or claim_remove_result == "missing" else "blocked"


func _advance_apply_plan_claim(claim, request, request_fingerprint, journal_path, response_path, key):
	if typeof(request.payload) == TYPE_DICTIONARY and request.payload.get("schema_version", "") == "2.0":
		return _advance_universal_plan_claim(claim, request, request_fingerprint, journal_path, response_path, key)
	var plan_validation = _validate_rectangular_room_plan(request.payload, request.request_id)
	if not plan_validation.ok:
		return _fail_claim_without_loss(claim, plan_validation.error)
	var plan = plan_validation.plan
	var plan_fingerprint = _plan_fingerprint_input(plan).sha256_text()
	var prepared_path = MAILBOX_ROOT + "/mutation-intents/" + key + ".prepared.json"
	var confirmed_path = MAILBOX_ROOT + "/mutation-intents/" + key + ".confirmed.json"
	var ambiguous_path = MAILBOX_ROOT + "/mutation-intents/" + key + ".ambiguous.json"
	var directory = Directory.new()
	if directory.file_exists(journal_path):
		var existing_journal = _read_validated_journal(journal_path, request, request_fingerprint)
		if not existing_journal.ok:
			return "blocked"
		return _advance_journaled_response(claim, request, request_fingerprint, journal_path, response_path, key, existing_journal.journal.response_text)

	if not directory.file_exists(prepared_path):
		if directory.file_exists(confirmed_path) or directory.file_exists(ambiguous_path):
			return "blocked"
		var preflight = _runtime_room_preflight(plan)
		if not preflight.ok:
			var preflight_text = to_json(_apply_failure(request, plan_fingerprint, preflight.error.code, preflight.error.message, preflight.error.path, false))
			return _advance_journaled_response(claim, request, request_fingerprint, journal_path, response_path, key, preflight_text)
		var prepared = _mutation_intent_payload(request, request_fingerprint, plan_fingerprint, "prepared", null)
		var prepared_write = _write_mutation_intent(prepared_path, prepared)
		if prepared_write == "created":
			_prepared_mutation_keys[key] = true
			return "mutation_intent_created"
		if prepared_write != "existing":
			return "blocked"

	var prepared_read = _read_validated_mutation_intent(prepared_path, key, request, request_fingerprint, plan_fingerprint, "prepared", false)
	if not prepared_read.ok:
		_write_mutation_conflict(key, "prepared")
		return "blocked"
	var has_confirmed = directory.file_exists(confirmed_path)
	var has_ambiguous = directory.file_exists(ambiguous_path)
	if has_confirmed and has_ambiguous:
		_write_mutation_conflict(key, "multiple-outcomes")
		return "blocked"
	if has_confirmed or has_ambiguous:
		var outcome_path = confirmed_path if has_confirmed else ambiguous_path
		var outcome_state = "confirmed" if has_confirmed else "ambiguous"
		var outcome = _read_validated_mutation_intent(outcome_path, key, request, request_fingerprint, plan_fingerprint, outcome_state, true)
		if not outcome.ok:
			_write_mutation_conflict(key, outcome_state)
			return "blocked"
		return _advance_journaled_response(claim, request, request_fingerprint, journal_path, response_path, key, outcome.intent.response_text)

	# A prepared intent from an earlier process may already have mutated the map.
	# Never replay it: durably record ambiguity and tell the user to inspect/Undo.
	if not _prepared_mutation_keys.has(key):
		var unknown_response = _apply_failure(request, plan_fingerprint, "mutation_outcome_unknown", "Dungeondraft may have applied this room before interruption; inspect the map and use Undo once if it appeared.", "request_id", true)
		var ambiguous = _mutation_intent_payload(request, request_fingerprint, plan_fingerprint, "ambiguous", to_json(unknown_response))
		return "mutation_ambiguity_recorded" if _write_mutation_intent(ambiguous_path, ambiguous) == "created" else "blocked"
	if _mutation_active:
		var busy_text = to_json(_apply_failure(request, plan_fingerprint, "mutation_busy", "Another native map mutation is already active.", "request_id", false))
		return _advance_journaled_response(claim, request, request_fingerprint, journal_path, response_path, key, busy_text)

	_prepared_mutation_keys.erase(key)
	_mutation_active = true
	var executed_response = _execute_rectangular_room(request, plan, plan_fingerprint)
	_mutation_active = false
	if not _validate_response(executed_response, request.request_id, request.command):
		return "blocked"
	if not executed_response.payload.has("plan_fingerprint") or executed_response.payload.plan_fingerprint != plan_fingerprint:
		return "blocked"
	var confirmed = _mutation_intent_payload(request, request_fingerprint, plan_fingerprint, "confirmed", to_json(executed_response))
	return "mutation_confirmed" if _write_mutation_intent(confirmed_path, confirmed) == "created" else "blocked"


func _advance_universal_plan_claim(claim, request, request_fingerprint, _journal_path, response_path, key):
	var validation = _validate_universal_plan(request.payload, request.request_id)
	if not validation.ok:
		return _fail_claim_without_loss(claim, validation.error)
	var plan = validation.plan
	var plan_fingerprint = _universal_plan_fingerprint_input(plan).sha256_text()
	var job_path = MAILBOX_ROOT + "/map-jobs/" + key + ".json"
	var completed_path = MAILBOX_ROOT + "/map-completed/" + key + ".json"
	var directory = Directory.new()
	if directory.file_exists(_journal_path):
		var existing_journal = _read_validated_journal(_journal_path, request, request_fingerprint)
		if not existing_journal.ok:
			return "blocked"
		return _advance_journaled_response(claim, request, request_fingerprint, _journal_path, response_path, key, existing_journal.journal.response_text)

	if directory.file_exists(completed_path):
		var completed = _read_bounded_dictionary(completed_path)
		if completed == null or not _dictionary_has_exact_keys(completed, ["schema_version", "request_id", "request_fingerprint", "plan_fingerprint", "journal_fingerprint", "response_fingerprint", "response_text"]) or completed.schema_version != MAILBOX_SCHEMA_VERSION or completed.request_id != request.request_id or completed.request_fingerprint != request_fingerprint or completed.plan_fingerprint != plan_fingerprint:
			return "blocked"
		if not _is_sha256(completed.journal_fingerprint) or not _is_sha256(completed.response_fingerprint) or typeof(completed.response_text) != TYPE_STRING or completed.response_text.sha256_text() != completed.response_fingerprint or not _validate_universal_completion_response(completed.response_text, request, plan_fingerprint):
			return "blocked"
		if directory.file_exists(job_path):
			var completed_job_read = _read_bounded_text(job_path)
			if not completed_job_read.ok or completed_job_read.text.sha256_text() != completed.journal_fingerprint:
				return "blocked"
		if not directory.file_exists(response_path) or not _response_text_matches(response_path, completed.get("response_text", "")):
			return "blocked"
		_resolved_job_assets.erase(key)
		return "map_job_claim_deleted" if _remove_file(claim.path) in ["removed", "missing"] else "blocked"

	if not directory.file_exists(job_path):
		var response_context = _preflight_response_context(plan)
		var preflight = _preflight_universal_plan(plan)
		if not preflight.ok:
			if not response_context.ok:
				return "blocked"
			var failed_response = _universal_apply_failure(request, plan_fingerprint, preflight.error.code, preflight.error.message, preflight.error.path, false, response_context)
			return _advance_journaled_response(claim, request, request_fingerprint, _journal_path, response_path, key, to_json(failed_response))
		var prepared = _new_map_job_journal(request, request_fingerprint, plan_fingerprint, preflight)
		if _write_map_job_journal(job_path, prepared, false) != "replaced":
			return "blocked"
		_newly_prepared_job_keys[key] = true
		_resolved_job_assets[key] = preflight.resolved_assets
		return "map_job_prepared"

	var job = _read_map_job_journal(job_path, request, request_fingerprint, plan_fingerprint)
	if job == null:
		return "blocked"
	if job.state != "committed" and job.state != "reversed" and job.state != "outcome_unknown" and job.map_id != _current_map_id():
		job.state = "outcome_unknown"
		job.canonical_response = null
		return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
	if job.state == "prepared":
		if int(job.next_operation_index) >= plan.operations.size():
			job.state = "committed"
			job.canonical_response = to_json(_universal_apply_success(request, plan_fingerprint, job))
			return "map_job_committed" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
		if _pending_operation_results.has(key):
			var operation_result = _pending_operation_results[key]
			_pending_operation_results.erase(key)
			if not operation_result.ok:
				if operation_result.get("untracked_change", false):
					job.state = "outcome_unknown"
					job.canonical_response = null
					return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
				if operation_result.node_ids.size() > 0:
					if _native_ids_are_safe_for_job(job, operation_result.node_ids):
						job.current_operation_node_ids = operation_result.node_ids.duplicate()
						return _begin_map_job_reversal(job_path, job, key, true)
					job.state = "outcome_unknown"
					job.canonical_response = null
					return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
				return _begin_map_job_reversal(job_path, job, key, false)
			job.current_operation_node_ids = operation_result.node_ids
			job.state = "operation_applied"
			return "map_job_operation_applied" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
		if not _newly_prepared_job_keys.has(key):
			job.state = "outcome_unknown"
			job.canonical_response = null
			return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
		if _capture_map_job_state_fingerprint() != job.map_state_fingerprint:
			job.state = "outcome_unknown"
			job.canonical_response = null
			return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
		if not _catalog_identity_matches(job):
			return _begin_map_job_reversal(job_path, job, key, false)
		_newly_prepared_job_keys.erase(key)
		var operation_result = _execute_operation(plan.operations[int(job.next_operation_index)], key, plan.canvas)
		_pending_operation_results[key] = operation_result
		return "map_job_native_operation_called"
	if job.state == "operation_applied":
		if _pending_observation_results.has(key):
			var observed = _pending_observation_results[key]
			_pending_observation_results.erase(key)
			if observed == "pending":
				return "map_job_native_observation_pending"
			if observed == "observed":
				job.operation_observations.append({
					"operation_index": int(job.next_operation_index),
					"operation_id": plan.operations[int(job.next_operation_index)].operation_id,
					"native_node_ids": job.current_operation_node_ids.duplicate(),
				})
				for node_id in job.current_operation_node_ids:
					job.observed_native_node_ids.append(node_id)
				job.current_operation_node_ids = []
				job.next_operation_index = int(job.next_operation_index) + 1
				job.state = "operation_observed"
				job.map_state_fingerprint = _capture_map_job_state_fingerprint()
				if not _is_sha256(job.map_state_fingerprint):
					return "blocked"
				if _write_map_job_journal(job_path, job, true) != "replaced":
					return "blocked"
				_map_job_revision += 1
				_map_job_state_fingerprint = job.map_state_fingerprint
				return "map_job_operation_observed"
			return _begin_map_job_reversal(job_path, job, key, true)
		var observation_result = _observe_operation(plan.operations[int(job.next_operation_index)], job.current_operation_node_ids, key)
		_pending_observation_results[key] = observation_result
		return "map_job_native_operation_observed"
	if job.state == "operation_observed":
		if int(job.next_operation_index) >= plan.operations.size():
			job.state = "committed"
			job.canonical_response = to_json(_universal_apply_success(request, plan_fingerprint, job))
			return "map_job_committed" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
		job.state = "prepared"
		_newly_prepared_job_keys[key] = true
		return "map_job_prepared" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
	if job.state == "reversing":
		if _pending_reversal_results.has(key):
			var pending_reversal = _pending_reversal_results[key]
			if _observe_reversal(plan.operations[int(job.reversal_operation_index)], pending_reversal.node_ids, key):
				_pending_reversal_results.erase(key)
				if int(job.reversal_operation_index) == 0:
					var reversed_observed_operation = int(job.reversal_operation_index) < int(job.next_operation_index)
					job.state = "reversed"
					job.current_operation_node_ids = []
					job.reversal_operation_index = null
					job.map_state_fingerprint = _capture_map_job_state_fingerprint()
					if not _is_sha256(job.map_state_fingerprint):
						return "blocked"
					if reversed_observed_operation:
						_map_job_revision += 1
					_map_job_state_fingerprint = job.map_state_fingerprint
					return "map_job_reversed" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
				var previous_index = int(job.reversal_operation_index) - 1
				job.current_operation_node_ids = job.operation_observations[previous_index].native_node_ids.duplicate()
				job.reversal_operation_index = previous_index
				job.map_state_fingerprint = _capture_map_job_state_fingerprint()
				if not _is_sha256(job.map_state_fingerprint) or _write_map_job_journal(job_path, job, true) != "replaced":
					return "blocked"
				_newly_reversing_job_keys[key] = true
				_map_job_revision += 1
				_map_job_state_fingerprint = job.map_state_fingerprint
				return "map_job_reversal_progressed"
			pending_reversal.attempts = int(pending_reversal.attempts) + 1
			if int(pending_reversal.attempts) >= 8:
				_pending_reversal_results.erase(key)
				job.state = "outcome_unknown"
				job.canonical_response = null
				return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
			_pending_reversal_results[key] = pending_reversal
			return "map_job_native_reversal_observed"
		if not _newly_reversing_job_keys.has(key):
			job.state = "outcome_unknown"
			job.canonical_response = null
			return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
		_newly_reversing_job_keys.erase(key)
		if not _reverse_operation(plan.operations[int(job.reversal_operation_index)], job.current_operation_node_ids, key):
			job.state = "outcome_unknown"
			job.canonical_response = null
			return "map_job_outcome_unknown" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
		_pending_reversal_results[key] = {"node_ids": job.current_operation_node_ids.duplicate(), "attempts": 0}
		return "map_job_native_reversal_called"
	if job.state == "reversed":
		job.state = "committed"
		job.canonical_response = to_json(_universal_apply_failure(request, plan_fingerprint, "operation_failed_reversed", "A native operation could not be verified; all observed native operations were reversed.", "operations", false, job))
		return "map_job_committed" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
	if job.state == "outcome_unknown":
		job.state = "committed"
		job.canonical_response = to_json(_universal_apply_failure(request, plan_fingerprint, "outcome_unknown", "A native map mutation or reversal may have completed before interruption; it will not be replayed.", "operations", true, job))
		return "map_job_committed" if _write_map_job_journal(job_path, job, true) == "replaced" else "blocked"
	if job.state == "committed":
		if typeof(job.canonical_response) != TYPE_STRING or job.canonical_response.to_utf8().size() > MAXIMUM_MESSAGE_BYTES or not _validate_universal_job_response(job.canonical_response, request, job, plan_fingerprint):
			return "blocked"
		var committed_response = JSON.parse(job.canonical_response)
		if committed_response.error != OK or typeof(committed_response.result) != TYPE_DICTIONARY:
			return "blocked"
		if committed_response.result.success:
			var undoable_path = MAILBOX_ROOT + "/map-undoable/" + key + ".json"
			var undoable_record = {
				"schema_version": MAILBOX_SCHEMA_VERSION,
				"target_request_id": request.request_id,
				"target_key": key,
				"map_id": job.map_id,
				"map_revision": int(committed_response.result.payload.map_revision),
				"map_state_fingerprint": job.map_state_fingerprint,
				"plan_fingerprint": plan_fingerprint,
				"plan_fingerprint_text": _universal_plan_fingerprint_input(request.payload),
				"plan": request.payload,
				"operation_observations": job.operation_observations,
			}
			var undoable_directory = Directory.new()
			if not undoable_directory.file_exists(undoable_path):
				return "map_job_undoable_recorded" if _write_text_atomically(undoable_path, to_json(undoable_record)) == "created" else "blocked"
			var existing_undoable = _read_bounded_dictionary(undoable_path)
			if existing_undoable == null or to_json(existing_undoable) != to_json(undoable_record):
				return "blocked"
		if not directory.file_exists(response_path):
			return "map_job_response_published" if _write_text_atomically(response_path, job.canonical_response) == "created" else "blocked"
		if not _response_text_matches(response_path, job.canonical_response):
			return "blocked"
		var committed_job_read = _read_bounded_text(job_path)
		if not committed_job_read.ok:
			return "blocked"
		var completed = {
			"schema_version": MAILBOX_SCHEMA_VERSION,
			"request_id": request.request_id,
			"request_fingerprint": request_fingerprint,
			"plan_fingerprint": plan_fingerprint,
			"journal_fingerprint": committed_job_read.text.sha256_text(),
			"response_fingerprint": job.canonical_response.sha256_text(),
			"response_text": job.canonical_response,
		}
		if _write_text_atomically(completed_path, to_json(completed)) == "created":
			_resolved_job_assets.erase(key)
			return "map_job_completion_recorded"
		return "blocked"
	return "blocked"


func _advance_undo_last_job_claim(claim, request, request_fingerprint, response_path, key):
	var validation = _validate_undo_last_job_payload(request.payload)
	if not validation.ok:
		return _fail_claim_without_loss(claim, validation.error)
	var payload = validation.payload
	var undo_path = MAILBOX_ROOT + "/undo-jobs/" + key + ".json"
	var undone_path = MAILBOX_ROOT + "/map-undone/" + payload.target_request_id.sha256_text() + ".json"
	var directory = Directory.new()
	if directory.file_exists(undone_path):
		var completed_undo = _read_bounded_dictionary(undone_path)
		if completed_undo == null or not _undo_completion_is_valid(completed_undo, request, request_fingerprint, payload):
			return "blocked"
		if not directory.file_exists(response_path):
			return "undo_job_response_published" if _write_text_atomically(response_path, completed_undo.response_text) == "created" else "blocked"
		if not _response_text_matches(response_path, completed_undo.response_text):
			return "blocked"
		return "undo_job_claim_deleted" if _remove_file(claim.path) in ["removed", "missing"] else "blocked"
	if not directory.file_exists(undo_path):
		var prepared = _prepare_undo_job(request, request_fingerprint, payload)
		if not prepared.ok:
			var failure = _undo_last_job_failure(request, payload, prepared.error.code, prepared.error.message, prepared.error.path, false)
			return _advance_journaled_response(claim, request, request_fingerprint, MAILBOX_ROOT + "/journal/" + claim.file_name, response_path, key, to_json(failure))
		if _write_undo_job(undo_path, prepared.job, false) != "replaced":
			return "blocked"
		return "undo_job_prepared"
	var job = _read_undo_job(undo_path, request, request_fingerprint, payload)
	if job == null:
		return "blocked"
	if job.state == "prepared" and (job.map_id != _current_map_id() or _capture_map_job_state_fingerprint() != job.map_state_fingerprint or _map_job_revision != int(job.starting_map_revision)):
		job.state = "outcome_unknown"
		job.canonical_response = null
		return "undo_job_outcome_unknown" if _write_undo_job(undo_path, job, true) == "replaced" else "blocked"
	if job.state == "prepared":
		job.state = "reversing"
		job.reversal_operation_index = job.plan.operations.size() - 1
		job.current_operation_node_ids = job.operation_observations[int(job.reversal_operation_index)].native_node_ids.duplicate()
		if _write_undo_job(undo_path, job, true) != "replaced":
			return "blocked"
		_newly_reversing_job_keys[key] = true
		return "undo_job_reversing"
	if job.state == "reversing":
		if _pending_reversal_results.has(key):
			var pending = _pending_reversal_results[key]
			if _observe_reversal(job.plan.operations[int(job.reversal_operation_index)], pending.node_ids, job.target_key):
				_pending_reversal_results.erase(key)
				if int(job.reversal_operation_index) == 0:
					job.state = "committed"
					job.current_operation_node_ids = []
					job.reversal_operation_index = null
					job.map_state_fingerprint = _capture_map_job_state_fingerprint()
					if not _is_sha256(job.map_state_fingerprint):
						return "blocked"
					_map_job_revision += 1
					_map_job_state_fingerprint = job.map_state_fingerprint
					job.canonical_response = to_json(_undo_last_job_success(request, payload, job))
					return "undo_job_committed" if _write_undo_job(undo_path, job, true) == "replaced" else "blocked"
				job.reversal_operation_index = int(job.reversal_operation_index) - 1
				job.current_operation_node_ids = job.operation_observations[int(job.reversal_operation_index)].native_node_ids.duplicate()
				if _write_undo_job(undo_path, job, true) != "replaced":
					return "blocked"
				_newly_reversing_job_keys[key] = true
				return "undo_job_reversal_progressed"
			pending.attempts = int(pending.attempts) + 1
			if int(pending.attempts) >= 8:
				_pending_reversal_results.erase(key)
				job.state = "outcome_unknown"
				job.canonical_response = null
				return "undo_job_outcome_unknown" if _write_undo_job(undo_path, job, true) == "replaced" else "blocked"
			_pending_reversal_results[key] = pending
			return "undo_job_native_reversal_observed"
		if not _newly_reversing_job_keys.has(key):
			job.state = "outcome_unknown"
			job.canonical_response = null
			return "undo_job_outcome_unknown" if _write_undo_job(undo_path, job, true) == "replaced" else "blocked"
		_newly_reversing_job_keys.erase(key)
		if not _reverse_operation(job.plan.operations[int(job.reversal_operation_index)], job.current_operation_node_ids, job.target_key):
			job.state = "outcome_unknown"
			job.canonical_response = null
			return "undo_job_outcome_unknown" if _write_undo_job(undo_path, job, true) == "replaced" else "blocked"
		_pending_reversal_results[key] = {"node_ids": job.current_operation_node_ids.duplicate(), "attempts": 0}
		return "undo_job_native_reversal_called"
	if job.state == "outcome_unknown":
		job.state = "committed"
		job.canonical_response = to_json(_undo_last_job_failure(request, payload, "outcome_unknown", "The exact DDAI job reversal could not be proven after interruption.", "target_request_id", true, job))
		return "undo_job_committed" if _write_undo_job(undo_path, job, true) == "replaced" else "blocked"
	if job.state == "committed":
		if typeof(job.canonical_response) != TYPE_STRING or not _validate_undo_response(job.canonical_response, request, payload, job):
			return "blocked"
		if not directory.file_exists(response_path):
			return "undo_job_response_published" if _write_text_atomically(response_path, job.canonical_response) == "created" else "blocked"
		if not _response_text_matches(response_path, job.canonical_response):
			return "blocked"
		var completion = {
			"schema_version": MAILBOX_SCHEMA_VERSION,
			"request_id": request.request_id,
			"request_fingerprint": request_fingerprint,
			"target_request_id": payload.target_request_id,
			"response_fingerprint": job.canonical_response.sha256_text(),
			"response_text": job.canonical_response,
		}
		if _write_text_atomically(undone_path, to_json(completion)) != "created":
			return "blocked"
		if job.canonical_response.find("\"success\":true") != -1:
			_remove_file(MAILBOX_ROOT + "/map-undoable/" + payload.target_request_id.sha256_text() + ".json")
			_remove_directory_tree_bounded(MAILBOX_ROOT + "/surface-rollbacks/" + payload.target_request_id.sha256_text(), 4096)
		return "undo_job_completion_recorded"
	return "blocked"


func _validate_undo_last_job_payload(payload):
	if typeof(payload) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(payload, ["target_request_id", "expected_map_id", "expected_map_revision"]):
		return {"ok": false, "error": _error("invalid_request", "Undo requires exact target job and map correlation fields.", "payload")}
	if typeof(payload.target_request_id) != TYPE_STRING or not _is_safe_request_id(payload.target_request_id):
		return {"ok": false, "error": _error("invalid_request", "target_request_id is invalid.", "payload.target_request_id")}
	if typeof(payload.expected_map_id) != TYPE_STRING or not _is_safe_request_id(payload.expected_map_id):
		return {"ok": false, "error": _error("invalid_request", "expected_map_id is invalid.", "payload.expected_map_id")}
	if not _is_runtime_nonnegative_safe_integer(payload.expected_map_revision):
		return {"ok": false, "error": _error("invalid_request", "expected_map_revision is invalid.", "payload.expected_map_revision")}
	return {"ok": true, "payload": payload}


func _prepare_undo_job(request, request_fingerprint, payload):
	if _current_map_id() != payload.expected_map_id:
		return {"ok": false, "error": _error("map_id_mismatch", "The open map identity changed.", "expected_map_id")}
	if _map_job_revision != int(payload.expected_map_revision) or _capture_map_job_state_fingerprint() != _map_job_state_fingerprint:
		return {"ok": false, "error": _error("map_revision_mismatch", "The map changed after the target DDAI job.", "expected_map_revision")}
	var target_key = payload.target_request_id.sha256_text()
	var record = _read_bounded_dictionary(MAILBOX_ROOT + "/map-undoable/" + target_key + ".json")
	if record == null or not _undoable_record_is_valid(record, payload, target_key):
		return {"ok": false, "error": _error("undo_job_unavailable", "The exact completed DDAI job is not durably undoable.", "target_request_id")}
	for index in range(record.operation_observations.size()):
		if _observe_operation(record.plan.operations[index], record.operation_observations[index].native_node_ids, target_key) != "observed":
			return {"ok": false, "error": _error("undo_evidence_missing", "A recorded native node is missing or no longer belongs to the completed DDAI job.", "target_request_id")}
	return {"ok": true, "job": {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"request_fingerprint": request_fingerprint,
		"target_request_id": payload.target_request_id,
		"target_key": target_key,
		"map_id": record.map_id,
		"starting_map_revision": payload.expected_map_revision,
		"map_state_fingerprint": record.map_state_fingerprint,
		"state": "prepared",
		"plan": record.plan,
		"operation_observations": record.operation_observations,
		"current_operation_node_ids": [],
		"reversal_operation_index": null,
		"canonical_response": null,
	}}


func _undoable_record_is_valid(record, payload, target_key):
	if not _dictionary_has_exact_keys(record, ["schema_version", "target_request_id", "target_key", "map_id", "map_revision", "map_state_fingerprint", "plan_fingerprint", "plan_fingerprint_text", "plan", "operation_observations"]):
		return false
	if record.schema_version != MAILBOX_SCHEMA_VERSION or record.target_request_id != payload.target_request_id or record.target_key != target_key or record.map_id != payload.expected_map_id or int(record.map_revision) != int(payload.expected_map_revision) or not _is_sha256(record.map_state_fingerprint) or not _is_sha256(record.plan_fingerprint):
		return false
	if typeof(record.plan_fingerprint_text) != TYPE_STRING or record.plan_fingerprint_text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES or record.plan_fingerprint_text.sha256_text() != record.plan_fingerprint or typeof(record.operation_observations) != TYPE_ARRAY or record.operation_observations.size() != record.plan.operations.size():
		return false
	var observed_operation_ids = {}
	for operation in record.plan.operations:
		if typeof(operation) != TYPE_DICTIONARY or typeof(operation.get("operation_id", null)) != TYPE_STRING or not _is_safe_request_id(operation.operation_id) or observed_operation_ids.has(operation.operation_id) or typeof(operation.get("operation_type", null)) != TYPE_STRING or not _certified_operation_executors.has(operation.operation_type):
			return false
		observed_operation_ids[operation.operation_id] = true
	for index in range(record.operation_observations.size()):
		var observation = record.operation_observations[index]
		if typeof(observation) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(observation, ["operation_index", "operation_id", "native_node_ids"]) or int(observation.operation_index) != index or observation.operation_id != record.plan.operations[index].operation_id or typeof(observation.native_node_ids) != TYPE_ARRAY or observation.native_node_ids.size() == 0:
			return false
		for node_id in observation.native_node_ids:
			if not _is_positive_json_safe_integer(node_id):
				return false
	return true


func _write_undo_job(path, job, replace):
	var text = to_json(job)
	if text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
		return "write_failed"
	return _replace_json_recoverably(path, job) if replace else ("replaced" if _write_text_atomically(path, text) == "created" else "write_failed")


func _read_undo_job(path, request, request_fingerprint, payload):
	var job = _read_bounded_dictionary(path)
	if job == null or not _dictionary_has_exact_keys(job, ["schema_version", "request_id", "request_fingerprint", "target_request_id", "target_key", "map_id", "starting_map_revision", "map_state_fingerprint", "state", "plan", "operation_observations", "current_operation_node_ids", "reversal_operation_index", "canonical_response"]):
		return null
	if job.schema_version != MAILBOX_SCHEMA_VERSION or job.request_id != request.request_id or job.request_fingerprint != request_fingerprint or job.target_request_id != payload.target_request_id or job.target_key != payload.target_request_id.sha256_text() or job.map_id != payload.expected_map_id or int(job.starting_map_revision) != int(payload.expected_map_revision) or not _is_sha256(job.request_fingerprint) or not _is_sha256(job.map_state_fingerprint):
		return null
	if not ["prepared", "reversing", "outcome_unknown", "committed"].has(job.state) or typeof(job.plan) != TYPE_DICTIONARY or typeof(job.operation_observations) != TYPE_ARRAY or typeof(job.current_operation_node_ids) != TYPE_ARRAY:
		return null
	if job.state == "prepared" and (job.current_operation_node_ids.size() != 0 or job.reversal_operation_index != null or job.canonical_response != null):
		return null
	if job.state == "reversing":
		if not _is_nonnegative_json_int32(job.reversal_operation_index) or int(job.reversal_operation_index) >= job.operation_observations.size() or job.current_operation_node_ids != job.operation_observations[int(job.reversal_operation_index)].native_node_ids or job.canonical_response != null:
			return null
	if job.state == "committed" and (typeof(job.canonical_response) != TYPE_STRING or not _validate_undo_response(job.canonical_response, request, payload, job)):
		return null
	return job


func _undo_last_job_success(request, payload, job):
	return {"schema_version": MAILBOX_SCHEMA_VERSION, "request_id": request.request_id, "command": "undo_last_job", "timestamp": _iso_timestamp(), "success": true, "payload": {"target_request_id": payload.target_request_id, "map_id": job.map_id, "starting_map_revision": payload.expected_map_revision, "map_revision": _map_job_revision, "outcome_unknown": false}}


func _undo_last_job_failure(request, payload, code, message, path, outcome_unknown, context = null):
	var map_id = payload.expected_map_id
	var revision = payload.expected_map_revision
	if context != null:
		map_id = context.map_id
		revision = context.starting_map_revision
	return {"schema_version": MAILBOX_SCHEMA_VERSION, "request_id": request.request_id, "command": "undo_last_job", "timestamp": _iso_timestamp(), "success": false, "payload": {"target_request_id": payload.target_request_id, "map_id": map_id, "starting_map_revision": revision, "map_revision": _map_job_revision if _map_job_revision >= int(revision) else revision, "outcome_unknown": outcome_unknown}, "error": _error(code, message, path)}


func _validate_undo_response(text, request, payload, job):
	var parsed = JSON.parse(text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY or not _validate_response(parsed.result, request.request_id, "undo_last_job"):
		return false
	var response = parsed.result
	var response_keys = ["schema_version", "request_id", "command", "timestamp", "success", "payload"] if response.success else ["schema_version", "request_id", "command", "timestamp", "success", "payload", "error"]
	if not _dictionary_has_exact_keys(response, response_keys):
		return false
	if not response.success and (not _dictionary_has_exact_keys(response.error, ["code", "message", "path"]) or typeof(response.error.path) != TYPE_STRING):
		return false
	var body = response.payload
	return typeof(body) == TYPE_DICTIONARY and _dictionary_has_exact_keys(body, ["target_request_id", "map_id", "starting_map_revision", "map_revision", "outcome_unknown"]) and body.target_request_id == payload.target_request_id and body.map_id == job.map_id and int(body.starting_map_revision) == int(payload.expected_map_revision) and _is_nonnegative_json_safe_integer(body.map_revision) and int(body.map_revision) >= int(payload.expected_map_revision) and typeof(body.outcome_unknown) == TYPE_BOOL and (not response.success or (not body.outcome_unknown and int(body.map_revision) > int(payload.expected_map_revision)))


func _undo_completion_is_valid(completion, request, request_fingerprint, payload):
	return _dictionary_has_exact_keys(completion, ["schema_version", "request_id", "request_fingerprint", "target_request_id", "response_fingerprint", "response_text"]) and completion.schema_version == MAILBOX_SCHEMA_VERSION and completion.request_id == request.request_id and completion.request_fingerprint == request_fingerprint and completion.target_request_id == payload.target_request_id and _is_sha256(completion.response_fingerprint) and typeof(completion.response_text) == TYPE_STRING and completion.response_text.sha256_text() == completion.response_fingerprint


func _validate_universal_plan(plan, expected_request_id):
	var plan_keys = ["schema_version", "request_id", "expected_map_id", "base_revision", "expected_catalog_revision", "mode", "coordinate_system", "canvas", "operations"]
	if typeof(plan) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(plan, plan_keys):
		return {"ok": false, "error": _error("malformed_plan", "Universal plan fields are missing or unsupported.", "payload")}
	if plan.schema_version != "2.0" or typeof(plan.request_id) != TYPE_STRING or plan.request_id != expected_request_id or not _is_safe_request_id(plan.request_id):
		return {"ok": false, "error": _error("malformed_plan", "Universal plan identity is invalid.", "payload.request_id")}
	if typeof(plan.expected_map_id) != TYPE_STRING or not _is_safe_request_id(plan.expected_map_id):
		return {"ok": false, "error": _error("invalid_map_id", "expected_map_id is required and must be safe.", "payload.expected_map_id")}
	if not _is_nonnegative_json_safe_integer(plan.base_revision) or not _is_nonnegative_json_safe_integer(plan.expected_catalog_revision):
		return {"ok": false, "error": _error("invalid_revision", "Map and catalog revisions must be non-negative integers.", "payload.base_revision")}
	if plan.mode != "add" or plan.coordinate_system != "grid":
		return {"ok": false, "error": _error("unsupported_plan", "The live executor currently supports add mode in grid coordinates.", "payload.mode")}
	if typeof(plan.canvas) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(plan.canvas, ["width", "height"]) or not _is_positive_json_int32(plan.canvas.width) or not _is_positive_json_int32(plan.canvas.height):
		return {"ok": false, "error": _error("invalid_canvas", "Canvas dimensions must be positive integers.", "payload.canvas")}
	if typeof(plan.operations) != TYPE_ARRAY or plan.operations.size() == 0 or plan.operations.size() > MAXIMUM_UNIVERSAL_OPERATIONS:
		return {"ok": false, "error": _error("invalid_operations", "operations exceeds its bounded count.", "payload.operations")}
	var operation_ids = {}
	var total_points = 0
	for index in range(plan.operations.size()):
		var operation = plan.operations[index]
		var operation_path = "payload.operations[" + str(index) + "]"
		var operation_validation = _validate_universal_operation(operation, operation_path, plan.canvas, operation_ids)
		if not operation_validation.ok:
			return operation_validation
		if total_points > MAXIMUM_UNIVERSAL_POINTS - int(operation_validation.point_count):
			return {"ok": false, "error": _error("invalid_geometry", "The plan exceeds its bounded geometry count.", operation_path)}
		total_points += int(operation_validation.point_count)
	return {"ok": true, "plan": plan}


func _validate_universal_operation(operation, operation_path, canvas, operation_ids):
	if typeof(operation) != TYPE_DICTIONARY or typeof(operation.get("operation_type", null)) != TYPE_STRING:
		return {"ok": false, "error": _error("malformed_operation", "Operation fields are missing or unsupported.", operation_path)}
	var operation_type = operation.operation_type
	var expected_keys = {
		"terrain_stroke": ["operation_type", "operation_id", "level_id", "asset_ref", "path", "width", "strength"],
		"pattern_region": ["operation_type", "operation_id", "level_id", "asset_ref", "region", "rotation_degrees", "layer"],
		"colorable_pattern_region": ["operation_type", "operation_id", "level_id", "asset_ref", "region", "color_rgba", "rotation_degrees", "layer"],
		"cave_region": ["operation_type", "operation_id", "level_id", "asset_ref", "region", "floor_color_rgba", "wall_color_rgba"],
		"roof_region": ["operation_type", "operation_id", "level_id", "asset_ref", "region", "width", "shade"],
		"wall_polyline": ["operation_type", "operation_id", "level_id", "asset_ref", "path", "closed", "color_rgba"],
	}
	if not expected_keys.has(operation_type) or not _certified_operation_executors.has(operation_type):
		return {"ok": false, "error": _error("unsupported_operation", "The operation type is not implemented by this runtime.", operation_path + ".operation_type")}
	if not _dictionary_has_exact_keys(operation, expected_keys[operation_type]):
		return {"ok": false, "error": _error("malformed_operation", "Operation fields are missing or unsupported.", operation_path)}
	if typeof(operation.operation_id) != TYPE_STRING or not _is_safe_request_id(operation.operation_id) or operation_ids.has(operation.operation_id):
		return {"ok": false, "error": _error("invalid_operation_id", "operation_id must be safe and unique.", operation_path + ".operation_id")}
	operation_ids[operation.operation_id] = true
	if typeof(operation.level_id) != TYPE_STRING or not _is_safe_request_id(operation.level_id):
		return {"ok": false, "error": _error("invalid_level_id", "level_id must be safe.", operation_path + ".level_id")}
	if typeof(operation.asset_ref) != TYPE_STRING or operation.asset_ref.length() != 71 or not operation.asset_ref.begins_with("sha256:") or not _is_sha256(operation.asset_ref.substr(7, 64)):
		return {"ok": false, "error": _error("invalid_asset_ref", "asset_ref must be a canonical opaque SHA-256 reference.", operation_path + ".asset_ref")}
	var geometry_name = "path" if operation_type in ["terrain_stroke", "wall_polyline"] else "region"
	var geometry = operation[geometry_name]
	var minimum_points = 2 if geometry_name == "path" else 3
	if typeof(geometry) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(geometry, ["points"]) or typeof(geometry.points) != TYPE_ARRAY or geometry.points.size() < minimum_points or geometry.points.size() > MAXIMUM_UNIVERSAL_POINTS:
		return {"ok": false, "error": _error("invalid_geometry", "Operation geometry is outside its bounded shape contract.", operation_path + "." + geometry_name)}
	var unique_points = {}
	for point_index in range(geometry.points.size()):
		var point = geometry.points[point_index]
		if typeof(point) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(point, ["x", "y"]) or not _is_bounded_grid_point(point, canvas):
			return {"ok": false, "error": _error("invalid_point", "Geometry points must be finite and inside the canvas.", operation_path + "." + geometry_name + ".points[" + str(point_index) + "]")}
		unique_points[_double_fingerprint_text(float(point.x)) + _double_fingerprint_text(float(point.y))] = true
	if geometry_name == "region" and unique_points.size() < 3:
		return {"ok": false, "error": _error("invalid_polygon", "A region requires three unique points.", operation_path + ".region")}
	if operation_type == "terrain_stroke" and (not _is_positive_universal_number(operation.width) or not _is_unit_universal_number(operation.strength)):
		return {"ok": false, "error": _error("invalid_terrain_style", "Terrain width and strength are invalid.", operation_path)}
	if operation_type in ["pattern_region", "colorable_pattern_region"] and (not _is_rotation(operation.rotation_degrees) or not _is_supported_layer(operation.layer)):
		return {"ok": false, "error": _error("invalid_pattern_style", "Pattern rotation or layer is invalid.", operation_path)}
	if operation_type == "colorable_pattern_region" and not _is_rgba(operation.color_rgba):
		return {"ok": false, "error": _error("invalid_pattern_style", "Pattern color is invalid.", operation_path + ".color_rgba")}
	if operation_type == "cave_region" and (not _is_rgba(operation.floor_color_rgba) or not _is_rgba(operation.wall_color_rgba)):
		return {"ok": false, "error": _error("invalid_cave_style", "Cave colors are invalid.", operation_path)}
	if operation_type == "roof_region" and (not _is_positive_universal_number(operation.width) or not _is_unit_universal_number(operation.shade)):
		return {"ok": false, "error": _error("invalid_roof_style", "Roof width or shade is invalid.", operation_path)}
	if operation_type == "wall_polyline" and (typeof(operation.closed) != TYPE_BOOL or not _is_rgba(operation.color_rgba)):
		return {"ok": false, "error": _error("invalid_wall_style", "Wall closed and color values are invalid.", operation_path)}
	return {"ok": true, "point_count": geometry.points.size()}


func _preflight_universal_plan(plan):
	if Global.World == null or not _is_runtime_positive_int32(Global.World.Width) or not _is_runtime_positive_int32(Global.World.Height) or not _is_runtime_positive_finite_number(Global.World.GridSize):
		return {"ok": false, "error": _error("map_not_available", "No usable Dungeondraft map is available.", "")}
	if _current_map_id() != plan.expected_map_id:
		return {"ok": false, "error": _error("map_id_mismatch", "The open map identity changed.", "payload.expected_map_id")}
	var current_state_fingerprint = _capture_map_job_state_fingerprint()
	if current_state_fingerprint == "" or _map_job_state_fingerprint == "":
		return {"ok": false, "error": _error("map_revision_mismatch", "The represented map state changed after capability discovery.", "payload.base_revision")}
	if current_state_fingerprint != _map_job_state_fingerprint:
		_map_job_revision += 1
		_map_job_state_fingerprint = current_state_fingerprint
	if _map_job_revision != int(plan.base_revision):
		return {"ok": false, "error": _error("map_revision_mismatch", "The open map revision changed.", "payload.base_revision")}
	if int(Global.World.Width) != int(plan.canvas.width) or int(Global.World.Height) != int(plan.canvas.height):
		return {"ok": false, "error": _error("canvas_mismatch", "The open canvas does not match the plan.", "payload.canvas")}
	if Global.Editor == null or typeof(Global.Editor.Tools) != TYPE_DICTIONARY or (Global.Editor.ActiveToolName != null and typeof(Global.Editor.ActiveToolName) != TYPE_STRING):
		return {"ok": false, "error": _error("editor_unavailable", "The documented editor state is unavailable.", "")}
	var catalog = _read_accepted_catalog()
	if not catalog.ok:
		return catalog
	if int(catalog.catalog_revision) != int(plan.expected_catalog_revision):
		return {"ok": false, "error": _error("catalog_revision_mismatch", "The accepted asset catalog changed.", "payload.expected_catalog_revision")}
	var resolved_assets = {}
	var resolved_references = {}
	for operation in plan.operations:
		if not _certified_operation_executors.has(operation.operation_type) or not _runtime_certified_operation_types.has(operation.operation_type):
			return {"ok": false, "error": _error("unsupported_operation", "The operation executor is not certified.", "payload.operations")}
		if not _current_level_ids().has(operation.level_id) or Global.World.GetLevelByID(int(operation.level_id)) == null:
			return {"ok": false, "error": _error("unsupported_level", "The requested level is unavailable.", "payload.operations.level_id")}
		var route_preflight = _surface_route_preflight(operation, Global.World.GetLevelByID(int(operation.level_id)))
		if not route_preflight.ok:
			return route_preflight
		if not catalog.entries.has(operation.asset_ref):
			return {"ok": false, "error": _error("asset_not_found", "The opaque asset reference is not in the accepted catalog.", "payload.operations.asset_ref")}
		var entry = catalog.entries[operation.asset_ref]
		var category = _surface_category(operation.operation_type)
		if category == "" or entry.category != category or not _is_sha256(entry.resource_fingerprint):
			return {"ok": false, "error": _error("asset_category_mismatch", "The asset is not in the required operation category.", "payload.operations.asset_ref")}
		if not resolved_references.has(operation.asset_ref):
			var live_asset = _resolve_live_asset(category, entry.resource_fingerprint)
			if not live_asset.ok:
				return live_asset
			resolved_references[operation.asset_ref] = live_asset.resource_identity
		resolved_assets[operation.operation_id] = resolved_references[operation.asset_ref]
	return {
		"ok": true,
		"map_id": _current_map_id(),
		"starting_map_revision": _map_job_revision,
		"map_state_fingerprint": current_state_fingerprint,
		"catalog_revision": catalog.catalog_revision,
		"catalog_fingerprint": catalog.catalog_fingerprint,
		"resolved_assets": resolved_assets,
	}


func _surface_category(operation_type):
	return {
		"terrain_stroke": "Terrain",
		"pattern_region": "Patterns",
		"colorable_pattern_region": "Patterns Colorable",
		"cave_region": "Caves",
		"roof_region": "Roofs",
		"wall_polyline": "Walls",
	}.get(operation_type, "")


func _surface_route_preflight(operation, level):
	var tool_name = {
		"terrain_stroke": "TerrainBrush",
		"pattern_region": "PatternShapeTool",
		"colorable_pattern_region": "PatternShapeTool",
		"cave_region": "CaveBrush",
		"roof_region": "RoofTool",
		"wall_polyline": "WallTool",
	}.get(operation.operation_type, "")
	if tool_name == "" or not Global.Editor.Tools.has(tool_name) or Global.Editor.Tools[tool_name] == null:
		return {"ok": false, "error": _error("operation_tool_unavailable", "The documented operation tool is unavailable.", "payload.operations")}
	var tool = Global.Editor.Tools[tool_name]
	if operation.operation_type == "terrain_stroke" and tool.IsPainting:
		return {"ok": false, "error": _error("terrain_tool_busy", "Finish the current manual terrain stroke first.", "payload.operations")}
	if operation.operation_type in ["pattern_region", "colorable_pattern_region"] and (tool.isDrawing or tool.isDragging):
		return {"ok": false, "error": _error("pattern_tool_busy", "Finish the current manual pattern edit first.", "payload.operations")}
	if operation.operation_type == "cave_region" and (level.CaveMesh == null or level.CaveMesh.IsDrawing or level.CaveMesh.IsMeshWorkerBusy):
		return {"ok": false, "error": _error("cave_tool_busy", "Wait for the current cave edit to finish.", "payload.operations")}
	if operation.operation_type == "roof_region" and tool.isDrawing:
		return {"ok": false, "error": _error("roof_tool_busy", "Finish the current manual roof first.", "payload.operations")}
	if operation.operation_type in ["roof_region", "wall_polyline"]:
		if Global.WorldUI == null or Global.WorldUI.EditArcPoint or Global.WorldUI.Polyline.size() > 0:
			return {"ok": false, "error": _error("polyline_busy", "Finish or cancel the current manual polyline first.", "payload.operations")}
	if operation.operation_type in ["pattern_region", "colorable_pattern_region", "roof_region"]:
		if not Global.Editor.Tools.has("SelectTool") or _select_tool_has_active_selection(Global.Editor.Tools["SelectTool"]):
			return {"ok": false, "error": _error("selection_busy", "Deselect the current manual selection before applying this operation.", "payload.operations")}
	if operation.operation_type == "wall_polyline" and tool.isDrawing:
		return {"ok": false, "error": _error("wall_tool_busy", "Finish the current manual wall first.", "payload.operations")}
	return {"ok": true}


func _preflight_response_context(plan):
	var identity = _read_catalog_identity_for_revision(int(plan.expected_catalog_revision))
	if not identity.ok:
		return {"ok": false}
	return {
		"ok": true,
		"map_id": plan.expected_map_id,
		"starting_map_revision": int(plan.base_revision),
		"catalog_revision": int(plan.expected_catalog_revision),
		"catalog_fingerprint": identity.catalog_fingerprint,
	}


func _read_catalog_identity_for_revision(expected_revision):
	var matched_fingerprint = null
	for pointer_name in ["current.json", "current-slot-0.json", "current-slot-1.json"]:
		var pointer = _read_bounded_dictionary(MAILBOX_ROOT + "/catalog/" + pointer_name)
		if pointer == null or not pointer.has("session_id") or not pointer.has("manifest"):
			continue
		if pointer.get("session_id", "") != _session_id or not _is_safe_relative_catalog_path(pointer.get("manifest", "")):
			continue
		var manifest_path = MAILBOX_ROOT + "/catalog/snapshots/" + pointer.manifest
		var manifest = _read_bounded_dictionary(manifest_path)
		if manifest == null or manifest.get("session_id", "") != _session_id or not manifest.get("complete", false) or not _is_nonnegative_json_safe_integer(manifest.get("catalog_revision", null)) or int(manifest.catalog_revision) != expected_revision or not _is_sha256(manifest.get("catalog_fingerprint", "")) or not _read_catalog_snapshot(manifest_path, manifest).ok:
			continue
		if pointer.has("catalog_revision") and (not _is_nonnegative_json_safe_integer(pointer.catalog_revision) or int(pointer.catalog_revision) != expected_revision):
			continue
		if matched_fingerprint != null and matched_fingerprint != manifest.catalog_fingerprint:
			return {"ok": false}
		matched_fingerprint = manifest.catalog_fingerprint
	# Completed snapshots are immutable and content-bound. Pointer slots rotate,
	# but recovery must still correlate an older durable request rather than
	# starving every newer claim behind it. Scan only a bounded directory and
	# accept solely the full canonical manifest validator used by live preflight.
	var directory = Directory.new()
	if directory.open(MAILBOX_ROOT + "/catalog/snapshots") == OK:
		var snapshot_count = 0
		directory.list_dir_begin(true, true)
		var snapshot_name = directory.get_next()
		while snapshot_name != "":
			if directory.current_is_dir():
				snapshot_count += 1
				if snapshot_count > MAXIMUM_CATALOG_SNAPSHOTS:
					directory.list_dir_end()
					return {"ok": false}
				var revision_marker = "-" + str(expected_revision) + "-"
				if _is_safe_request_id(snapshot_name) and snapshot_name.find(revision_marker) != -1:
					var manifest_path = MAILBOX_ROOT + "/catalog/snapshots/" + snapshot_name + "/manifest.json"
					var manifest = _read_bounded_dictionary(manifest_path)
					if _catalog_manifest_is_valid(manifest) and int(manifest.catalog_revision) == expected_revision and _read_catalog_snapshot(manifest_path, manifest).ok:
						if matched_fingerprint != null and matched_fingerprint != manifest.catalog_fingerprint:
							directory.list_dir_end()
							return {"ok": false}
						matched_fingerprint = manifest.catalog_fingerprint
			snapshot_name = directory.get_next()
		directory.list_dir_end()
	if matched_fingerprint == null:
		return {"ok": false}
	return {"ok": true, "catalog_fingerprint": matched_fingerprint}


func _new_map_job_journal(request, request_fingerprint, plan_fingerprint, preflight):
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"request_fingerprint": request_fingerprint,
		"plan_fingerprint": plan_fingerprint,
		"map_id": preflight.map_id,
		"starting_map_revision": preflight.starting_map_revision,
		"catalog_revision": preflight.catalog_revision,
		"catalog_fingerprint": preflight.catalog_fingerprint,
		"map_state_fingerprint": preflight.map_state_fingerprint,
		"state": "prepared",
		"next_operation_index": 0,
		"observed_native_node_ids": [],
		"operation_observations": [],
		"current_operation_node_ids": [],
		"reversal_operation_index": null,
		"canonical_response": null,
	}


func _write_map_job_journal(path, journal, replace):
	var text = to_json(journal)
	if text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
		return "write_failed"
	if replace:
		return _replace_json_recoverably(path, journal)
	return "replaced" if _write_text_atomically(path, text) == "created" else "write_failed"


func _read_map_job_journal(path, request, request_fingerprint, plan_fingerprint):
	var job = _read_bounded_dictionary(path)
	if job == null or not _dictionary_has_exact_keys(job, ["schema_version", "request_id", "request_fingerprint", "plan_fingerprint", "map_id", "starting_map_revision", "catalog_revision", "catalog_fingerprint", "map_state_fingerprint", "state", "next_operation_index", "observed_native_node_ids", "operation_observations", "current_operation_node_ids", "reversal_operation_index", "canonical_response"]):
		return null
	if job.schema_version != MAILBOX_SCHEMA_VERSION or job.request_id != request.request_id or job.request_fingerprint != request_fingerprint or job.plan_fingerprint != plan_fingerprint:
		return null
	if not _is_sha256(job.request_fingerprint) or not _is_sha256(job.plan_fingerprint) or not _is_sha256(job.catalog_fingerprint):
		return null
	if not _is_sha256(job.map_state_fingerprint):
		return null
	if typeof(job.map_id) != TYPE_STRING or job.map_id != request.payload.expected_map_id:
		return null
	if not _is_nonnegative_json_safe_integer(job.starting_map_revision) or int(job.starting_map_revision) != int(request.payload.base_revision):
		return null
	if not _is_nonnegative_json_safe_integer(job.catalog_revision) or int(job.catalog_revision) != int(request.payload.expected_catalog_revision):
		return null
	if not ["prepared", "operation_applied", "operation_observed", "reversing", "reversed", "outcome_unknown", "committed"].has(job.state):
		return null
	if not _is_nonnegative_json_int32(job.next_operation_index) or int(job.next_operation_index) > request.payload.operations.size():
		return null
	if typeof(job.observed_native_node_ids) != TYPE_ARRAY or typeof(job.operation_observations) != TYPE_ARRAY or typeof(job.current_operation_node_ids) != TYPE_ARRAY:
		return null
	if job.observed_native_node_ids.size() > MAXIMUM_UNIVERSAL_POINTS or job.current_operation_node_ids.size() > MAXIMUM_UNIVERSAL_POINTS or job.operation_observations.size() > MAXIMUM_UNIVERSAL_OPERATIONS:
		return null
	for node_id in job.observed_native_node_ids + job.current_operation_node_ids:
		if not _is_positive_json_safe_integer(node_id):
			return null
	for observation in job.operation_observations:
		if typeof(observation) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(observation, ["operation_index", "operation_id", "native_node_ids"]):
			return null
		if not _is_nonnegative_json_int32(observation.operation_index) or int(observation.operation_index) >= request.payload.operations.size() or observation.operation_id != request.payload.operations[int(observation.operation_index)].operation_id or typeof(observation.native_node_ids) != TYPE_ARRAY:
			return null
		for node_id in observation.native_node_ids:
			if not _is_positive_json_safe_integer(node_id):
				return null
	if job.reversal_operation_index != null and (not _is_json_int32(job.reversal_operation_index) or int(job.reversal_operation_index) < -1 or int(job.reversal_operation_index) >= request.payload.operations.size()):
		return null
	if job.canonical_response != null and (typeof(job.canonical_response) != TYPE_STRING or job.canonical_response.to_utf8().size() > MAXIMUM_MESSAGE_BYTES):
		return null
	if not _map_job_state_is_consistent(job, request.payload.operations.size()):
		return null
	if job.state == "committed" and not _validate_universal_job_response(job.canonical_response, request, job, plan_fingerprint):
		return null
	return job


func _begin_map_job_reversal(job_path, job, key, include_current_operation):
	job.state = "reversing"
	job.canonical_response = null
	if include_current_operation:
		job.reversal_operation_index = int(job.next_operation_index)
	elif int(job.next_operation_index) > 0:
		job.reversal_operation_index = int(job.next_operation_index) - 1
		job.current_operation_node_ids = job.operation_observations[int(job.reversal_operation_index)].native_node_ids.duplicate()
	else:
		job.state = "reversed"
		job.current_operation_node_ids = []
		job.reversal_operation_index = null
	var transition = "map_job_reversing" if job.state == "reversing" else "map_job_reversed"
	if _write_map_job_journal(job_path, job, true) != "replaced":
		return "blocked"
	if job.state == "reversing":
		_newly_reversing_job_keys[key] = true
	return transition


func _native_ids_are_safe_for_job(job, node_ids):
	if typeof(node_ids) != TYPE_ARRAY or node_ids.size() == 0 or job.observed_native_node_ids.size() > MAXIMUM_UNIVERSAL_POINTS - node_ids.size():
		return false
	var existing = {}
	for node_id in job.observed_native_node_ids:
		existing[int(node_id)] = true
	var current = {}
	for node_id in node_ids:
		if not _is_positive_json_safe_integer(float(node_id)):
			return false
		var canonical_id = int(node_id)
		if existing.has(canonical_id) or current.has(canonical_id):
			return false
		current[canonical_id] = true
	return true


func _map_job_state_is_consistent(job, operation_count):
	var observed = {}
	var flattened = []
	var current = {}
	if job.observed_native_node_ids.size() + job.current_operation_node_ids.size() > MAXIMUM_UNIVERSAL_POINTS:
		return false
	for node_id in job.current_operation_node_ids:
		var current_id = int(node_id)
		if current.has(current_id):
			return false
		current[current_id] = true
	if job.operation_observations.size() != int(job.next_operation_index):
		return false
	for index in range(job.operation_observations.size()):
		var observation = job.operation_observations[index]
		if int(observation.operation_index) != index:
			return false
		if observation.native_node_ids.size() == 0:
			return false
		var observation_ids = {}
		for node_id in observation.native_node_ids:
			var canonical_id = int(node_id)
			if observed.has(canonical_id) or observation_ids.has(canonical_id):
				return false
			observation_ids[canonical_id] = true
			observed[canonical_id] = true
			flattened.append(canonical_id)
	if flattened.size() != job.observed_native_node_ids.size():
		return false
	for index in range(flattened.size()):
		if flattened[index] != int(job.observed_native_node_ids[index]):
			return false
	if job.state == "operation_applied":
		if job.current_operation_node_ids.size() == 0 or job.canonical_response != null or job.reversal_operation_index != null or int(job.next_operation_index) >= operation_count:
			return false
		for node_id in job.current_operation_node_ids:
			if observed.has(int(node_id)):
				return false
		return true
	if job.state == "reversing":
		if job.current_operation_node_ids.size() == 0 or job.canonical_response != null or job.reversal_operation_index == null or int(job.reversal_operation_index) < 0 or int(job.reversal_operation_index) > int(job.next_operation_index):
			return false
		if int(job.reversal_operation_index) == int(job.next_operation_index):
			for node_id in job.current_operation_node_ids:
				if observed.has(int(node_id)):
					return false
			return int(job.next_operation_index) < operation_count
		var expected_ids = job.operation_observations[int(job.reversal_operation_index)].native_node_ids
		return job.current_operation_node_ids == expected_ids
	if job.state == "reversed":
		return job.current_operation_node_ids.size() == 0 and job.reversal_operation_index == null and job.canonical_response == null
	if job.state == "outcome_unknown":
		return job.canonical_response == null
	if job.state == "committed":
		return typeof(job.canonical_response) == TYPE_STRING
	if job.state == "prepared":
		return int(job.next_operation_index) < operation_count and job.current_operation_node_ids.size() == 0 and job.reversal_operation_index == null and job.canonical_response == null
	if job.state == "operation_observed":
		return int(job.next_operation_index) > 0 and job.current_operation_node_ids.size() == 0 and job.reversal_operation_index == null and job.canonical_response == null
	return false


func _validate_universal_job_response(response_text, request, job, plan_fingerprint):
	var parsed = JSON.parse(response_text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY or not _validate_response(parsed.result, request.request_id, "apply_plan"):
		return false
	var response = parsed.result
	var expected_response_keys = ["schema_version", "request_id", "command", "timestamp", "success", "payload"] if response.success else ["schema_version", "request_id", "command", "timestamp", "success", "payload", "error"]
	if not _dictionary_has_exact_keys(response, expected_response_keys):
		return false
	if not response.success and (not _dictionary_has_exact_keys(response.error, ["code", "message", "path"]) or typeof(response.error.path) != TYPE_STRING):
		return false
	var payload = response.payload
	if typeof(payload) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(payload, ["plan_fingerprint", "map_id", "starting_map_revision", "map_revision", "catalog_revision", "catalog_fingerprint", "outcome_unknown"]):
		return false
	if payload.plan_fingerprint != plan_fingerprint or payload.map_id != job.map_id or not _is_nonnegative_json_safe_integer(payload.starting_map_revision) or int(payload.starting_map_revision) != int(job.starting_map_revision) or not _is_nonnegative_json_safe_integer(payload.map_revision) or int(payload.map_revision) < int(job.starting_map_revision) or not _is_nonnegative_json_safe_integer(payload.catalog_revision) or int(payload.catalog_revision) != int(job.catalog_revision) or payload.catalog_fingerprint != job.catalog_fingerprint or typeof(payload.outcome_unknown) != TYPE_BOOL:
		return false
	if response.success:
		return not payload.outcome_unknown and int(payload.map_revision) > int(job.starting_map_revision) and int(job.next_operation_index) == request.payload.operations.size() and job.operation_observations.size() == request.payload.operations.size() and job.current_operation_node_ids.size() == 0 and job.reversal_operation_index == null
	if response.error.code == "operation_failed_reversed":
		return not payload.outcome_unknown and job.current_operation_node_ids.size() == 0 and job.reversal_operation_index == null
	return response.error.code == "outcome_unknown" and payload.outcome_unknown


func _validate_universal_completion_response(response_text, request, plan_fingerprint):
	var parsed = JSON.parse(response_text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY or not _validate_response(parsed.result, request.request_id, "apply_plan"):
		return false
	var response = parsed.result
	var expected_response_keys = ["schema_version", "request_id", "command", "timestamp", "success", "payload"] if response.success else ["schema_version", "request_id", "command", "timestamp", "success", "payload", "error"]
	if not _dictionary_has_exact_keys(response, expected_response_keys):
		return false
	if not response.success and (not _dictionary_has_exact_keys(response.error, ["code", "message", "path"]) or typeof(response.error.path) != TYPE_STRING):
		return false
	var payload = response.payload
	if typeof(payload) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(payload, ["plan_fingerprint", "map_id", "starting_map_revision", "map_revision", "catalog_revision", "catalog_fingerprint", "outcome_unknown"]):
		return false
	if payload.plan_fingerprint != plan_fingerprint or payload.map_id != request.payload.expected_map_id or not _is_nonnegative_json_safe_integer(payload.starting_map_revision) or int(payload.starting_map_revision) != int(request.payload.base_revision) or not _is_nonnegative_json_safe_integer(payload.map_revision) or int(payload.map_revision) < int(request.payload.base_revision) or not _is_nonnegative_json_safe_integer(payload.catalog_revision) or int(payload.catalog_revision) != int(request.payload.expected_catalog_revision) or not _is_sha256(payload.catalog_fingerprint) or typeof(payload.outcome_unknown) != TYPE_BOOL:
		return false
	if response.success:
		return not payload.outcome_unknown and int(payload.map_revision) > int(request.payload.base_revision)
	return (response.error.code == "operation_failed_reversed" and not payload.outcome_unknown) or (response.error.code == "outcome_unknown" and payload.outcome_unknown)


func _execute_operation(operation, job_key, canvas = null):
	if typeof(operation) != TYPE_DICTIONARY or not _certified_operation_executors.has(operation.operation_type):
		return {"ok": false, "node_ids": []}
	if operation.operation_type == "terrain_stroke":
		return _execute_terrain_stroke(operation, job_key, canvas)
	return _certified_operation_executors[operation.operation_type].call_func(operation, job_key)


func _execute_terrain_stroke(operation, job_key, canvas = null):
	var context = _surface_context(operation, job_key, "TerrainBrush")
	if not context.ok:
		return _surface_failure(operation, job_key, "context")
	var level = context.level
	var terrain = level.Terrain
	var terrain_tool = context.tool
	if typeof(canvas) != TYPE_DICTIONARY or not _is_positive_json_int32(canvas.get("width", null)) or not _is_positive_json_int32(canvas.get("height", null)):
		return _surface_failure(operation, job_key, "native_state_pixel_coordinate_dimensions")
	if terrain == null or terrain_tool.IsPainting or terrain_tool.brush == null:
		return _surface_failure(operation, job_key, "native_state")
	var expanded_slots = terrain.ExpandedSlots
	if typeof(expanded_slots) != TYPE_BOOL:
		return _surface_failure(operation, job_key, "native_state")
	var previous_splat = terrain.CloneSplatImage()
	var previous_splat_2 = terrain.CloneSplatImage2() if expanded_slots else null
	var before_hash = _terrain_state_hash(terrain)
	var rollback = _persist_surface_rollback(operation, job_key, previous_splat, previous_splat_2, null, before_hash, null, null, expanded_slots)
	if not rollback.ok:
		return _surface_failure(operation, job_key, rollback.get("stage", "rollback_persistence"))
	var texture = _texture_loader.call_func(context.resource_identity)
	if texture == null:
		return _surface_failure(operation, job_key, "texture_load")
	var texture_slot = _terrain_texture_slot(terrain, context.resource_identity, texture)
	if texture_slot < 0:
		_terrain_texture_diagnostic(operation, job_key, terrain, context.resource_identity, texture)
		return _surface_failure(operation, job_key, "texture_load")
	var sample_result = _polyline_surface_samples(operation.path.points)
	if not sample_result.ok:
		return _surface_failure(operation, job_key, "sampling")
	var prior_size = terrain_tool.Size
	var prior_intensity = terrain_tool.Intensity
	var prior_terrain_id = terrain_tool.TerrainID
	terrain_tool.SetSize(float(operation.width))
	terrain_tool.Intensity = float(operation.strength)
	terrain_tool.TerrainID = texture_slot
	var brush_radius = _terrain_brush_radius(terrain_tool.brush)
	if not _is_runtime_positive_finite_number(brush_radius):
		terrain_tool.SetSize(prior_size)
		terrain_tool.Intensity = prior_intensity
		terrain_tool.TerrainID = prior_terrain_id
		return _surface_failure(operation, job_key, "native_state_brush_radius", [rollback.token])
	var brush_offset = Vector2.ONE * (-float(brush_radius) - 32.0)
	var painted = _paint_terrain_splat(terrain, terrain_tool, previous_splat, previous_splat_2, expanded_slots, texture_slot, terrain_tool.brush, brush_offset, sample_result.points, float(operation.strength), int(canvas.width), int(canvas.height), job_key)
	terrain_tool.SetSize(prior_size)
	terrain_tool.Intensity = prior_intensity
	terrain_tool.TerrainID = prior_terrain_id
	if not painted.ok:
		return _surface_failure(operation, job_key, painted.stage, [rollback.token])
	if terrain_tool.IsPainting:
		return _surface_failure(operation, job_key, "painting_busy", [rollback.token])
	if not _complete_surface_rollback(rollback, _terrain_state_hash(terrain)):
		return _surface_failure(operation, job_key, "after_state", [rollback.token], true)
	return {"ok": true, "node_ids": [rollback.token]}


func _paint_terrain_splat(terrain, terrain_tool, primary, secondary, expanded_slots, texture_slot, brush, brush_offset, samples, strength, canvas_width, canvas_height, job_key):
	if terrain == null or not (primary is Image) or not (brush is Image) or typeof(expanded_slots) != TYPE_BOOL or (expanded_slots and not (secondary is Image)):
		return {"ok": false, "stage": "native_state_pixel_input"}
	if typeof(texture_slot) != TYPE_INT or texture_slot < 0 or texture_slot >= (8 if expanded_slots else 4) or typeof(samples) != TYPE_ARRAY or samples.size() == 0:
		return {"ok": false, "stage": "native_state_pixel_input"}
	var brush_width = brush.get_width()
	var brush_height = brush.get_height()
	if brush_width <= 0 or brush_height <= 0 or brush_width > MAXIMUM_TERRAIN_BLEND_PIXELS / brush_height:
		return {"ok": false, "stage": "native_state_pixel_bound"}
	var brush_pixels = brush_width * brush_height
	if samples.size() > MAXIMUM_TERRAIN_BLEND_PIXELS / brush_pixels:
		return {"ok": false, "stage": "native_state_pixel_bound"}
	var channel = texture_slot - 4 if texture_slot >= 4 else texture_slot
	var paints_secondary = texture_slot >= 4
	var grid_size = float(Global.World.GridSize)
	var initial_hash = _sha256_bytes(primary.get_data())
	for point in samples:
		if typeof(point) != TYPE_VECTOR2:
			return {"ok": false, "stage": "native_state_pixel_input"}
		var world_position = Vector2(float(point.x) * grid_size + float(brush_offset.x), float(point.y) * grid_size + float(brush_offset.y))
		if typeof(world_position) != TYPE_VECTOR2:
			return {"ok": false, "stage": "native_state_pixel_coordinate_position"}
		var destination_result = _terrain_world_to_texture(primary, world_position, canvas_width, canvas_height, grid_size)
		if not destination_result.ok:
			return {"ok": false, "stage": destination_result.stage}
		var destination = destination_result.value
		if not _blend_terrain_pixels(primary, secondary, expanded_slots, texture_slot, brush, destination, strength):
			return {"ok": false, "stage": "native_state_pixel_blend"}
	var candidate_hash = _sha256_bytes(primary.get_data())
	if not _is_sha256(candidate_hash):
		return {"ok": false, "stage": "native_state_pixel_candidate_hash"}
	if candidate_hash == initial_hash:
		return {"ok": false, "stage": "native_state_pixel_candidate_unchanged"}
	return {"ok": false, "stage": "native_state_managed_adapter_required"}


func _terrain_world_to_texture(primary, world_position, canvas_width, canvas_height, grid_size):
	if not (primary is Image):
		return {"ok": false, "stage": "native_state_pixel_coordinate_image"}
	if typeof(world_position) != TYPE_VECTOR2:
		return {"ok": false, "stage": "native_state_pixel_coordinate_position"}
	var map_width = float(canvas_width)
	var map_height = float(canvas_height)
	if map_width <= 0.0 or map_width > 2147483647.0 or map_width != floor(map_width):
		return {"ok": false, "stage": "native_state_pixel_coordinate_width"}
	if map_height <= 0.0 or map_height > 2147483647.0 or map_height != floor(map_height):
		return {"ok": false, "stage": "native_state_pixel_coordinate_height"}
	if grid_size <= 0.0:
		return {"ok": false, "stage": "native_state_pixel_coordinate_grid"}
	if primary.get_width() != int(map_width) * 4 or primary.get_height() != int(map_height) * 4:
		return {"ok": false, "stage": "native_state_pixel_coordinate_scale"}
	return {"ok": true, "value": world_position / 64.0 + Vector2.ONE * 0.5}


func _terrain_brush_radius(brush):
	if not (brush is Image):
		return -1.0
	var width = brush.get_width()
	var height = brush.get_height()
	if width <= 0 or width != height:
		return -1.0
	return float(width) * 16.0


func _blend_terrain_pixels(primary, secondary, expanded_slots, texture_slot, brush, destination, strength):
	var destination_x = int(floor(destination.x))
	var destination_y = int(floor(destination.y))
	var channel = texture_slot - 4 if texture_slot >= 4 else texture_slot
	var target = Color(0.0, 0.0, 0.0, 0.0)
	if channel == 0:
		target.r = 1.0
	elif channel == 1:
		target.g = 1.0
	elif channel == 2:
		target.b = 1.0
	else:
		target.a = 1.0
	var active = secondary if expanded_slots and texture_slot >= 4 else primary
	var inactive = primary if expanded_slots and texture_slot >= 4 else secondary
	brush.lock()
	active.lock()
	if inactive != null:
		inactive.lock()
	for brush_y in range(brush.get_height()):
		var target_y = destination_y + brush_y
		if target_y < 0 or target_y >= active.get_height():
			continue
		for brush_x in range(brush.get_width()):
			var target_x = destination_x + brush_x
			if target_x < 0 or target_x >= active.get_width():
				continue
			var mask = brush.get_pixel(brush_x, brush_y)
			var amount = clamp(max(mask.r, mask.a) * float(strength), 0.0, 1.0)
			if amount <= 0.0:
				continue
			active.set_pixel(target_x, target_y, active.get_pixel(target_x, target_y).linear_interpolate(target, amount))
			if inactive != null:
				inactive.set_pixel(target_x, target_y, inactive.get_pixel(target_x, target_y).linear_interpolate(Color(0.0, 0.0, 0.0, 0.0), amount))
	if inactive != null:
		inactive.unlock()
	active.unlock()
	brush.unlock()
	return true


func _terrain_texture_slot(terrain, resource_identity, requested_texture):
	if terrain == null or typeof(resource_identity) != TYPE_STRING or resource_identity.length() == 0 or requested_texture == null:
		return -1
	# Dungeondraft 1.2.0.1 exposes Terrain.textures (Texture[]) through the
	# GDScript/C# proxy even though Save() and GetTexture() results marshal as
	# null.  Bound and validate the documented array before indexing it.
	var expected_slots = 8 if terrain.ExpandedSlots else 4
	if typeof(terrain.textures) == TYPE_ARRAY and terrain.textures.size() == expected_slots:
		var requested_array_hash = _texture_content_hash(requested_texture)
		var array_match = -1
		for slot in range(expected_slots):
			var array_texture = terrain.textures[slot]
			if array_texture == requested_texture or (array_texture != null and str(array_texture.resource_path) == resource_identity):
				return slot
			if requested_array_hash != "" and _texture_content_hash(array_texture) == requested_array_hash:
				if array_match != -1:
					return -1
				array_match = slot
		if array_match != -1:
			return array_match
	var saved = terrain.Save(false)
	if typeof(saved) == TYPE_DICTIONARY:
		var saved_match = -1
		for slot in range(8):
			var saved_identity = saved.get("texture_" + str(slot + 1), null)
			if _saved_terrain_texture_identity(saved_identity) == resource_identity:
				if saved_match != -1:
					return -1
				saved_match = slot
		if saved_match != -1:
			return saved_match
	var requested_hash = _texture_content_hash(requested_texture)
	var matched_slot = -1
	for slot in range(8):
		var texture = terrain.GetTexture(slot)
		if texture == requested_texture:
			return slot
		if texture != null and str(texture.resource_path) == resource_identity:
			return slot
		if requested_hash != "" and _texture_content_hash(texture) == requested_hash:
			if matched_slot != -1:
				return -1
			matched_slot = slot
	return matched_slot


func _saved_terrain_texture_identity(value):
	if typeof(value) == TYPE_STRING:
		return value
	if value is Resource:
		return str(value.resource_path)
	return ""


func _texture_content_hash(texture):
	if texture == null or not texture.has_method("get_data"):
		return ""
	var image = texture.get_data()
	if image == null:
		return ""
	return _sha256_bytes(image.get_data())


func _terrain_texture_diagnostic(operation, job_key, terrain, resource_identity, requested_texture):
	if typeof(operation) != TYPE_DICTIONARY or typeof(job_key) != TYPE_STRING or not _is_safe_request_id(job_key) or terrain == null:
		return false
	var requested_hash = _texture_content_hash(requested_texture)
	var saved = terrain.Save(false)
	var slots = []
	for slot in range(8):
		var texture = terrain.GetTexture(slot)
		var saved_key = "texture_" + str(slot + 1)
		slots.append({
			"hash_matches": requested_hash != "" and _texture_content_hash(texture) == requested_hash,
			"path_present": texture != null and str(texture.resource_path).length() > 0,
			"same_object": texture == requested_texture,
			"saved_has_key": typeof(saved) == TYPE_DICTIONARY and saved.has(saved_key),
			"saved_type_code": typeof(saved.get(saved_key, null)) if typeof(saved) == TYPE_DICTIONARY else TYPE_NIL,
			"type_code": typeof(texture),
		})
	var record = {
		"operation_type": str(operation.get("operation_type", "unknown")),
		"requested_hash_present": requested_hash != "",
		"requested_path_present": requested_texture != null and str(requested_texture.resource_path).length() > 0,
		"requested_type_code": typeof(requested_texture),
		"saved_type_code": typeof(saved),
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"slots": slots,
	}
	var path = MAILBOX_ROOT + "/failed/" + job_key + ".terrain-slot.json"
	var directory = Directory.new()
	var result = _replace_json_recoverably(path, record) if directory.file_exists(path) else _write_json_atomically(path, record)
	return result != "write_failed"


func _surface_failure(operation, job_key, stage, node_ids = [], untracked_change = false):
	var allowed_stages = ["context", "native_state", "native_state_brush_radius", "native_state_pixel_input", "native_state_pixel_bound", "native_state_pixel_coordinate", "native_state_pixel_coordinate_input", "native_state_pixel_coordinate_image", "native_state_pixel_coordinate_position", "native_state_pixel_coordinate_world", "native_state_pixel_coordinate_dimensions", "native_state_pixel_coordinate_missing", "native_state_pixel_coordinate_width", "native_state_pixel_coordinate_height", "native_state_pixel_coordinate_grid", "native_state_pixel_coordinate_scale", "native_state_pixel_blend", "native_state_pixel_candidate_hash", "native_state_pixel_candidate_unchanged", "native_state_pixel_restore", "native_state_managed_adapter_required", "rollback_persistence", "texture_load", "sampling", "painting_busy", "after_state", "rollback_primary_missing", "rollback_hash_invalid", "rollback_texture_missing", "rollback_texture_identity_missing", "rollback_directory", "rollback_primary_write", "rollback_secondary_write", "rollback_hash", "rollback_metadata"]
	if typeof(operation) != TYPE_DICTIONARY or typeof(job_key) != TYPE_STRING or not _is_safe_request_id(job_key) or typeof(stage) != TYPE_STRING or not allowed_stages.has(stage):
		return {"ok": false, "node_ids": [], "untracked_change": true}
	var record = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"operation_type": str(operation.get("operation_type", "unknown")),
		"stage": stage,
	}
	var path = MAILBOX_ROOT + "/failed/" + job_key + ".surface-stage.json"
	var directory = Directory.new()
	var write_result = _replace_json_recoverably(path, record) if directory.file_exists(path) else _write_json_atomically(path, record)
	if write_result == "write_failed":
		return {"ok": false, "node_ids": node_ids, "untracked_change": true}
	return {"ok": false, "node_ids": node_ids, "untracked_change": untracked_change}


func _execute_pattern_region(operation, job_key):
	var context = _surface_context(operation, job_key, "PatternShapeTool")
	if not context.ok:
		return {"ok": false, "node_ids": []}
	var level = context.level
	var pattern_tool = context.tool
	var shapes_before = level.PatternShapes.GetShapes()
	if typeof(shapes_before) != TYPE_ARRAY or shapes_before.size() >= MAXIMUM_INSPECTION_STATE_ITEMS or pattern_tool.isDrawing or pattern_tool.isDragging:
		return {"ok": false, "node_ids": []}
	var texture = _texture_loader.call_func(context.resource_identity)
	if texture == null:
		return {"ok": false, "node_ids": []}
	var prior_texture = pattern_tool.Texture
	var prior_color = pattern_tool.Color
	var prior_rotation = pattern_tool.Rotation.value
	var prior_layer = pattern_tool.ActiveLayer
	pattern_tool.Texture = texture
	pattern_tool.Rotation.value = float(operation.rotation_degrees)
	pattern_tool.SetLayer(int(operation.layer))
	if operation.operation_type == "colorable_pattern_region":
		pattern_tool.ChangeColor(_rgba_color(operation.color_rgba), "PatternColor")
	var points = _grid_points_to_world(operation.region.points)
	level.PatternShapes.DrawPolygon(points, false)
	pattern_tool.Texture = prior_texture
	pattern_tool.Rotation.value = prior_rotation
	pattern_tool.SetLayer(prior_layer)
	if operation.operation_type == "colorable_pattern_region":
		pattern_tool.ChangeColor(prior_color, "PatternColor")
	var shapes_after = level.PatternShapes.GetShapes()
	if typeof(shapes_after) != TYPE_ARRAY or shapes_after.size() != shapes_before.size() + 1:
		return {"ok": false, "node_ids": [], "untracked_change": true}
	var created = _single_new_node(shapes_before, shapes_after)
	var node_id = _ensure_registered_node(created)
	return {"ok": node_id.ok, "node_ids": [node_id.value] if node_id.ok else [], "untracked_change": not node_id.ok}


func _execute_cave_region(operation, job_key):
	var context = _surface_context(operation, job_key, "CaveBrush")
	if not context.ok:
		return {"ok": false, "node_ids": []}
	var cave_mesh = context.level.CaveMesh
	var cave_tool = context.tool
	if cave_mesh == null or cave_mesh.bitmap == null or cave_mesh.IsDrawing or cave_mesh.IsMeshWorkerBusy:
		return {"ok": false, "node_ids": []}
	var previous_bitmap = cave_mesh.bitmap.duplicate()
	var previous_floor = cave_mesh.CaveFloor
	var previous_ground_color = cave_mesh.GroundColor
	var previous_wall_color = cave_mesh.WallColor
	var rollback = _persist_surface_rollback(operation, job_key, previous_bitmap, null, previous_floor, _cave_state_hash(cave_mesh), previous_ground_color, previous_wall_color)
	if not rollback.ok:
		return {"ok": false, "node_ids": []}
	var texture = _texture_loader.call_func(context.resource_identity)
	if texture == null:
		return {"ok": false, "node_ids": []}
	var raster = _polygon_surface_samples(operation.region.points)
	if not raster.ok:
		return {"ok": false, "node_ids": []}
	cave_tool.ChangeTexture(texture, "CaveFloor")
	cave_mesh.SetGroundColor(_rgba_color(operation.floor_color_rgba))
	cave_mesh.SetWallColor(_rgba_color(operation.wall_color_rgba))
	cave_mesh.OnDrawingBegin()
	for point in raster.points:
		cave_mesh.SetCircle(point * float(Global.World.GridSize), 1, true)
	cave_mesh.OnDrawingEnd()
	if cave_mesh.IsMeshWorkerBusy:
		return {"ok": true, "node_ids": [rollback.token]}
	if not _complete_surface_rollback(rollback, _cave_state_hash(cave_mesh)):
		return {"ok": false, "node_ids": [rollback.token], "untracked_change": true}
	return {"ok": true, "node_ids": [rollback.token]}


func _execute_roof_region(operation, job_key):
	var context = _surface_context(operation, job_key, "RoofTool")
	if not context.ok:
		return {"ok": false, "node_ids": []}
	var level = context.level
	var roof_tool = context.tool
	if roof_tool.isDrawing or level.Roofs.get_child_count() >= MAXIMUM_INSPECTION_STATE_ITEMS:
		return {"ok": false, "node_ids": []}
	var texture = _texture_loader.call_func(context.resource_identity)
	if texture == null:
		return {"ok": false, "node_ids": []}
	var roofs_before = level.Roofs.get_children()
	var prior_texture = roof_tool.Texture
	var prior_width = roof_tool.Width.value
	var prior_shade = roof_tool.Shade
	var prior_contrast = roof_tool.ShadeContrast.value
	var prior_mode = roof_tool.Mode
	roof_tool.Texture = texture
	roof_tool.Width.value = float(operation.width) * float(Global.World.GridSize)
	roof_tool.ShadeContrast.value = float(operation.shade)
	roof_tool.SetShade(float(operation.shade) > 0.0)
	var points = _grid_points_to_world(operation.region.points)
	var rectangle = _axis_aligned_rectangle(points)
	if rectangle.ok:
		roof_tool.Mode = 0
		roof_tool.DrawRect(rectangle.rect)
	else:
		roof_tool.Mode = 1
		Global.WorldUI.ClearPolyline()
		for point in points:
			Global.WorldUI.AddPolyPoint(point)
		roof_tool.FinishShape()
		Global.WorldUI.ClearPolyline()
	roof_tool.Texture = prior_texture
	roof_tool.Width.value = prior_width
	roof_tool.ShadeContrast.value = prior_contrast
	roof_tool.SetShade(prior_shade)
	roof_tool.Mode = prior_mode
	var roofs_after = level.Roofs.get_children()
	if roofs_after.size() != roofs_before.size() + 1:
		return {"ok": false, "node_ids": [], "untracked_change": true}
	var created = _single_new_node(roofs_before, roofs_after)
	var node_id = _ensure_registered_node(created)
	return {"ok": node_id.ok, "node_ids": [node_id.value] if node_id.ok else [], "untracked_change": not node_id.ok}


func _surface_context(operation, job_key, tool_name):
	if not _resolved_job_assets.has(job_key) or not _resolved_job_assets[job_key].has(operation.operation_id):
		return {"ok": false}
	if Global.World == null or Global.Editor == null or typeof(Global.Editor.Tools) != TYPE_DICTIONARY or not Global.Editor.Tools.has(tool_name) or Global.Editor.Tools[tool_name] == null:
		return {"ok": false}
	var level = Global.World.GetLevelByID(int(operation.level_id))
	if level == null:
		return {"ok": false}
	return {"ok": true, "level": level, "tool": Global.Editor.Tools[tool_name], "resource_identity": _resolved_job_assets[job_key][operation.operation_id]}


func _grid_points_to_world(points):
	var result = []
	for point in points:
		result.append(Vector2(float(point.x), float(point.y)) * float(Global.World.GridSize))
	return result


func _single_new_node(before, after):
	var known = {}
	for node in before:
		known[node] = true
	var created = null
	for node in after:
		if not known.has(node):
			if created != null:
				return null
			created = node
	return created


func _ensure_registered_node(node):
	if node == null:
		return {"ok": false}
	var node_id = _node_id_metadata(node)
	if node_id.ok and Global.World.HasNodeID(int(node_id.value)) and Global.World.GetNodeByID(int(node_id.value)) == node:
		return node_id
	node_id = _runtime_node_id(Global.World.AssignNodeID(node))
	var persisted = _node_id_metadata(node)
	if not node_id.ok or not persisted.ok or persisted.value != node_id.value or not Global.World.HasNodeID(int(node_id.value)) or Global.World.GetNodeByID(int(node_id.value)) != node:
		return {"ok": false}
	return node_id


func _axis_aligned_rectangle(points):
	if points.size() != 4:
		return {"ok": false}
	var xs = {}
	var ys = {}
	for point in points:
		xs[point.x] = true
		ys[point.y] = true
	if xs.size() != 2 or ys.size() != 2:
		return {"ok": false}
	var min_x = min(points[0].x, min(points[1].x, min(points[2].x, points[3].x)))
	var max_x = max(points[0].x, max(points[1].x, max(points[2].x, points[3].x)))
	var min_y = min(points[0].y, min(points[1].y, min(points[2].y, points[3].y)))
	var max_y = max(points[0].y, max(points[1].y, max(points[2].y, points[3].y)))
	if min_x == max_x or min_y == max_y:
		return {"ok": false}
	return {"ok": true, "rect": Rect2(min_x, min_y, max_x - min_x, max_y - min_y)}


func _polyline_surface_samples(points):
	var samples = []
	for index in range(points.size() - 1):
		var start = Vector2(float(points[index].x), float(points[index].y))
		var finish = Vector2(float(points[index + 1].x), float(points[index + 1].y))
		var steps = max(1, int(ceil(start.distance_to(finish) * 2.0)))
		if samples.size() > MAXIMUM_SURFACE_SAMPLES - steps - 1:
			return {"ok": false}
		for step in range(steps + 1):
			samples.append(start.linear_interpolate(finish, float(step) / float(steps)))
	return {"ok": true, "points": samples}


func _polygon_surface_samples(points):
	var minimum = Vector2(float(points[0].x), float(points[0].y))
	var maximum = minimum
	var polygon = []
	for point in points:
		var vector = Vector2(float(point.x), float(point.y))
		polygon.append(vector)
		minimum.x = min(minimum.x, vector.x)
		minimum.y = min(minimum.y, vector.y)
		maximum.x = max(maximum.x, vector.x)
		maximum.y = max(maximum.y, vector.y)
	var samples = []
	for y in range(int(floor(minimum.y)), int(ceil(maximum.y)) + 1):
		for x in range(int(floor(minimum.x)), int(ceil(maximum.x)) + 1):
			if samples.size() >= MAXIMUM_SURFACE_SAMPLES:
				return {"ok": false}
			var sample = Vector2(float(x) + 0.5, float(y) + 0.5)
			if Geometry.is_point_in_polygon(sample, PoolVector2Array(polygon)):
				samples.append(sample)
	if samples.size() == 0:
		return {"ok": false}
	return {"ok": true, "points": samples}


func _surface_rollback_token(job_key, operation_id):
	var value = ("0x" + (job_key + "\n" + operation_id).sha256_text().substr(0, 13)).hex_to_int()
	return max(1, int(value))


func _surface_rollback_directory(job_key, operation_id):
	return MAILBOX_ROOT + "/surface-rollbacks/" + job_key + "/" + operation_id


func _persist_surface_rollback(operation, job_key, primary, secondary, texture, before_hash, color_a = null, color_b = null, expanded_slots = false):
	if primary == null:
		return {"ok": false, "stage": "rollback_primary_missing"}
	if not _is_sha256(before_hash):
		return {"ok": false, "stage": "rollback_hash_invalid"}
	if operation.operation_type == "cave_region" and texture == null:
		return {"ok": false, "stage": "rollback_texture_missing"}
	if operation.operation_type == "cave_region" and str(texture.resource_path).length() == 0:
		return {"ok": false, "stage": "rollback_texture_identity_missing"}
	var directory_path = _surface_rollback_directory(job_key, operation.operation_id)
	var directory = Directory.new()
	if directory.make_dir_recursive(directory_path) != OK and not directory.dir_exists(directory_path):
		return {"ok": false, "stage": "rollback_directory"}
	var primary_result = _save_surface_rollback_payload(directory_path + "/primary", primary)
	if not primary_result.ok:
		return {"ok": false, "stage": "rollback_primary_write"}
	var has_secondary = secondary != null
	var secondary_result = {"ok": true, "path": "", "format": null}
	if has_secondary:
		secondary_result = _save_surface_rollback_payload(directory_path + "/secondary", secondary)
		if not secondary_result.ok:
			return {"ok": false, "stage": "rollback_secondary_write"}
	var token = _surface_rollback_token(job_key, operation.operation_id)
	var rollback = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"job_key": job_key,
		"operation_id": operation.operation_id,
		"operation_type": operation.operation_type,
		"level_id": operation.level_id,
		"token": token,
		"state": "prepared",
		"before_hash": before_hash,
		"after_hash": null,
		"primary_format": primary_result.format,
		"secondary_format": secondary_result.format if has_secondary else null,
		"primary_sha256": File.new().get_sha256(primary_result.path),
		"secondary_sha256": File.new().get_sha256(secondary_result.path) if has_secondary else null,
		"texture_identity": str(texture.resource_path) if texture != null else null,
		"color_a": color_a.to_html(true) if typeof(color_a) == TYPE_COLOR else null,
		"color_b": color_b.to_html(true) if typeof(color_b) == TYPE_COLOR else null,
		"expanded_slots": expanded_slots,
	}
	if not _is_sha256(rollback.primary_sha256) or (has_secondary and not _is_sha256(rollback.secondary_sha256)):
		return {"ok": false, "stage": "rollback_hash"}
	var metadata_path = directory_path + "/rollback.json"
	if not ["created", "existing"].has(_write_json_atomically(metadata_path, rollback)):
		return {"ok": false, "stage": "rollback_metadata"}
	return {"ok": true, "token": token, "path": metadata_path}


func _save_surface_rollback_payload(base_path, value):
	if value is Image:
		var image_path = base_path + ".png"
		return {"ok": value.save_png(image_path) == OK, "path": image_path, "format": "png"}
	if value is BitMap:
		var resource_path = base_path + ".res"
		return {"ok": ResourceSaver.save(resource_path, value) == OK, "path": resource_path, "format": "resource"}
	return {"ok": false}


func _load_surface_rollback_payload(base_path, format):
	if format == "png":
		var image = Image.new()
		return image if image.load(base_path + ".png") == OK else null
	if format == "resource":
		return load(base_path + ".res")
	return null


func _complete_surface_rollback(rollback, after_hash):
	if not rollback.ok or not _is_sha256(after_hash):
		return false
	var record = _read_bounded_dictionary(rollback.path)
	if record == null or record.state != "prepared" or record.before_hash == after_hash:
		return false
	record.state = "applied"
	record.after_hash = after_hash
	return _replace_json_recoverably(rollback.path, record) == "replaced"


func _execute_wall_polyline(operation, job_key):
	if not _resolved_job_assets.has(job_key) or not _resolved_job_assets[job_key].has(operation.operation_id):
		return {"ok": false, "node_ids": []}
	var texture = _texture_loader.call_func(_resolved_job_assets[job_key][operation.operation_id])
	var level = Global.World.GetLevelByID(int(operation.level_id))
	if texture == null or level == null or level.Walls == null or Global.Editor == null or typeof(Global.Editor.Tools) != TYPE_DICTIONARY or not Global.Editor.Tools.has("WallTool") or Global.Editor.Tools["WallTool"] == null or Global.WorldUI == null:
		return {"ok": false, "node_ids": []}
	if level.Walls.get_child_count() > MAXIMUM_INSPECTION_STATE_ITEMS:
		return {"ok": false, "node_ids": []}
	var wall_tool = Global.Editor.Tools["WallTool"]
	if wall_tool.isDrawing or Global.WorldUI.EditArcPoint or Global.WorldUI.Polyline.size() > 0:
		return {"ok": false, "node_ids": []}
	var walls_before = {}
	for wall in level.Walls.get_children():
		walls_before[wall] = true
	var points = []
	for point in operation.path.points:
		points.append(Vector2(float(point.x) * float(Global.World.GridSize), float(point.y) * float(Global.World.GridSize)))
	var wall_tool_was_active = Global.Editor.ActiveToolName == "WallTool"
	var prior_texture = wall_tool.Texture
	var prior_color = wall_tool.get("Color")
	wall_tool.Texture = texture
	wall_tool.set("Color", _rgba_color(operation.color_rgba))
	if not wall_tool_was_active:
		wall_tool.Enable()
	Global.WorldUI.ClearPolyline()
	for point in points:
		Global.WorldUI.AddPolyPoint(point)
	wall_tool.EndWall(operation.closed)
	var cleanup_ok = _cleanup_wall_tool(wall_tool, not wall_tool_was_active)
	wall_tool.Texture = prior_texture
	wall_tool.set("Color", prior_color)
	if not cleanup_ok or level.Walls.get_child_count() > MAXIMUM_INSPECTION_STATE_ITEMS:
		return {"ok": false, "node_ids": [], "untracked_change": true}
	var created_walls = []
	for wall in level.Walls.get_children():
		if not walls_before.has(wall):
			created_walls.append(wall)
	if created_walls.size() != 1:
		return {"ok": false, "node_ids": [], "untracked_change": true}
	var created_wall = created_walls[0]
	var normalized_node_id = _node_id_metadata(created_wall)
	if not normalized_node_id.ok or not Global.World.HasNodeID(int(normalized_node_id.value)) or Global.World.GetNodeByID(int(normalized_node_id.value)) != created_wall:
		normalized_node_id = _runtime_node_id(Global.World.AssignNodeID(created_wall))
	var persisted_node_id = _node_id_metadata(created_wall)
	if not normalized_node_id.ok or normalized_node_id.value <= 0 or not persisted_node_id.ok or persisted_node_id.value != normalized_node_id.value or not Global.World.HasNodeID(int(normalized_node_id.value)) or Global.World.GetNodeByID(int(normalized_node_id.value)) != created_wall:
		return {"ok": false, "node_ids": [], "untracked_change": true}
	return {"ok": true, "node_ids": [normalized_node_id.value]}


func _observe_operation(operation, native_node_ids, job_key = ""):
	if typeof(operation) != TYPE_DICTIONARY or typeof(native_node_ids) != TYPE_ARRAY or native_node_ids.size() == 0:
		return "failed"
	if operation.operation_type in ["terrain_stroke", "cave_region"]:
		return _observe_surface_rollback(operation, native_node_ids, job_key)
	return "observed" if _observe_addressable_surface(operation, native_node_ids) else "failed"


func _reverse_operation(operation, native_node_ids, job_key = ""):
	if operation.operation_type in ["terrain_stroke", "cave_region"]:
		return _reverse_surface_rollback(operation, native_node_ids, job_key)
	if operation.operation_type in ["pattern_region", "colorable_pattern_region", "roof_region"]:
		return _reverse_addressable_surface(operation, native_node_ids)
	if operation.operation_type == "wall_polyline":
		return _reverse_wall_polyline(native_node_ids)
	return false


func _reverse_wall_polyline(native_node_ids):
	for node_id in native_node_ids:
		if not Global.World.HasNodeID(int(node_id)):
			continue
		var wall = Global.World.GetNodeByID(int(node_id))
		if wall == null:
			continue
		wall.Clear()
		wall.queue_free()
	return true


func _observe_reversal(operation, native_node_ids, job_key = ""):
	if operation.operation_type in ["terrain_stroke", "cave_region"]:
		return _observe_surface_rollback_reversal(operation, native_node_ids, job_key)
	return _observe_addressable_surface_reversal(operation, native_node_ids)


func _surface_record(operation, native_node_ids, job_key):
	if native_node_ids.size() != 1 or int(native_node_ids[0]) != _surface_rollback_token(job_key, operation.operation_id):
		return null
	var path = _surface_rollback_directory(job_key, operation.operation_id) + "/rollback.json"
	var record = _read_bounded_dictionary(path)
	if record == null or not _dictionary_has_exact_keys(record, ["schema_version", "job_key", "operation_id", "operation_type", "level_id", "token", "state", "before_hash", "after_hash", "primary_format", "secondary_format", "primary_sha256", "secondary_sha256", "texture_identity", "color_a", "color_b", "expanded_slots"]):
		return null
	if record.schema_version != MAILBOX_SCHEMA_VERSION or record.job_key != job_key or record.operation_id != operation.operation_id or record.operation_type != operation.operation_type or record.level_id != operation.level_id or int(record.token) != int(native_node_ids[0]) or typeof(record.expanded_slots) != TYPE_BOOL:
		return null
	if not ["png", "resource"].has(record.primary_format) or (record.secondary_format != null and not ["png", "resource"].has(record.secondary_format)) or not _is_sha256(record.before_hash) or (record.after_hash != null and not _is_sha256(record.after_hash)) or not _is_sha256(record.primary_sha256):
		return null
	if record.operation_type == "terrain_stroke" and record.texture_identity != null:
		return null
	if record.operation_type == "cave_region" and (typeof(record.texture_identity) != TYPE_STRING or record.texture_identity.length() == 0):
		return null
	return {"record": record, "path": path, "directory": _surface_rollback_directory(job_key, operation.operation_id)}


func _observe_surface_rollback(operation, native_node_ids, job_key):
	var rollback = _surface_record(operation, native_node_ids, job_key)
	if rollback == null or rollback.record.state != "applied":
		return "failed"
	var level = Global.World.GetLevelByID(int(operation.level_id))
	if level == null:
		return "failed"
	if operation.operation_type == "terrain_stroke":
		if Global.Editor.Tools["TerrainBrush"].IsPainting:
			return "pending"
		return "observed" if _terrain_state_hash(level.Terrain) == rollback.record.after_hash else "failed"
	if operation.operation_type == "cave_region":
		if level.CaveMesh.IsMeshWorkerBusy or level.CaveMesh.IsDrawing:
			return "pending"
		return "observed" if _cave_state_hash(level.CaveMesh) == rollback.record.after_hash else "failed"
	return "failed"


func _reverse_surface_rollback(operation, native_node_ids, job_key):
	var rollback = _surface_record(operation, native_node_ids, job_key)
	if rollback == null or rollback.record.state != "applied":
		return false
	var level = Global.World.GetLevelByID(int(operation.level_id))
	if level == null:
		return false
	var primary_path = rollback.directory + "/primary." + ("png" if rollback.record.primary_format == "png" else "res")
	if File.new().get_sha256(primary_path) != rollback.record.primary_sha256:
		return false
	var primary = _load_surface_rollback_payload(rollback.directory + "/primary", rollback.record.primary_format)
	if primary == null:
		return false
	if operation.operation_type == "terrain_stroke":
		var terrain = level.Terrain
		if rollback.record.expanded_slots:
			if rollback.record.secondary_sha256 == null:
				return false
			var secondary_path = rollback.directory + "/secondary." + ("png" if rollback.record.secondary_format == "png" else "res")
			if File.new().get_sha256(secondary_path) != rollback.record.secondary_sha256:
				return false
			var secondary = _load_surface_rollback_payload(rollback.directory + "/secondary", rollback.record.secondary_format)
			if secondary == null:
				return false
			terrain.RestoreSplat2(primary, secondary)
		else:
			if rollback.record.secondary_sha256 != null:
				return false
			terrain.RestoreSplat(primary)
	elif operation.operation_type == "cave_region":
		var texture = _texture_loader.call_func(rollback.record.texture_identity)
		if texture == null:
			return false
		var cave_mesh = level.CaveMesh
		cave_mesh.SetBitmap(primary)
		cave_mesh.SetFloorTexture(texture)
		if typeof(rollback.record.color_a) != TYPE_STRING or typeof(rollback.record.color_b) != TYPE_STRING:
			return false
		cave_mesh.SetGroundColor(Color(rollback.record.color_a))
		cave_mesh.SetWallColor(Color(rollback.record.color_b))
	else:
		return false
	rollback.record.state = "reversed"
	return _replace_json_recoverably(rollback.path, rollback.record) == "replaced"


func _observe_surface_rollback_reversal(operation, native_node_ids, job_key):
	var rollback = _surface_record(operation, native_node_ids, job_key)
	if rollback == null or rollback.record.state != "reversed":
		return false
	var level = Global.World.GetLevelByID(int(operation.level_id))
	if level == null:
		return false
	if operation.operation_type == "terrain_stroke":
		return not Global.Editor.Tools["TerrainBrush"].IsPainting and _terrain_state_hash(level.Terrain) == rollback.record.before_hash
	if operation.operation_type == "cave_region":
		return not level.CaveMesh.IsMeshWorkerBusy and not level.CaveMesh.IsDrawing and _cave_state_hash(level.CaveMesh) == rollback.record.before_hash
	return false


func _addressable_surface_nodes(operation):
	if typeof(Global.World.levels) != TYPE_ARRAY or Global.World.levels.size() > MAXIMUM_INSPECTION_LEVELS:
		return null
	var nodes = []
	for level in Global.World.levels:
		if level == null:
			return null
		var source = null
		if operation.operation_type == "wall_polyline":
			var count = level.Walls.get_child_count()
			if count < 0 or nodes.size() > MAXIMUM_INSPECTION_STATE_ITEMS - count:
				return null
			source = level.Walls.get_children()
		elif operation.operation_type in ["pattern_region", "colorable_pattern_region"]:
			source = level.PatternShapes.GetShapes()
		elif operation.operation_type == "roof_region":
			var count = level.Roofs.get_child_count()
			if count < 0 or nodes.size() > MAXIMUM_INSPECTION_STATE_ITEMS - count:
				return null
			source = level.Roofs.get_children()
		else:
			return null
		if typeof(source) != TYPE_ARRAY or nodes.size() > MAXIMUM_INSPECTION_STATE_ITEMS - source.size():
			return null
		for node in source:
			nodes.append(node)
	return nodes


func _observe_addressable_surface(operation, native_node_ids):
	var nodes = _addressable_surface_nodes(operation)
	if nodes == null:
		return false
	var targets = {}
	for node_id in native_node_ids:
		if not Global.World.HasNodeID(int(node_id)):
			return false
		targets[int(node_id)] = true
	var counts = {}
	for node in nodes:
		var node_id = _node_id_metadata(node)
		if node_id.ok and targets.has(node_id.value):
			counts[node_id.value] = int(counts.get(node_id.value, 0)) + 1
	for node_id in native_node_ids:
		if counts.get(int(node_id), 0) != 1:
			return false
	return true


func _reverse_addressable_surface(operation, native_node_ids):
	if not _observe_addressable_surface(operation, native_node_ids):
		return false
	if Global.Editor == null or typeof(Global.Editor.Tools) != TYPE_DICTIONARY or not Global.Editor.Tools.has("SelectTool") or Global.Editor.Tools["SelectTool"] == null:
		return false
	var select_tool = Global.Editor.Tools["SelectTool"]
	if _select_tool_has_active_selection(select_tool):
		return false
	select_tool.DeselectAll()
	for node_id in native_node_ids:
		var node = Global.World.GetNodeByID(int(node_id))
		if node == null or select_tool.SelectThing(node, true) == null:
			select_tool.DeselectAll()
			return false
	select_tool.Delete()
	select_tool.DeselectAll()
	return true


func _select_tool_has_active_selection(select_tool):
	return select_tool == null or typeof(select_tool.Selected) != TYPE_ARRAY or select_tool.Selected.size() > 0


func _observe_addressable_surface_reversal(operation, native_node_ids):
	for node_id in native_node_ids:
		if Global.World.HasNodeID(int(node_id)):
			return false
	var nodes = _addressable_surface_nodes(operation)
	if nodes == null:
		return false
	var targets = {}
	for node_id in native_node_ids:
		targets[int(node_id)] = true
	for node in nodes:
		var node_id = _node_id_metadata(node)
		if node_id.ok and targets.has(node_id.value):
			return false
	return true


func _terrain_state_hash(terrain):
	if terrain == null:
		return ""
	var primary = terrain.CloneSplatImage()
	var secondary = terrain.CloneSplatImage2()
	if primary == null:
		return ""
	var text = "primary=" + _sha256_bytes(primary.get_data()) + "\n"
	text += "secondary=" + (_sha256_bytes(secondary.get_data()) if secondary != null else "null") + "\n"
	return text.sha256_text()


func _cave_state_hash(cave_mesh):
	if cave_mesh == null or cave_mesh.bitmap == null or cave_mesh.CaveFloor == null or str(cave_mesh.CaveFloor.resource_path).length() == 0:
		return ""
	var image = cave_mesh.bitmap.convert_to_image()
	if image == null:
		return ""
	var text = "bitmap=" + _sha256_bytes(image.get_data()) + "\n"
	text += "floor=" + str(cave_mesh.CaveFloor.resource_path).sha256_text() + "\n"
	text += "ground=" + cave_mesh.GroundColor.to_html(true) + "\n"
	text += "wall=" + cave_mesh.WallColor.to_html(true) + "\n"
	return text.sha256_text()


func _sha256_bytes(bytes):
	if typeof(bytes) != TYPE_RAW_ARRAY:
		return ""
	var context = HashingContext.new()
	if context.start(HashingContext.HASH_SHA256) != OK or context.update(bytes) != OK:
		return ""
	return context.finish().hex_encode()


func _universal_apply_success(request, plan_fingerprint, job):
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": true,
		"payload": _universal_response_payload(plan_fingerprint, job, false),
	}


func _universal_apply_failure(request, plan_fingerprint, code, message, path, outcome_unknown, context):
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": false,
		"payload": _universal_response_payload(plan_fingerprint, context, outcome_unknown),
		"error": _error(code, message, path),
	}


func _universal_response_payload(plan_fingerprint, context, outcome_unknown):
	return {
		"plan_fingerprint": plan_fingerprint,
		"map_id": context.get("map_id", _current_map_id()),
		"starting_map_revision": int(context.get("starting_map_revision", _map_job_revision)),
		"map_revision": max(_map_job_revision, int(context.get("starting_map_revision", _map_job_revision))),
		"catalog_revision": int(context.get("catalog_revision", 0)),
		"catalog_fingerprint": context.get("catalog_fingerprint", ""),
		"outcome_unknown": outcome_unknown,
	}


func _read_accepted_catalog():
	var identity = _read_current_catalog_identity()
	if not identity.ok:
		return {"ok": false, "error": _error("catalog_unavailable", "The accepted asset catalog manifest is unavailable or invalid.", "")}
	var snapshot = _read_catalog_snapshot(identity.manifest_path, identity.manifest)
	if not snapshot.ok:
		return snapshot
	return {
		"ok": true,
		"catalog_revision": int(identity.manifest.catalog_revision),
		"catalog_fingerprint": identity.manifest.catalog_fingerprint,
		"entries": snapshot.entries,
	}


func _read_catalog_snapshot(manifest_path, manifest):
	if not _catalog_manifest_is_valid(manifest):
		return {"ok": false, "error": _error("catalog_unavailable", "The accepted asset catalog manifest is unavailable or invalid.", "")}
	var entries = {}
	var observed_category_counts = {}
	for category in CATALOG_CATEGORIES:
		observed_category_counts[category] = 0
	var manifest_directory = manifest_path.get_base_dir()
	for chunk in manifest.chunks:
		var chunk_read = _read_bounded_text(manifest_directory + "/" + chunk.file_name)
		if not chunk_read.ok or chunk_read.text.to_utf8().size() != int(chunk.byte_count) or chunk_read.text.sha256_text() != chunk.sha256:
			return {"ok": false, "error": _error("catalog_unavailable", "An accepted catalog chunk failed integrity validation.", "")}
		var parsed = JSON.parse(chunk_read.text)
		if parsed.error != OK or typeof(parsed.result) != TYPE_ARRAY or parsed.result.size() != int(chunk.entry_count) or entries.size() > MAXIMUM_CATALOG_ENTRIES - parsed.result.size():
			return {"ok": false, "error": _error("catalog_unavailable", "An accepted catalog chunk is invalid or exceeds its bound.", "")}
		for entry in parsed.result:
			if not _catalog_entry_is_valid(entry) or entries.has(entry.asset_ref):
				return {"ok": false, "error": _error("catalog_unavailable", "An accepted catalog entry is invalid or duplicated.", "")}
			entries[entry.asset_ref] = entry
			observed_category_counts[entry.category] = int(observed_category_counts[entry.category]) + 1
	for category in CATALOG_CATEGORIES:
		if int(observed_category_counts[category]) != int(manifest.category_counts[category]):
			return {"ok": false, "error": _error("catalog_unavailable", "The accepted catalog category counts do not match its entries.", "")}
	return {
		"ok": true,
		"entries": entries,
	}


func _catalog_identity_matches(job):
	var identity = _read_current_catalog_identity()
	return identity.ok and int(identity.catalog_revision) == int(job.catalog_revision) and identity.catalog_fingerprint == job.catalog_fingerprint


func _read_current_catalog_identity():
	var pointer = _read_bounded_dictionary(MAILBOX_ROOT + "/catalog/current.json")
	var pointer_shape_ok = _dictionary_has_exact_keys(pointer, ["session_id", "manifest"]) or _dictionary_has_exact_keys(pointer, ["session_id", "manifest", "catalog_revision"])
	if not pointer_shape_ok or not _is_safe_request_id(pointer.session_id) or not _is_safe_relative_catalog_path(pointer.manifest):
		return {"ok": false}
	var manifest_path = MAILBOX_ROOT + "/catalog/snapshots/" + pointer.manifest
	var manifest = _read_bounded_dictionary(manifest_path)
	if not _catalog_manifest_is_valid(manifest) or manifest.session_id != pointer.session_id:
		return {"ok": false}
	if pointer.has("catalog_revision") and (not _is_nonnegative_json_safe_integer(pointer.catalog_revision) or int(pointer.catalog_revision) != int(manifest.catalog_revision)):
		return {"ok": false}
	return {
		"ok": true,
		"manifest_path": manifest_path,
		"manifest": manifest,
		"catalog_revision": int(manifest.catalog_revision),
		"catalog_fingerprint": manifest.catalog_fingerprint,
	}


func _catalog_manifest_is_valid(manifest):
	if not _dictionary_has_exact_keys(manifest, ["schema_version", "session_id", "catalog_revision", "catalog_fingerprint", "snapshot_at", "complete", "category_counts", "chunks", "errors"]):
		return false
	if manifest.schema_version != CATALOG_SCHEMA_VERSION or typeof(manifest.session_id) != TYPE_STRING or manifest.session_id.strip_edges() == "":
		return false
	if not _is_nonnegative_json_safe_integer(manifest.catalog_revision) or not _is_sha256(manifest.catalog_fingerprint) or _canonical_catalog_snapshot_at(manifest.snapshot_at) == "":
		return false
	if typeof(manifest.complete) != TYPE_BOOL or not manifest.complete or not _dictionary_has_exact_keys(manifest.category_counts, CATALOG_CATEGORIES):
		return false
	if typeof(manifest.chunks) != TYPE_ARRAY or manifest.chunks.size() > MAXIMUM_CATALOG_CHUNKS or typeof(manifest.errors) != TYPE_ARRAY:
		return false
	var category_entry_total = 0
	for category in CATALOG_CATEGORIES:
		var category_count = manifest.category_counts[category]
		if not _is_nonnegative_json_int32(category_count) or category_entry_total > MAXIMUM_CATALOG_ENTRIES - int(category_count):
			return false
		category_entry_total += int(category_count)
	var chunk_entry_total = 0
	var chunk_file_names = {}
	for chunk in manifest.chunks:
		if not _dictionary_has_exact_keys(chunk, ["file_name", "sha256", "entry_count", "byte_count"]):
			return false
		if not _is_safe_catalog_file_name(chunk.file_name) or chunk_file_names.has(chunk.file_name) or not _is_sha256(chunk.sha256):
			return false
		if not _is_nonnegative_json_int32(chunk.entry_count) or not _is_nonnegative_json_safe_integer(chunk.byte_count) or chunk_entry_total > MAXIMUM_CATALOG_ENTRIES - int(chunk.entry_count):
			return false
		chunk_file_names[chunk.file_name] = true
		chunk_entry_total += int(chunk.entry_count)
	if category_entry_total != chunk_entry_total:
		return false
	for error in manifest.errors:
		if not _dictionary_has_exact_keys(error, ["code", "message", "category"]):
			return false
		if typeof(error.code) != TYPE_STRING or error.code.strip_edges() == "" or typeof(error.message) != TYPE_STRING or error.message.strip_edges() == "":
			return false
		if error.category != null and (typeof(error.category) != TYPE_STRING or not CATALOG_CATEGORIES.has(error.category)):
			return false
	return _catalog_manifest_fingerprint(manifest) == manifest.catalog_fingerprint


func _catalog_entry_is_valid(entry):
	if not _dictionary_has_exact_keys(entry, ["asset_ref", "category", "display_name", "resource_fingerprint", "pack_id", "pack_name", "search_terms", "tags", "preview_hash", "allow_third_party_use", "generated"]):
		return false
	if typeof(entry.asset_ref) != TYPE_STRING or entry.asset_ref.length() != 71 or not entry.asset_ref.begins_with("sha256:") or not _is_sha256(entry.asset_ref.substr(7, 64)):
		return false
	if typeof(entry.category) != TYPE_STRING or not CATALOG_CATEGORIES.has(entry.category) or typeof(entry.display_name) != TYPE_STRING or entry.display_name.strip_edges() == "":
		return false
	if not _is_sha256(entry.resource_fingerprint) or (entry.preview_hash != null and not _is_sha256(entry.preview_hash)):
		return false
	if entry.pack_id != null and (typeof(entry.pack_id) != TYPE_STRING or entry.pack_id.strip_edges() == ""):
		return false
	if entry.pack_name != null and (typeof(entry.pack_name) != TYPE_STRING or entry.pack_name.strip_edges() == ""):
		return false
	if not _catalog_metadata_array_is_bounded(entry.search_terms, 64) or not _catalog_metadata_array_is_bounded(entry.tags, 64):
		return false
	return typeof(entry.allow_third_party_use) == TYPE_BOOL and typeof(entry.generated) == TYPE_BOOL


func _catalog_metadata_array_is_bounded(values, maximum_count):
	if typeof(values) != TYPE_ARRAY or values.size() > maximum_count:
		return false
	for value in values:
		if typeof(value) != TYPE_STRING:
			return false
	return true


func _catalog_manifest_fingerprint(manifest):
	var snapshot_at = _canonical_catalog_snapshot_at(manifest.get("snapshot_at", null))
	if snapshot_at == "":
		return ""
	var framed = ""
	framed += _catalog_framed_string("schema_version", manifest.schema_version)
	framed += _catalog_framed_string("session_id", manifest.session_id)
	framed += _catalog_framed_integer("catalog_revision", manifest.catalog_revision)
	framed += _catalog_framed_string("snapshot_at", snapshot_at)
	framed += _catalog_framed_boolean("complete", manifest.complete)
	for category in CATALOG_CATEGORIES:
		framed += _catalog_framed_integer("category[" + category + "]", manifest.category_counts.get(category, -1))
	framed += _catalog_framed_integer("chunks_count", manifest.chunks.size())
	for index in range(manifest.chunks.size()):
		var chunk = manifest.chunks[index]
		framed += _catalog_framed_string("chunk[" + str(index) + "].file_name", chunk.file_name)
		framed += _catalog_framed_string("chunk[" + str(index) + "].sha256", chunk.sha256)
		framed += _catalog_framed_integer("chunk[" + str(index) + "].entry_count", chunk.entry_count)
		framed += _catalog_framed_integer("chunk[" + str(index) + "].byte_count", chunk.byte_count)
	framed += _catalog_framed_integer("errors_count", manifest.errors.size())
	for index in range(manifest.errors.size()):
		var error = manifest.errors[index]
		framed += _catalog_framed_string("error[" + str(index) + "].code", error.code)
		framed += _catalog_framed_string("error[" + str(index) + "].message", error.message)
		framed += _catalog_framed_string("error[" + str(index) + "].category", error.category)
	return framed.sha256_text()


func _canonical_catalog_snapshot_at(value):
	if typeof(value) != TYPE_STRING or not _is_wire_timestamp(value):
		return ""
	var zone_index = value.length() - 1 if value.ends_with("Z") else value.length() - 6
	if not value.ends_with("Z") and value.substr(zone_index, 6) != "+00:00" and value.substr(zone_index, 6) != "-00:00":
		return ""
	var main = value.substr(0, zone_index)
	var decimal_index = main.find(".")
	var fraction = "" if decimal_index == -1 else main.substr(decimal_index + 1)
	var whole = main if decimal_index == -1 else main.substr(0, decimal_index)
	while fraction.length() < 7:
		fraction += "0"
	return whole + "." + fraction + "+00:00"


func _catalog_framed_string(name, value):
	if value == null:
		return name + "=null\n"
	var text = str(value)
	return name + "=" + str(text.to_utf8().size()) + ":" + text + "\n"


func _catalog_framed_integer(name, value):
	return name + "=" + str(int(value)) + "\n"


func _catalog_framed_boolean(name, value):
	return name + "=" + ("true" if value else "false") + "\n"


func _resolve_live_asset(category, resource_fingerprint):
	var listed = _asset_list_provider.call_func(category)
	if typeof(listed) != TYPE_ARRAY and typeof(listed) != TYPE_STRING_ARRAY:
		return {"ok": false, "error": _error("asset_runtime_unavailable", "The documented asset list is unavailable.", "payload.operations.asset_ref")}
	if listed.size() > MAXIMUM_CATALOG_ENTRIES:
		return {"ok": false, "error": _error("asset_runtime_unavailable", "The documented asset list exceeds its bound.", "payload.operations.asset_ref")}
	var matched_identity = null
	for value in listed:
		var resource_identity = str(value)
		if resource_identity.sha256_text() == resource_fingerprint:
			if matched_identity != null:
				return {"ok": false, "error": _error("asset_runtime_ambiguous", "The catalog asset identity is ambiguous at runtime.", "payload.operations.asset_ref")}
			matched_identity = resource_identity
	if matched_identity == null:
		return {"ok": false, "error": _error("asset_runtime_unavailable", "The catalog asset is not loaded in Dungeondraft.", "payload.operations.asset_ref")}
	return {"ok": true, "resource_identity": matched_identity}


func _get_live_asset_list(category):
	return Script.GetAssetList(category)


func _load_live_texture(resource_identity):
	return load(resource_identity)


func _universal_plan_fingerprint_input(plan):
	var text = _framed_string("schema_version=", plan.schema_version)
	text += _framed_string("request_id=", plan.request_id)
	text += "expected_map_id.present=true\n"
	text += _framed_string("expected_map_id=", plan.expected_map_id)
	text += "base_revision=" + str(int(plan.base_revision)) + "\n"
	text += "expected_catalog_revision.present=true\n"
	text += "expected_catalog_revision=" + str(int(plan.expected_catalog_revision)) + "\n"
	text += _framed_string("mode=", plan.mode)
	text += "coordinate_system.present=true\n"
	text += _framed_string("coordinate_system=", plan.coordinate_system)
	text += "canvas_width=" + str(int(plan.canvas.width)) + "\n"
	text += "canvas_height=" + str(int(plan.canvas.height)) + "\n"
	text += "operations_count=" + str(plan.operations.size()) + "\n"
	for index in range(plan.operations.size()):
		var operation = plan.operations[index]
		var prefix = "operation[" + str(index) + "]"
		text += _framed_string(prefix + ".operation_type=", operation.operation_type)
		text += _framed_string(prefix + ".operation_id=", operation.operation_id)
		text += _framed_string(prefix + ".level_id=", operation.level_id)
		text += _framed_string(prefix + ".asset_ref=", operation.asset_ref)
		if operation.operation_type in ["terrain_stroke", "wall_polyline"]:
			text += _universal_geometry_fingerprint(prefix, "path", operation.path)
		else:
			text += _universal_geometry_fingerprint(prefix, "region", operation.region)
		if operation.operation_type == "terrain_stroke":
			text += prefix + ".width=" + _double_fingerprint_text(float(operation.width)) + "\n"
			text += prefix + ".strength=" + _double_fingerprint_text(float(operation.strength)) + "\n"
		elif operation.operation_type in ["pattern_region", "colorable_pattern_region"]:
			if operation.operation_type == "colorable_pattern_region":
				text += _framed_string(prefix + ".color_rgba=", operation.color_rgba)
			text += prefix + ".rotation_degrees=" + _double_fingerprint_text(float(operation.rotation_degrees)) + "\n"
			text += prefix + ".layer=" + str(int(operation.layer)) + "\n"
		elif operation.operation_type == "cave_region":
			text += _framed_string(prefix + ".floor_color_rgba=", operation.floor_color_rgba)
			text += _framed_string(prefix + ".wall_color_rgba=", operation.wall_color_rgba)
		elif operation.operation_type == "roof_region":
			text += prefix + ".width=" + _double_fingerprint_text(float(operation.width)) + "\n"
			text += prefix + ".shade=" + _double_fingerprint_text(float(operation.shade)) + "\n"
		elif operation.operation_type == "wall_polyline":
			text += prefix + ".closed=" + ("true" if operation.closed else "false") + "\n"
			text += _framed_string(prefix + ".color_rgba=", operation.color_rgba)
	return text


func _universal_geometry_fingerprint(prefix, geometry_name, geometry):
	var text = prefix + "." + geometry_name + ".points_count=" + str(geometry.points.size()) + "\n"
	for point_index in range(geometry.points.size()):
		var point = geometry.points[point_index]
		text += prefix + "." + geometry_name + ".point[" + str(point_index) + "].x=" + _double_fingerprint_text(float(point.x)) + "\n"
		text += prefix + "." + geometry_name + ".point[" + str(point_index) + "].y=" + _double_fingerprint_text(float(point.y)) + "\n"
	return text


func _dictionary_has_exact_keys(value, expected_keys):
	if typeof(value) != TYPE_DICTIONARY or value.size() != expected_keys.size():
		return false
	for key in expected_keys:
		if not value.has(key):
			return false
	return true


func _is_nonnegative_json_safe_integer(value):
	return typeof(value) == TYPE_REAL and not is_nan(value) and not is_inf(value) and value == floor(value) and value >= 0.0 and value <= 9007199254740991.0


func _is_positive_json_safe_integer(value):
	return _is_nonnegative_json_safe_integer(value) and value > 0.0


func _double_fingerprint_text(value):
	var stream = StreamPeerBuffer.new()
	stream.big_endian = true
	stream.put_double(value)
	var bytes = stream.data_array
	if bytes.size() != 8:
		return ""
	var alphabet = "0123456789abcdef"
	var text = ""
	for byte in bytes:
		text += alphabet.substr(int(byte) >> 4, 1)
		text += alphabet.substr(int(byte) & 15, 1)
	return text


func _is_bounded_grid_point(point, canvas):
	for coordinate in [point.x, point.y]:
		if typeof(coordinate) != TYPE_REAL or is_nan(coordinate) or is_inf(coordinate) or (coordinate != 0.0 and abs(coordinate) < MINIMUM_UNIVERSAL_NONZERO_MAGNITUDE):
			return false
	return point.x >= 0.0 and point.y >= 0.0 and point.x <= float(canvas.width) and point.y <= float(canvas.height)


func _is_positive_universal_number(value):
	return typeof(value) == TYPE_REAL and not is_nan(value) and not is_inf(value) and value >= MINIMUM_UNIVERSAL_NONZERO_MAGNITUDE


func _is_unit_universal_number(value):
	return typeof(value) == TYPE_REAL and not is_nan(value) and not is_inf(value) and (value == 0.0 or abs(value) >= MINIMUM_UNIVERSAL_NONZERO_MAGNITUDE) and value >= 0.0 and value <= 1.0


func _is_rotation(value):
	return typeof(value) == TYPE_REAL and not is_nan(value) and not is_inf(value) and (value == 0.0 or abs(value) >= MINIMUM_UNIVERSAL_NONZERO_MAGNITUDE) and value >= -360.0 and value <= 360.0


func _is_supported_layer(value):
	return typeof(value) == TYPE_REAL and value == floor(value) and [-400, -100, 100, 200, 300, 400, 700, 900].has(int(value))


func _is_rgba(value):
	if typeof(value) != TYPE_STRING or value.length() != 9 or not value.begins_with("#"):
		return false
	for index in range(1, 9):
		var code = value.to_lower().ord_at(index)
		if not (code >= 48 and code <= 57) and not (code >= 97 and code <= 102):
			return false
	return value == value.to_lower()


func _rgba_color(value):
	return Color(
		float(("0x" + value.substr(1, 2)).hex_to_int()) / 255.0,
		float(("0x" + value.substr(3, 2)).hex_to_int()) / 255.0,
		float(("0x" + value.substr(5, 2)).hex_to_int()) / 255.0,
		float(("0x" + value.substr(7, 2)).hex_to_int()) / 255.0)


func _current_map_id():
	_resolve_map_identity()
	return _resolved_map_uuid


# Resolves identity once per opened world and caches it. The value is minted in
# memory when the map carries none, so it never changes mid-session; only its
# durability changes, when _bind_map_identity() persists it at mutation time.
func _resolve_map_identity():
	if Global.World == null:
		_resolved_map_uuid = ""
		_map_identity_state = "unbound"
		_resolved_map_world_id = 0
		return
	var world_id = Global.World.get_instance_id()
	if _resolved_map_uuid != "" and _resolved_map_world_id == world_id:
		return
	_resolved_map_world_id = world_id
	var stored = _read_stored_map_uuid()
	if stored != "":
		_resolved_map_uuid = stored
		_map_identity_state = "bound"
		return
	_resolved_map_uuid = _mint_map_uuid()
	_map_identity_state = "pending"


# Direct member access only. Reflecting on Global crashed Dungeondraft 1.2.0.1;
# see docs/superpowers/reports/2026-08-14-ddai-map-data-probe-crash-report.md.
func _read_stored_map_uuid():
	var data = Global.ModMapData
	if typeof(data) != TYPE_DICTIONARY or not data.has(MAP_DATA_KEY):
		return ""
	var owned = data[MAP_DATA_KEY]
	if typeof(owned) != TYPE_DICTIONARY:
		return ""
	var value = owned.get("map_uuid", null)
	return value if _is_sha256(value) else ""


# Uniqueness, not unpredictability, is the requirement.
func _mint_map_uuid():
	return (
		_framed_string("session_id=", _session_id)
		+ "world_instance_id=" + str(_resolved_map_world_id)
		+ "ticks=" + str(OS.get_ticks_usec())
		+ "\n").sha256_text()


func _current_level_ids():
	if Global.World == null or not _is_runtime_nonnegative_int32(Global.World.CurrentLevelId):
		return []
	return [str(int(Global.World.CurrentLevelId))]


func _capture_map_job_state_fingerprint():
	if Global.World == null:
		return ""
	var result = _inspect_map_payload({"region": null, "level": null, "limit": 1.0, "cursor": null})
	if not result.ok or typeof(result.payload.get("map_revision", null)) != TYPE_STRING or not _is_sha256(result.payload.map_revision):
		return ""
	return result.payload.map_revision


func _read_bounded_dictionary(path):
	var read_result = _read_bounded_text(path)
	if not read_result.ok:
		return null
	var parsed = JSON.parse(read_result.text)
	return parsed.result if parsed.error == OK and typeof(parsed.result) == TYPE_DICTIONARY else null


func _is_safe_relative_catalog_path(value):
	return typeof(value) == TYPE_STRING and value.length() > 0 and value.length() <= 240 and not value.is_abs_path() and value.find("\\") == -1 and value.find("..") == -1 and value.ends_with("/manifest.json")


func _is_safe_catalog_file_name(value):
	return typeof(value) == TYPE_STRING and value.length() > 0 and value.length() <= 128 and value.get_file() == value and value.find("/") == -1 and value.find("\\") == -1 and value.ends_with(".json")


func _advance_journaled_response(claim, request, request_fingerprint, journal_path, response_path, key, response_text):
	var directory = Directory.new()
	if not directory.file_exists(journal_path):
		var journal_write = _write_json_atomically(journal_path, {
			"schema_version": MAILBOX_SCHEMA_VERSION,
			"request_id": request.request_id,
			"request_fingerprint": request_fingerprint,
			"response_text": response_text,
		})
		return "journal_created" if journal_write == "created" else "blocked"
	var journal_result = _read_validated_journal(journal_path, request, request_fingerprint)
	if not journal_result.ok or journal_result.journal.response_text != response_text:
		return "blocked"
	if not directory.file_exists(response_path):
		return "response_published" if _write_text_atomically(response_path, response_text) == "created" else "blocked"
	if not _response_text_matches(response_path, response_text):
		return "blocked"
	var claim_remove_result = _remove_file(claim.path)
	return "claim_deleted" if claim_remove_result == "removed" or claim_remove_result == "missing" else "blocked"


func _validate_rectangular_room_plan(plan, expected_request_id):
	if typeof(plan) != TYPE_DICTIONARY:
		return {"ok": false, "error": _error("room_required", "apply_plan payload must contain a map plan object.", "payload")}
	if not plan.has("schema_version") or typeof(plan.schema_version) != TYPE_STRING or plan.schema_version != MAILBOX_SCHEMA_VERSION:
		return {"ok": false, "error": _error("malformed_plan", "Unsupported or missing plan schema_version.", "payload.schema_version")}
	if not plan.has("request_id") or typeof(plan.request_id) != TYPE_STRING or plan.request_id != expected_request_id or not _is_safe_request_id(plan.request_id):
		return {"ok": false, "error": _error("malformed_plan", "Plan request_id must match the request envelope.", "payload.request_id")}
	if not plan.has("mode") or typeof(plan.mode) != TYPE_STRING or plan.mode != "add":
		return {"ok": false, "error": _error("unsupported_mode", "Only add mode is supported.", "payload.mode")}
	if not plan.has("base_revision") or not _is_json_int32(plan.base_revision) or plan.base_revision != 0.0:
		return {"ok": false, "error": _error("unsupported_base_revision", "Only base_revision 0 is supported.", "payload.base_revision")}
	if not plan.has("canvas") or typeof(plan.canvas) != TYPE_DICTIONARY:
		return {"ok": false, "error": _error("malformed_plan", "canvas is required.", "payload.canvas")}
	var canvas = plan.canvas
	if not canvas.has("width") or not _is_positive_json_int32(canvas.width):
		return {"ok": false, "error": _error("invalid_canvas_width", "Canvas width must be a positive 32-bit integer.", "payload.canvas.width")}
	if not canvas.has("height") or not _is_positive_json_int32(canvas.height):
		return {"ok": false, "error": _error("invalid_canvas_height", "Canvas height must be a positive 32-bit integer.", "payload.canvas.height")}
	if not plan.has("rooms") or typeof(plan.rooms) != TYPE_ARRAY or plan.rooms.size() != 1:
		return {"ok": false, "error": _error("invalid_room_count", "Exactly one room is required.", "payload.rooms")}
	if typeof(plan.rooms[0]) != TYPE_DICTIONARY:
		return {"ok": false, "error": _error("room_required", "The room must be an object.", "payload.rooms[0]")}
	var room = plan.rooms[0]
	if not room.has("id") or typeof(room.id) != TYPE_STRING or not _is_safe_request_id(room.id):
		return {"ok": false, "error": _error("invalid_room_id", "Room id is missing or unsafe.", "payload.rooms[0].id")}
	if not room.has("x") or not _is_nonnegative_json_int32(room.x):
		return {"ok": false, "error": _error("invalid_room_x", "Room x must be a nonnegative 32-bit integer.", "payload.rooms[0].x")}
	if not room.has("y") or not _is_nonnegative_json_int32(room.y):
		return {"ok": false, "error": _error("invalid_room_y", "Room y must be a nonnegative 32-bit integer.", "payload.rooms[0].y")}
	if not room.has("width") or not _is_positive_json_int32(room.width):
		return {"ok": false, "error": _error("invalid_room_width", "Room width must be a positive 32-bit integer.", "payload.rooms[0].width")}
	if not room.has("height") or not _is_positive_json_int32(room.height):
		return {"ok": false, "error": _error("invalid_room_height", "Room height must be a positive 32-bit integer.", "payload.rooms[0].height")}
	if room.x > canvas.width or room.y > canvas.height or room.width > canvas.width - room.x or room.height > canvas.height - room.y:
		return {"ok": false, "error": _error("room_out_of_bounds", "Room must fit inside the canvas.", "payload.rooms[0]")}
	return {"ok": true, "plan": plan}


func _is_json_int32(value):
	return typeof(value) == TYPE_REAL and not is_nan(value) and not is_inf(value) and value == floor(value) and value >= -2147483648.0 and value <= 2147483647.0


func _is_nonnegative_json_int32(value):
	return _is_json_int32(value) and value >= 0.0


func _is_positive_json_int32(value):
	return _is_json_int32(value) and value > 0.0


func _plan_fingerprint_input(plan):
	var room = plan.rooms[0]
	var text = _framed_string("schema_version=", plan.schema_version)
	text += _framed_string("request_id=", plan.request_id)
	text += "base_revision=" + str(int(plan.base_revision)) + "\n"
	text += _framed_string("mode=", plan.mode)
	text += "canvas_width=" + str(int(plan.canvas.width)) + "\n"
	text += "canvas_height=" + str(int(plan.canvas.height)) + "\n"
	text += "rooms_count=" + str(plan.rooms.size()) + "\n"
	text += _framed_string("room[0].id=", room.id)
	text += "room[0].x=" + str(int(room.x)) + "\n"
	text += "room[0].y=" + str(int(room.y)) + "\n"
	text += "room[0].width=" + str(int(room.width)) + "\n"
	text += "room[0].height=" + str(int(room.height)) + "\n"
	return text


func _framed_string(name, value):
	return name + str(value.to_utf8().size()) + ":" + value + "\n"


func _runtime_room_preflight(plan):
	if Global.World == null:
		return {"ok": false, "error": _error("map_not_available", "No Dungeondraft map is available.", "")}
	if Global.World.Width == null or Global.World.Height == null:
		return {"ok": false, "error": _error("canvas_unavailable", "The map canvas dimensions are unavailable.", "")}
	if int(Global.World.Width) != int(plan.canvas.width) or int(Global.World.Height) != int(plan.canvas.height):
		return {"ok": false, "error": _error("canvas_mismatch", "The open map dimensions do not match the plan.", "payload.canvas")}
	if Global.World.GridSize == null or float(Global.World.GridSize) <= 0.0:
		return {"ok": false, "error": _error("grid_scale_unavailable", "The map grid scale is unavailable.", "")}
	var level = Global.World.GetLevelByID(Global.World.CurrentLevelId)
	if level == null:
		return {"ok": false, "error": _error("active_level_unavailable", "The active map level is unavailable.", "")}
	if level.Walls == null:
		return {"ok": false, "error": _error("wall_count_unavailable", "The active level wall container is unavailable.", "")}
	var wall_count = level.Walls.get_children().size()
	if wall_count < 0:
		return {"ok": false, "error": _error("wall_count_unavailable", "The active level wall count is unavailable.", "")}
	if Global.Editor == null or typeof(Global.Editor.Tools) != TYPE_DICTIONARY or not Global.Editor.Tools.has("WallTool") or Global.Editor.Tools["WallTool"] == null or Global.WorldUI == null:
		return {"ok": false, "error": _error("wall_tool_unavailable", "The documented Dungeondraft wall tool is unavailable.", "")}
	var wall_tool = Global.Editor.Tools["WallTool"]
	if wall_tool.isDrawing or Global.WorldUI.EditArcPoint or Global.WorldUI.Polyline.size() > 0:
		return {"ok": false, "error": _error("wall_tool_busy", "Finish or cancel the current manual wall before applying an AI room.", "")}
	return {"ok": true}


func _execute_rectangular_room(request, plan, plan_fingerprint):
	var execution_preflight = _runtime_room_preflight(plan)
	if not execution_preflight.ok:
		return _apply_failure(request, plan_fingerprint, execution_preflight.error.code, execution_preflight.error.message, execution_preflight.error.path, false)
	var runtime_width = Global.World.Width
	var runtime_height = Global.World.Height
	var grid_size = Global.World.GridSize
	var level_id = Global.World.CurrentLevelId
	var level = Global.World.GetLevelByID(level_id)
	var walls_before = level.Walls.get_children().size()
	var wall_tool = Global.Editor.Tools["WallTool"]
	var room = plan.rooms[0]
	var point_1 = Vector2(int(room.x) * grid_size, int(room.y) * grid_size)
	var point_2 = Vector2(int(room.x + room.width) * grid_size, int(room.y) * grid_size)
	var point_3 = Vector2(int(room.x + room.width) * grid_size, int(room.y + room.height) * grid_size)
	var point_4 = Vector2(int(room.x) * grid_size, int(room.y + room.height) * grid_size)
	wall_tool.Enable()
	Global.WorldUI.ClearPolyline()
	Global.WorldUI.AddPolyPoint(point_1)
	Global.WorldUI.AddPolyPoint(point_2)
	Global.WorldUI.AddPolyPoint(point_3)
	Global.WorldUI.AddPolyPoint(point_4)
	# Confirm() is cursor-driven: when the live cursor is not on point_1 it appends
	# that cursor as another point and creates an open wall. EndWall(true) consumes
	# only these four supplied corners and records one closed native wall operation.
	wall_tool.EndWall(true)
	var walls_after = level.Walls.get_children().size()
	var cleanup_ok = _cleanup_wall_tool(wall_tool)
	if not cleanup_ok:
		return _apply_failure(request, plan_fingerprint, "cleanup_failed", "The wall tool could not be returned to an idle state.", "", false)
	if int(runtime_width) != int(plan.canvas.width) or int(runtime_height) != int(plan.canvas.height) or not walls_after == walls_before + 1:
		return _apply_failure(request, plan_fingerprint, "wall_creation_unverified", "Dungeondraft did not report exactly one new native wall.", "", false)
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": true,
		"payload": {
			"applied": true,
			"created_walls": 1,
			"room_id": room.id,
			"undo_available": true,
			"undo_instruction": "Use Dungeondraft Undo once",
			"plan_fingerprint": plan_fingerprint,
		},
	}


func _cleanup_wall_tool(wall_tool, disable_tool = true):
	Global.WorldUI.ClearPolyline()
	if disable_tool:
		wall_tool.Disable()
	return true


func _apply_failure(request, plan_fingerprint, code, message, path, outcome_unknown):
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": false,
		"payload": {"plan_fingerprint": plan_fingerprint, "outcome_unknown": outcome_unknown},
		"error": _error(code, message, path),
	}


func _mutation_intent_payload(request, request_fingerprint, plan_fingerprint, state, response_text):
	var intent = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"request_fingerprint": request_fingerprint,
		"plan_fingerprint": plan_fingerprint,
		"command": request.command,
		"state": state,
	}
	if response_text != null:
		intent["response_text"] = response_text
	return intent


func _write_mutation_intent(path, intent):
	var text = to_json(intent)
	if text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
		return "write_failed"
	return _write_text_atomically(path, text)


func _read_validated_mutation_intent(path, key, request, request_fingerprint, plan_fingerprint, state, require_response):
	var read_result = _read_bounded_text(path)
	if not read_result.ok:
		return {"ok": false}
	var parsed = JSON.parse(read_result.text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		return {"ok": false}
	var intent = parsed.result
	if not intent.has("schema_version") or intent.schema_version != MAILBOX_SCHEMA_VERSION:
		return {"ok": false}
	if not intent.has("request_id") or intent.request_id != request.request_id or intent.request_id.sha256_text() != key:
		return {"ok": false}
	if not intent.has("request_fingerprint") or intent.request_fingerprint != request_fingerprint or not _is_sha256(intent.request_fingerprint):
		return {"ok": false}
	if not intent.has("plan_fingerprint") or intent.plan_fingerprint != plan_fingerprint or not _is_sha256(intent.plan_fingerprint):
		return {"ok": false}
	if not intent.has("command") or intent.command != "apply_plan" or not intent.has("state") or intent.state != state:
		return {"ok": false}
	if require_response:
		if not intent.has("response_text") or typeof(intent.response_text) != TYPE_STRING or intent.response_text == "" or intent.response_text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
			return {"ok": false}
		var response = JSON.parse(intent.response_text)
		if response.error != OK or typeof(response.result) != TYPE_DICTIONARY or not _validate_response(response.result, request.request_id, "apply_plan"):
			return {"ok": false}
		if typeof(response.result.payload) != TYPE_DICTIONARY or not response.result.payload.has("plan_fingerprint") or response.result.payload.plan_fingerprint != plan_fingerprint:
			return {"ok": false}
	elif intent.has("response_text"):
		return {"ok": false}
	return {"ok": true, "intent": intent}


func _is_sha256(value):
	if typeof(value) != TYPE_STRING or value.length() != 64:
		return false
	for index in range(value.length()):
		var code = value.ord_at(index)
		if not (code >= 48 and code <= 57) and not (code >= 97 and code <= 102):
			return false
	return true


func _write_mutation_conflict(key, state):
	_write_json_atomically(MAILBOX_ROOT + "/failed/" + key + ".mutation-intent-" + state + ".json", {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"error": _error("mutation_intent_conflict", "Mutation intent state is invalid or conflicting.", ""),
		"state": state,
	})


func _recover_processing_claims():
	var directory = Directory.new()
	if directory.open(MAILBOX_ROOT + "/processing") != OK:
		return
	directory.list_dir_begin(true, true)
	var file_name = directory.get_next()
	while file_name != "":
		if not directory.current_is_dir() and file_name.ends_with(".json"):
			var path = MAILBOX_ROOT + "/processing/" + file_name
			_run_claim_state_machine({"file_name": file_name, "path": path})
		file_name = directory.get_next()
	directory.list_dir_end()

	# Mutation intent cleanup is also restartable when the claim and journal are gone.
	if directory.open(MAILBOX_ROOT + "/mutation-intents") != OK:
		return
	directory.list_dir_begin(true, true)
	file_name = directory.get_next()
	while file_name != "":
		if not directory.current_is_dir() and (file_name.ends_with(".prepared.json") or file_name.ends_with(".confirmed.json") or file_name.ends_with(".ambiguous.json")):
			var key = file_name.substr(0, 64)
			_run_claim_state_machine({"file_name": key + ".json", "path": MAILBOX_ROOT + "/processing/" + key + ".json"})
		file_name = directory.get_next()
	directory.list_dir_end()

	# A crash after claim deletion leaves only journal + verified response.
	if directory.open(MAILBOX_ROOT + "/journal") != OK:
		return
	directory.list_dir_begin(true, true)
	file_name = directory.get_next()
	while file_name != "":
		if not directory.current_is_dir() and file_name.ends_with(".json"):
			_cleanup_stale_journal(file_name)
		file_name = directory.get_next()
	directory.list_dir_end()


func _claim_next_request():
	var directory = Directory.new()
	if directory.open(MAILBOX_ROOT + "/requests") != OK:
		return {}

	directory.list_dir_begin(true, true)
	var file_name = directory.get_next()
	while file_name != "":
		if not directory.current_is_dir() and file_name.ends_with(".json"):
			var source_path = MAILBOX_ROOT + "/requests/" + file_name
			var processing_path = MAILBOX_ROOT + "/processing/" + file_name
			if directory.rename(source_path, processing_path) == OK:
				directory.list_dir_end()
				return {"file_name": file_name, "path": processing_path}
		file_name = directory.get_next()
	directory.list_dir_end()
	return {}


func _claim_next_processing():
	var directory = Directory.new()
	if directory.open(MAILBOX_ROOT + "/processing") != OK:
		return {}
	var file_names = []
	directory.list_dir_begin(true, true)
	var file_name = directory.get_next()
	while file_name != "":
		if not directory.current_is_dir() and file_name.ends_with(".json"):
			file_names.append(file_name)
			if file_names.size() > MAXIMUM_PROCESSING_CLAIMS:
				directory.list_dir_end()
				return {}
		file_name = directory.get_next()
	directory.list_dir_end()
	file_names.sort()
	if file_names.size() > 0:
		_processing_claim_cursor = _processing_claim_cursor % file_names.size()
		for offset in range(file_names.size()):
			var index = (_processing_claim_cursor + offset) % file_names.size()
			file_name = file_names[index]
			_processing_claim_cursor = (index + 1) % file_names.size()
			return {"file_name": file_name, "path": MAILBOX_ROOT + "/processing/" + file_name}
	if directory.open(MAILBOX_ROOT + "/map-jobs") != OK:
		return {}
	directory.list_dir_begin(true, true)
	file_name = directory.get_next()
	while file_name != "":
		if not directory.current_is_dir() and file_name.ends_with(".json"):
			directory.list_dir_end()
			return {"file_name": file_name, "path": MAILBOX_ROOT + "/processing/" + file_name}
		file_name = directory.get_next()
	directory.list_dir_end()
	return {}


func _read_bounded_text(path):
	var file = File.new()
	if file.open(path, File.READ) != OK:
		return {"ok": false, "error": _error("malformed_request", "Claimed request could not be opened.", "")}
	if file.get_len() > MAXIMUM_MESSAGE_BYTES:
		file.close()
		return {"ok": false, "error": _error("malformed_request", "Mailbox messages must not exceed 1 MiB.", "")}
	var text = file.get_as_text()
	file.close()
	return {"ok": true, "text": text}


func _validate_request(request, file_name):
	if not request.has("schema_version") or request.schema_version != MAILBOX_SCHEMA_VERSION:
		return _error("malformed_request", "Unsupported or missing schema_version.", "schema_version")
	if not request.has("request_id") or typeof(request.request_id) != TYPE_STRING or not _is_safe_request_id(request.request_id):
		return _error("malformed_request", "request_id is missing or unsafe.", "request_id")
	if file_name.get_basename() != request.request_id.sha256_text():
		return _error("malformed_request", "Request file name does not match request_id.", "request_id")
	if not request.has("command") or typeof(request.command) != TYPE_STRING or request.command.strip_edges() == "":
		return _error("malformed_request", "command is required.", "command")
	if not request.has("timestamp") or typeof(request.timestamp) != TYPE_STRING or not _is_wire_timestamp(request.timestamp):
		return _error("malformed_request", "timestamp is required.", "timestamp")
	if not request.has("payload") or request.payload == null:
		return _error("malformed_request", "payload is required.", "payload")
	return {}


func _is_wire_timestamp(value):
	if value.length() < 20:
		return false
	var zone_index = value.length() - 1
	var zone_sign = 0
	var zone_hour_value = 0
	var zone_minute_value = 0
	if not value.ends_with("Z"):
		zone_index = value.length() - 6
		if zone_index < 19 or (value[zone_index] != "+" and value[zone_index] != "-") or value[zone_index + 3] != ":":
			return false
		var zone_hour = value.substr(zone_index + 1, 2)
		var zone_minute = value.substr(zone_index + 4, 2)
		if not _is_ascii_decimal_digits(zone_hour) or not _is_ascii_decimal_digits(zone_minute):
			return false
		zone_sign = 1 if value[zone_index] == "+" else -1
		zone_hour_value = int(zone_hour)
		zone_minute_value = int(zone_minute)
		if zone_hour_value > 14 or zone_minute_value > 59 or (zone_hour_value == 14 and zone_minute_value != 0):
			return false
		if zone_hour_value == 0 and zone_minute_value == 0:
			zone_sign = 0
	var main = value.substr(0, zone_index)
	var decimal_index = main.find(".")
	var fraction_nonzero = false
	if decimal_index != -1:
		var fraction = main.substr(decimal_index + 1, main.length() - decimal_index - 1)
		if fraction == "" or fraction.length() > 7 or not _is_ascii_decimal_digits(fraction):
			return false
		fraction_nonzero = int(fraction) != 0
		main = main.substr(0, decimal_index)
	if main.length() != 19:
		return false
	if main[4] != "-" or main[7] != "-" or main[10] != "T" or main[13] != ":" or main[16] != ":":
		return false
	var digits = main.substr(0, 4) + main.substr(5, 2) + main.substr(8, 2) + main.substr(11, 2) + main.substr(14, 2) + main.substr(17, 2)
	if not _is_ascii_decimal_digits(digits):
		return false
	var year = int(main.substr(0, 4))
	var month = int(main.substr(5, 2))
	var day = int(main.substr(8, 2))
	var hour = int(main.substr(11, 2))
	var minute = int(main.substr(14, 2))
	var second = int(main.substr(17, 2))
	var calendar_valid = year >= 1 and month >= 1 and month <= 12 and day >= 1 and day <= _days_in_month(year, month) and hour <= 23 and minute <= 59 and second <= 59
	if not calendar_valid:
		return false
	var local_seconds = hour * 3600 + minute * 60 + second
	var offset_seconds = zone_hour_value * 3600 + zone_minute_value * 60
	var minimum_date = year == 1 and month == 1 and day == 1
	if minimum_date and zone_sign >= 0:
		if local_seconds < offset_seconds:
			return false
		if local_seconds == offset_seconds and not fraction_nonzero:
			return false
	var maximum_date = year == 9999 and month == 12 and day == 31
	if maximum_date and zone_sign < 0 and local_seconds + offset_seconds >= 86400:
		return false
	return true


func _is_ascii_decimal_digits(value):
	if value.length() == 0:
		return false
	for index in range(value.length()):
		var code = value.ord_at(index)
		if code < 48 or code > 57:
			return false
	return true


func _days_in_month(year, month):
	if month == 2:
		return 29 if (year % 4 == 0 and (year % 100 != 0 or year % 400 == 0)) else 28
	return 30 if month == 4 or month == 6 or month == 9 or month == 11 else 31


func _is_safe_request_id(request_id):
	return request_id.strip_edges() != "" and request_id.find("/") == -1 and request_id.find("\\") == -1 and request_id != "." and request_id != ".."


func _canonical_request_text(request):
	return to_json({
		"schema_version": request.schema_version,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": request.timestamp,
		"payload": request.payload,
	})


func _reconcile_request_duplicate(file_name, canonical_processing_text):
	var request_path = MAILBOX_ROOT + "/requests/" + file_name
	var directory = Directory.new()
	if not directory.file_exists(request_path):
		return "reconciled"
	var duplicate_read = _read_bounded_text(request_path)
	if duplicate_read.ok:
		var duplicate_parsed = JSON.parse(duplicate_read.text)
		if duplicate_parsed.error == OK and typeof(duplicate_parsed.result) == TYPE_DICTIONARY:
			var duplicate = duplicate_parsed.result
			if not _validate_request(duplicate, file_name).has("code") and _canonical_request_text(duplicate) == canonical_processing_text:
				var remove_result = _remove_file(request_path)
				if remove_result == "removed" or remove_result == "missing":
					return "reconciled"
				return "blocked"
	var move_result = _move_to_unique_failed(request_path, file_name.get_basename(), "duplicate-conflict")
	if move_result == "moved" or move_result == "missing":
		return "reconciled"
	return "blocked"


func _fail_claim_without_loss(claim, error):
	var failed_result = _write_failed_record(claim, error)
	if failed_result != "created":
		return "blocked"
	var remove_result = _remove_file(claim.path)
	return "invalid_claim_failed" if remove_result == "removed" or remove_result == "missing" else "blocked"


func _prepare_response(request):
	if request.command == "status":
		var response_timestamp = _iso_timestamp()
		var response_payload = _status_payload()
		return {
			"schema_version": MAILBOX_SCHEMA_VERSION,
			"request_id": request.request_id,
			"command": request.command,
			"timestamp": response_timestamp,
			"success": true,
			"payload": response_payload,
		}
	if request.command == "inspect_map":
		return _inspect_map_response(request)
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": false,
		"payload": {},
		"error": _error("unsupported_command", "This mod does not support the requested command.", "command"),
	}


func _inspect_map_response(request):
	var result = _inspect_map_payload(request.payload)
	if not result.ok:
		return _inspect_map_failure(request, result.error.code, result.error.message, result.error.path)
	var response = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": true,
		"payload": result.payload,
	}
	if to_json(response).to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
		return _inspect_map_failure(request, "response_too_large", "The bounded inspection response exceeds 1 MiB.", "payload")
	return response


func _inspect_map_payload(payload):
	if typeof(payload) != TYPE_DICTIONARY or payload.size() != 4:
		return {"ok": false, "error": _error("invalid_request", "inspect_map payload must contain exactly region, level, limit, and cursor.", "payload")}
	for key in ["region", "level", "limit", "cursor"]:
		if not payload.has(key):
			return {"ok": false, "error": _error("invalid_request", "inspect_map payload is missing a required field.", "payload." + key)}
	if not _is_positive_json_int32(payload.limit) or payload.limit > MAXIMUM_INSPECTION_LIMIT:
		return {"ok": false, "error": _error("invalid_request", "Inspection limit must be between 1 and 500.", "payload.limit")}
	if payload.level != null and not _is_nonnegative_json_int32(payload.level):
		return {"ok": false, "error": _error("invalid_request", "Inspection level must be a non-negative integer or null.", "payload.level")}
	if Global.World == null:
		return {"ok": false, "error": _error("map_not_available", "No Dungeondraft map is available.", "")}
	if not _is_runtime_positive_int32(Global.World.Width) or not _is_runtime_positive_int32(Global.World.Height):
		return {"ok": false, "error": _error("map_not_available", "The current map canvas is unavailable.", "")}
	if not _is_runtime_positive_finite_number(Global.World.GridSize):
		return {"ok": false, "error": _error("map_not_available", "The current map grid scale is unavailable.", "")}
	if not _is_runtime_nonnegative_int32(Global.World.CurrentLevelId):
		return {"ok": false, "error": _error("active_level_unavailable", "The current map level ID is invalid.", "")}
	var current_level_id = int(Global.World.CurrentLevelId)
	if payload.level != null and int(payload.level) != current_level_id:
		return {"ok": false, "error": _error("unsupported_level", "Only the documented current level can be inspected.", "payload.level")}
	var level = Global.World.GetLevelByID(current_level_id)
	if level == null:
		return {"ok": false, "error": _error("active_level_unavailable", "The current map level is unavailable.", "")}
	if level.Walls == null or level.Pathways == null or level.Roofs == null or level.PatternShapes == null or level.Objects == null:
		return {"ok": false, "error": _error("active_level_unavailable", "A documented current-level inspection container is unavailable.", "")}

	var region_result = _inspection_region(payload.region, int(Global.World.Width), int(Global.World.Height))
	if not region_result.ok:
		return region_result
	var levels_result = _inspection_levels(current_level_id, level)
	if not levels_result.ok:
		return levels_result
	var state = {"items": [], "node_ids": {}, "object_correlation_unavailable": false}
	var grid_size = float(Global.World.GridSize)
	# This is a fixed allowlist of documented public containers on the current
	# Level. It deliberately does not reflect over or recursively crawl the scene.
	var fixed_container_count = level.Walls.get_child_count() + level.Pathways.get_child_count() + level.Roofs.get_child_count() + level.Objects.get_child_count()
	if fixed_container_count < 0 or fixed_container_count > MAXIMUM_INSPECTION_STATE_ITEMS:
		return {"ok": false, "error": _error("inspection_state_too_large", "The documented inspection state exceeds its item bound.", "")}
	var pattern_shapes = level.PatternShapes.GetShapes()
	if typeof(pattern_shapes) != TYPE_ARRAY:
		return {"ok": false, "error": _error("inspection_state_invalid", "The documented pattern shape container did not return an array.", "")}
	if pattern_shapes.size() > MAXIMUM_INSPECTION_STATE_ITEMS - fixed_container_count:
		return {"ok": false, "error": _error("inspection_state_too_large", "The documented inspection state exceeds its item bound.", "")}
	var append_result = _append_inspection_container(level.Walls.get_children(), "wall", state, current_level_id, grid_size)
	if not append_result.ok:
		return append_result
	append_result = _append_inspection_container(level.Pathways.get_children(), "path", state, current_level_id, grid_size)
	if not append_result.ok:
		return append_result
	append_result = _append_inspection_container(level.Roofs.get_children(), "roof", state, current_level_id, grid_size)
	if not append_result.ok:
		return append_result
	append_result = _append_inspection_container(pattern_shapes, "pattern_shape", state, current_level_id, grid_size)
	if not append_result.ok:
		return append_result
	append_result = _append_inspection_container(level.Objects.get_children(), "object", state, current_level_id, grid_size)
	if not append_result.ok:
		return append_result
	var unsupported_kinds = ["portal", "light", "text", "material", "floor_shape"]
	if state.object_correlation_unavailable:
		unsupported_kinds.append("object_asset_correlation")
	var revision_result = _inspection_state_revision(
		int(Global.World.Width),
		int(Global.World.Height),
		grid_size,
		levels_result.levels,
		state.items,
		unsupported_kinds)
	if not revision_result.ok:
		return revision_result
	var map_revision = revision_result.revision
	var map_id = (_framed_string("session_id=", _session_id) + "world_instance_id=" + str(Global.World.get_instance_id()) + "\n").sha256_text()
	var offset_result = _inspection_cursor_offset(
		payload.cursor,
		map_id,
		map_revision,
		current_level_id,
		payload.region)
	if not offset_result.ok:
		return offset_result

	var matching_items = []
	for item in state.items:
		if _inspection_bounds_intersect(item.bounds, region_result.rect):
			matching_items.append(item)
	if offset_result.offset > matching_items.size() or (payload.cursor != null and offset_result.offset == matching_items.size()):
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor offset does not identify a remaining page.", "payload.cursor")}
	var page_items = []
	var page_end = min(offset_result.offset + int(payload.limit), matching_items.size())
	for index in range(offset_result.offset, page_end):
		page_items.append(matching_items[index])
	var truncated = page_end < matching_items.size()
	var next_cursor = null
	if truncated:
		if page_end > MAXIMUM_INSPECTION_CURSOR_OFFSET:
			return {"ok": false, "error": _error("invalid_cursor", "The inspection cursor exceeds its bounded offset.", "payload.cursor")}
		next_cursor = _inspection_cursor_value(map_id, map_revision, current_level_id, payload.region, page_end)
	return {"ok": true, "payload": {
		"map_id": map_id,
		"map_revision": map_revision,
		"canvas": {"width": int(Global.World.Width), "height": int(Global.World.Height)},
		"grid_size": grid_size,
		"levels": levels_result.levels,
		"items": page_items,
		"next_cursor": next_cursor,
		"truncated": truncated,
		"unsupported_kinds": unsupported_kinds,
	}}


func _inspection_levels(current_level_id, expected_current_level):
	if typeof(Global.World.levels) != TYPE_ARRAY or Global.World.levels.size() == 0 or Global.World.levels.size() > MAXIMUM_INSPECTION_LEVELS:
		return {"ok": false, "error": _error("inspection_state_invalid", "The documented map level structure is invalid or exceeds its bound.", "")}
	var levels = []
	var level_ids = {}
	var current_count = 0
	for map_level in Global.World.levels:
		if map_level == null or not _is_runtime_nonnegative_int32(map_level.ID) or typeof(map_level.Label) != TYPE_STRING or map_level.Label.strip_edges() == "" or map_level.Label.length() > MAXIMUM_INSPECTION_LABEL_LENGTH:
			return {"ok": false, "error": _error("inspection_state_invalid", "A documented map level record is invalid.", "")}
		var level_id = int(map_level.ID)
		if level_ids.has(level_id):
			return {"ok": false, "error": _error("inspection_state_invalid", "Documented map level IDs must be unique.", "")}
		level_ids[level_id] = true
		var current = level_id == current_level_id
		if current:
			if map_level != expected_current_level:
				return {"ok": false, "error": _error("inspection_state_invalid", "The current level lookup does not match the public level structure.", "")}
			current_count += 1
		levels.append({"id": level_id, "label": map_level.Label, "current": current})
	if current_count != 1:
		return {"ok": false, "error": _error("active_level_unavailable", "The current level is not represented exactly once.", "")}
	return {"ok": true, "levels": levels}


func _append_inspection_container(nodes, kind, state, level_id, grid_size):
	if typeof(nodes) != TYPE_ARRAY:
		return {"ok": false, "error": _error("inspection_state_invalid", "A documented inspection container did not return an array.", "")}
	for node in nodes:
		if state.items.size() >= MAXIMUM_INSPECTION_STATE_ITEMS:
			return {"ok": false, "error": _error("inspection_state_too_large", "The documented inspection state exceeds its item bound.", "")}
		var item_result = _inspection_item(node, kind, level_id, grid_size)
		if not item_result.ok:
			return item_result
		var item = item_result.item
		if state.node_ids.has(item.node_id):
			return {"ok": false, "error": _error("inspection_state_invalid", "Native inspection node IDs must be unique.", "")}
		state.node_ids[item.node_id] = true
		state.items.append(item)
		if item_result.get("correlation_unavailable", false):
			state.object_correlation_unavailable = true
	return {"ok": true}


func _inspection_item(node, kind, level_id, grid_size):
	if node == null:
		return {"ok": false, "error": _error("inspection_state_invalid", "A documented inspection container contains a null node.", "")}
	var global_rect = null
	if kind == "wall":
		global_rect = node.GlobalRect
	elif kind == "path":
		global_rect = node.GlobalRect
	elif kind == "roof":
		global_rect = node.GlobalRect
	elif kind == "pattern_shape":
		global_rect = node.GlobalRect
	elif kind == "object":
		global_rect = node.Rect
	else:
		return {"ok": false, "error": _error("inspection_state_invalid", "Inspection item kind is not supported.", "")}
	if typeof(global_rect) != TYPE_RECT2 or not _inspection_rect_is_finite_positive(global_rect):
		return {"ok": false, "error": _error("inspection_state_invalid", "A native inspection item has invalid global bounds.", "")}
	var normalized_node_id = _node_id_metadata(node)
	if not normalized_node_id.ok:
		var reason = normalized_node_id.get("reason", "invalid_value")
		var message = "A native %s has an invalid node ID (type_code=%d, reason=%s)." % [kind, normalized_node_id.type_code, reason]
		return {"ok": false, "error": _error("inspection_state_invalid", message, "")}
	var node_id = normalized_node_id.value
	var resource_fingerprint = null
	var correlation_unavailable = false
	if kind == "object":
		if node.Sprite == null or node.Sprite.texture == null or str(node.Sprite.texture.resource_path).length() == 0:
			correlation_unavailable = true
		else:
			resource_fingerprint = str(node.Sprite.texture.resource_path).sha256_text()
	var item = {
		"node_id": node_id,
		"kind": kind,
		"bounds": {
			"x": global_rect.position.x / grid_size,
			"y": global_rect.position.y / grid_size,
			"width": global_rect.size.x / grid_size,
			"height": global_rect.size.y / grid_size,
		},
		"level": level_id,
		# Public container/node contracts do not expose a canonical catalog ID.
		# Never guess one or enumerate the filesystem; unresolved references are null.
		"asset_ref": null,
		"resource_fingerprint": resource_fingerprint,
	}
	return {"ok": true, "item": item, "correlation_unavailable": correlation_unavailable}


func _inspection_rect_is_finite_positive(rect):
	for value in [rect.position.x, rect.position.y, rect.size.x, rect.size.y]:
		if is_nan(float(value)) or is_inf(float(value)):
			return false
	return rect.size.x >= 0.0 and rect.size.y >= 0.0 and (rect.size.x > 0.0 or rect.size.y > 0.0)


func _is_runtime_nonnegative_safe_integer(value):
	return (typeof(value) == TYPE_INT or typeof(value) == TYPE_REAL) and not is_nan(float(value)) and not is_inf(float(value)) and float(value) == floor(float(value)) and float(value) >= 0.0 and float(value) <= 9007199254740991.0


func _runtime_node_id(value):
	var type_code = typeof(value)
	if type_code == TYPE_INT and value >= 0 and value <= 9007199254740991:
		return {"ok": true, "value": int(value)}
	return {"ok": false, "type_code": type_code, "reason": "unsupported_type" if type_code != TYPE_INT else "outside_safe_range"}


func _node_id_metadata(node):
	return _runtime_node_id(node.get_meta("node_id") if node != null and node.has_meta("node_id") else null)


func _is_runtime_nonnegative_int32(value):
	return _is_runtime_nonnegative_safe_integer(value) and float(value) <= 2147483647.0


func _is_runtime_positive_int32(value):
	return _is_runtime_nonnegative_int32(value) and float(value) > 0.0


func _is_runtime_positive_finite_number(value):
	return (typeof(value) == TYPE_INT or typeof(value) == TYPE_REAL) and not is_nan(float(value)) and not is_inf(float(value)) and float(value) > 0.0


func _inspection_region(region, canvas_width, canvas_height):
	if region == null:
		return {"ok": true, "rect": null}
	if typeof(region) != TYPE_DICTIONARY or region.size() != 4:
		return {"ok": false, "error": _error("invalid_region", "Inspection region must contain exactly x, y, width, and height.", "payload.region")}
	for key in ["x", "y", "width", "height"]:
		if not region.has(key) or typeof(region[key]) != TYPE_REAL or is_nan(region[key]) or is_inf(region[key]):
			return {"ok": false, "error": _error("invalid_region", "Inspection region values must be finite numbers.", "payload.region." + key)}
		if float("%.6f" % region[key]) != region[key]:
			return {"ok": false, "error": _error("invalid_region", "Inspection region values may use at most six decimal places.", "payload.region." + key)}
	if region.x < 0.0 or region.y < 0.0 or region.width <= 0.0 or region.height <= 0.0:
		return {"ok": false, "error": _error("invalid_region", "Inspection region must be non-negative and have positive size.", "payload.region")}
	if region.x > canvas_width or region.y > canvas_height or region.width > canvas_width - region.x or region.height > canvas_height - region.y:
		return {"ok": false, "error": _error("invalid_region", "Inspection region must fit within the current map canvas.", "payload.region")}
	return {"ok": true, "rect": Rect2(region.x, region.y, region.width, region.height)}


func _inspection_bounds_intersect(bounds, region_rect):
	if region_rect == null:
		return true
	return bounds.x < region_rect.position.x + region_rect.size.x and bounds.x + bounds.width > region_rect.position.x and bounds.y < region_rect.position.y + region_rect.size.y and bounds.y + bounds.height > region_rect.position.y


func _inspection_cursor_value(map_id, map_revision, level_id, region, offset):
	var text = "map_id=" + map_id + "\n"
	text += "map_revision=" + map_revision + "\n"
	text += "level=" + str(level_id) + "\n"
	if region == null:
		text += "region=null\n"
	else:
		text += "region=" + ("%.6f" % region.x) + "," + ("%.6f" % region.y) + "," + ("%.6f" % region.width) + "," + ("%.6f" % region.height) + "\n"
	text += "offset=" + str(offset) + "\n"
	return text.sha256_text() + ":" + str(offset)


func _inspection_cursor_offset(cursor, map_id, map_revision, level_id, region):
	if cursor == null:
		return {"ok": true, "offset": 0}
	if typeof(cursor) != TYPE_STRING or cursor.length() < 66 or cursor.length() > 71 or cursor.substr(64, 1) != ":":
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor is malformed.", "payload.cursor")}
	var fingerprint = cursor.substr(0, 64)
	var digits = cursor.substr(65, cursor.length() - 65)
	if not _is_sha256(fingerprint) or not _is_ascii_decimal_digits(digits) or (digits.length() > 1 and digits.begins_with("0")):
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor is not canonical.", "payload.cursor")}
	var offset = int(digits)
	if offset < 0 or offset > MAXIMUM_INSPECTION_CURSOR_OFFSET:
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor offset is outside the bounded range.", "payload.cursor")}
	if cursor != _inspection_cursor_value(map_id, map_revision, level_id, region, offset):
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor does not match this exact map state, level, filters, and offset.", "payload.cursor")}
	return {"ok": true, "offset": offset}


func _inspection_state_revision(canvas_width, canvas_height, grid_size, levels, items, unsupported_kinds):
	var context = HashingContext.new()
	if context.start(HashingContext.HASH_SHA256) != OK:
		return {"ok": false, "error": _error("inspection_unavailable", "The inspection revision hash could not be initialized.", "")}
	_inspection_hash_value(context, "canvas_width", canvas_width)
	_inspection_hash_value(context, "canvas_height", canvas_height)
	_inspection_hash_value(context, "grid_size", grid_size)
	_inspection_hash_value(context, "levels_count", levels.size())
	for level_index in range(levels.size()):
		var map_level = levels[level_index]
		_inspection_hash_value(context, "level_index", level_index)
		_inspection_hash_value(context, "level_id", map_level.id)
		_inspection_hash_value(context, "level_label", map_level.label)
		_inspection_hash_value(context, "level_current", map_level.current)
	_inspection_hash_value(context, "items_count", items.size())
	for item_index in range(items.size()):
		var item = items[item_index]
		_inspection_hash_value(context, "item_index", item_index)
		_inspection_hash_value(context, "item_node_id", item.node_id)
		_inspection_hash_value(context, "item_kind", item.kind)
		_inspection_hash_value(context, "item_x", item.bounds.x)
		_inspection_hash_value(context, "item_y", item.bounds.y)
		_inspection_hash_value(context, "item_width", item.bounds.width)
		_inspection_hash_value(context, "item_height", item.bounds.height)
		_inspection_hash_value(context, "item_level", item.level)
		_inspection_hash_value(context, "item_asset_ref", item.asset_ref)
		_inspection_hash_value(context, "item_resource_fingerprint", item.resource_fingerprint)
	_inspection_hash_value(context, "unsupported_count", unsupported_kinds.size())
	for unsupported_index in range(unsupported_kinds.size()):
		_inspection_hash_value(context, "unsupported_index", unsupported_index)
		_inspection_hash_value(context, "unsupported_kind", unsupported_kinds[unsupported_index])
	return {"ok": true, "revision": context.finish().hex_encode()}


func _inspection_hash_value(context, name, value):
	var encoded = var2bytes(value)
	context.update((name + "=" + str(encoded.size()) + ":").to_utf8())
	context.update(encoded)
	context.update("\n".to_utf8())


func _inspect_map_failure(request, code, message, path):
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": false,
		"payload": {},
		"error": _error(code, message, path),
	}


func _read_validated_journal(path, request, expected_fingerprint):
	var result = _read_journal_envelope(path)
	if not result.ok:
		return result
	var journal = result.journal
	if journal.request_id != request.request_id or journal.request_fingerprint != expected_fingerprint:
		return {"ok": false}
	var parsed_response = JSON.parse(journal.response_text)
	if parsed_response.error != OK or typeof(parsed_response.result) != TYPE_DICTIONARY or not _validate_response(parsed_response.result, request.request_id, request.command):
		return {"ok": false}
	return result


func _read_journal_envelope(path):
	var read_result = _read_bounded_text(path)
	if not read_result.ok:
		return {"ok": false}
	var parsed = JSON.parse(read_result.text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		return {"ok": false}
	var journal = parsed.result
	if not journal.has("schema_version") or journal.schema_version != MAILBOX_SCHEMA_VERSION:
		return {"ok": false}
	if not journal.has("request_id") or typeof(journal.request_id) != TYPE_STRING or not _is_safe_request_id(journal.request_id):
		return {"ok": false}
	if not journal.has("request_fingerprint") or typeof(journal.request_fingerprint) != TYPE_STRING or journal.request_fingerprint.length() != 64:
		return {"ok": false}
	if not journal.has("response_text") or typeof(journal.response_text) != TYPE_STRING or journal.response_text == "" or journal.response_text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
		return {"ok": false}
	return {"ok": true, "journal": journal}


func _validate_response(response, expected_request_id, expected_command):
	if not response.has("schema_version") or response.schema_version != MAILBOX_SCHEMA_VERSION:
		return false
	if not response.has("request_id") or response.request_id != expected_request_id:
		return false
	if not response.has("command") or typeof(response.command) != TYPE_STRING or response.command != expected_command or response.command.strip_edges() == "":
		return false
	if not response.has("timestamp") or typeof(response.timestamp) != TYPE_STRING or not _is_wire_timestamp(response.timestamp):
		return false
	if not response.has("success") or typeof(response.success) != TYPE_BOOL:
		return false
	if not response.has("payload") or response.payload == null:
		return false
	if response.success:
		return not response.has("error")
	return response.has("error") and typeof(response.error) == TYPE_DICTIONARY and response.error.has("code") and typeof(response.error.code) == TYPE_STRING and response.error.code.strip_edges() != "" and response.error.has("message") and typeof(response.error.message) == TYPE_STRING and response.error.message.strip_edges() != ""


func _response_text_matches(path, expected_response_text):
	var result = _read_bounded_text(path)
	if not result.ok:
		return false
	if result.text == expected_response_text:
		return true
	var existing = JSON.parse(result.text)
	var expected = JSON.parse(expected_response_text)
	return existing.error == OK and expected.error == OK and typeof(existing.result) == TYPE_DICTIONARY and typeof(expected.result) == TYPE_DICTIONARY and to_json(existing.result) == to_json(expected.result)


func _advance_without_claim(file_name):
	var map_job_cleanup = _cleanup_completed_map_job(file_name)
	if map_job_cleanup != "no_work":
		return map_job_cleanup
	var journal_result = _cleanup_stale_journal(file_name)
	if journal_result != "no_work":
		return journal_result
	var key = file_name.get_basename()
	if not _is_sha256(key):
		return "blocked"
	var response_path = MAILBOX_ROOT + "/responses/" + file_name
	var prepared_path = MAILBOX_ROOT + "/mutation-intents/" + key + ".prepared.json"
	var confirmed_path = MAILBOX_ROOT + "/mutation-intents/" + key + ".confirmed.json"
	var ambiguous_path = MAILBOX_ROOT + "/mutation-intents/" + key + ".ambiguous.json"
	var directory = Directory.new()
	if directory.file_exists(ambiguous_path):
		var ambiguous = _read_standalone_mutation_intent(ambiguous_path, key, "ambiguous", true)
		if not ambiguous.ok:
			return "blocked"
		var diagnostic_path = MAILBOX_ROOT + "/failed/" + key + ".mutation-outcome-unknown.json"
		if not directory.file_exists(diagnostic_path):
			return "mutation_ambiguity_recorded" if _write_text_atomically(diagnostic_path, ambiguous.text) == "created" else "blocked"
		return "no_work"
	if directory.file_exists(confirmed_path):
		var confirmed = _read_standalone_mutation_intent(confirmed_path, key, "confirmed", true)
		if not confirmed.ok or not directory.file_exists(response_path) or not _response_text_matches(response_path, confirmed.intent.response_text):
			return "blocked"
		return "mutation_intent_deleted" if _remove_file(confirmed_path) == "removed" else "blocked"
	if directory.file_exists(prepared_path):
		var prepared = _read_standalone_mutation_intent(prepared_path, key, "prepared", false)
		if not prepared.ok or not directory.file_exists(response_path):
			return "blocked"
		var response_read = _read_bounded_text(response_path)
		if not response_read.ok:
			return "blocked"
		var response = JSON.parse(response_read.text)
		if response.error != OK or typeof(response.result) != TYPE_DICTIONARY or not _validate_response(response.result, prepared.intent.request_id, "apply_plan"):
			return "blocked"
		if typeof(response.result.payload) != TYPE_DICTIONARY or not response.result.payload.has("plan_fingerprint") or response.result.payload.plan_fingerprint != prepared.intent.plan_fingerprint:
			return "blocked"
		return "mutation_intent_deleted" if _remove_file(prepared_path) == "removed" else "blocked"
	return "no_work"


func _cleanup_completed_map_job(file_name):
	var key = file_name.get_basename()
	if not _is_sha256(key):
		return "no_work"
	var job_path = MAILBOX_ROOT + "/map-jobs/" + key + ".json"
	var completed_path = MAILBOX_ROOT + "/map-completed/" + key + ".json"
	var response_path = MAILBOX_ROOT + "/responses/" + key + ".json"
	var directory = Directory.new()
	if not directory.file_exists(job_path):
		return "no_work"
	var completed = _read_bounded_dictionary(completed_path)
	var job = _read_bounded_dictionary(job_path)
	var job_read = _read_bounded_text(job_path)
	if completed == null or job == null or not job_read.ok or not _dictionary_has_exact_keys(completed, ["schema_version", "request_id", "request_fingerprint", "plan_fingerprint", "journal_fingerprint", "response_fingerprint", "response_text"]):
		return "blocked"
	if completed.schema_version != MAILBOX_SCHEMA_VERSION or typeof(completed.request_id) != TYPE_STRING or completed.request_id.sha256_text() != key or not _is_sha256(completed.request_fingerprint) or not _is_sha256(completed.plan_fingerprint) or not _is_sha256(completed.journal_fingerprint) or job_read.text.sha256_text() != completed.journal_fingerprint or not _is_sha256(completed.response_fingerprint):
		return "blocked"
	if typeof(completed.response_text) != TYPE_STRING or completed.response_text.sha256_text() != completed.response_fingerprint or completed.response_text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
		return "blocked"
	if job.get("request_id", null) != completed.request_id or job.get("request_fingerprint", null) != completed.request_fingerprint or job.get("plan_fingerprint", null) != completed.plan_fingerprint or job.get("state", null) != "committed" or job.get("canonical_response", null) != completed.response_text:
		return "blocked"
	var parsed_response = JSON.parse(completed.response_text)
	if parsed_response.error != OK or typeof(parsed_response.result) != TYPE_DICTIONARY or not _validate_response(parsed_response.result, completed.request_id, "apply_plan") or typeof(parsed_response.result.payload) != TYPE_DICTIONARY or parsed_response.result.payload.get("plan_fingerprint", "") != completed.plan_fingerprint:
		return "blocked"
	if not directory.file_exists(response_path) or not _response_text_matches(response_path, completed.response_text):
		return "blocked"
	var rollback_path = MAILBOX_ROOT + "/surface-rollbacks/" + key
	var undoable_path = MAILBOX_ROOT + "/map-undoable/" + key + ".json"
	if not directory.file_exists(undoable_path) and directory.dir_exists(rollback_path) and not _remove_directory_tree_bounded(rollback_path, 4096):
		return "blocked"
	if not ["removed", "missing"].has(_remove_file(job_path)):
		return "blocked"
	return "map_job_journal_deleted"


func _remove_directory_tree_bounded(path, remaining_entries):
	if remaining_entries <= 0:
		return false
	var directory = Directory.new()
	if not directory.dir_exists(path):
		return true
	if directory.open(path) != OK:
		return false
	directory.list_dir_begin(true, true)
	var names = []
	var name = directory.get_next()
	while name != "":
		if names.size() >= remaining_entries or name == "." or name == "..":
			directory.list_dir_end()
			return false
		names.append({"name": name, "directory": directory.current_is_dir()})
		name = directory.get_next()
	directory.list_dir_end()
	for entry in names:
		var child = path + "/" + entry.name
		if entry.directory:
			if not _remove_directory_tree_bounded(child, remaining_entries - names.size()):
				return false
		elif directory.remove(child) != OK:
			return false
	return directory.remove(path) == OK


func _read_standalone_mutation_intent(path, key, state, require_response):
	var read_result = _read_bounded_text(path)
	if not read_result.ok:
		return {"ok": false}
	var parsed = JSON.parse(read_result.text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		return {"ok": false}
	var intent = parsed.result
	if not intent.has("schema_version") or intent.schema_version != MAILBOX_SCHEMA_VERSION:
		return {"ok": false}
	if not intent.has("request_id") or typeof(intent.request_id) != TYPE_STRING or intent.request_id.sha256_text() != key:
		return {"ok": false}
	if not intent.has("request_fingerprint") or not _is_sha256(intent.request_fingerprint):
		return {"ok": false}
	if not intent.has("plan_fingerprint") or not _is_sha256(intent.plan_fingerprint):
		return {"ok": false}
	if not intent.has("command") or intent.command != "apply_plan" or not intent.has("state") or intent.state != state:
		return {"ok": false}
	if require_response:
		if not intent.has("response_text") or typeof(intent.response_text) != TYPE_STRING or intent.response_text == "" or intent.response_text.to_utf8().size() > MAXIMUM_MESSAGE_BYTES:
			return {"ok": false}
		var response = JSON.parse(intent.response_text)
		if response.error != OK or typeof(response.result) != TYPE_DICTIONARY or not _validate_response(response.result, intent.request_id, "apply_plan"):
			return {"ok": false}
		if typeof(response.result.payload) != TYPE_DICTIONARY or not response.result.payload.has("plan_fingerprint") or response.result.payload.plan_fingerprint != intent.plan_fingerprint:
			return {"ok": false}
	elif intent.has("response_text"):
		return {"ok": false}
	return {"ok": true, "intent": intent, "text": read_result.text}


func _cleanup_stale_journal(file_name):
	var journal_path = MAILBOX_ROOT + "/journal/" + file_name
	var response_path = MAILBOX_ROOT + "/responses/" + file_name
	var directory = Directory.new()
	if not directory.file_exists(journal_path):
		return "no_work"
	var result = _read_journal_envelope(journal_path)
	if not result.ok:
		return "blocked"
	var journal = result.journal
	if journal.request_id.sha256_text() != file_name.get_basename() or not directory.file_exists(response_path):
		return "blocked"
	var response = JSON.parse(journal.response_text)
	if response.error != OK or typeof(response.result) != TYPE_DICTIONARY or not response.result.has("command"):
		return "blocked"
	if not _validate_response(response.result, journal.request_id, response.result.command):
		return "blocked"
	if not _response_text_matches(response_path, journal.response_text):
		return "blocked"
	var journal_remove_result = _remove_file(journal_path)
	return "journal_deleted" if journal_remove_result == "removed" or journal_remove_result == "missing" else "blocked"


func _status_payload():
	var current_state_fingerprint = _capture_map_job_state_fingerprint()
	if current_state_fingerprint != "":
		if _map_job_state_fingerprint != "" and _map_job_state_fingerprint != current_state_fingerprint:
			_map_job_revision += 1
		_map_job_state_fingerprint = current_state_fingerprint
	return {
		"mod_version": MOD_VERSION,
		"dungeondraft_version": null,
		"dungeondraft_version_available": false,
		"target_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"map_loaded": Global.World != null,
		"current_level": int(Global.World.CurrentLevelId) if Global.World != null and _is_runtime_nonnegative_int32(Global.World.CurrentLevelId) else null,
		"dimensions": {"width": int(Global.World.Width), "height": int(Global.World.Height)} if Global.World != null and _is_runtime_positive_int32(Global.World.Width) and _is_runtime_positive_int32(Global.World.Height) else null,
		"revision": _map_job_revision,
		"map_id": _current_map_id(),
		"map_job_revision": _map_job_revision,
		"level_ids": _current_level_ids(),
		"certified_operation_types": _certified_operation_types(),
		"operation_certifications": _operation_certifications.duplicate(true),
		# ModMapData is per-map state, so the probe must be recomputed on every
		# status call (like map_id/dimensions/level_ids above), not captured
		# once in _certify_operation_routes()/start(). start() can run before
		# any map is open or against a different map than the one now open; a
		# reading frozen at mod-load would then falsely report the current
		# map's state for the rest of the process lifetime. The .NET side
		# passes unknown payload keys through unfiltered (see
		# src/DDAI.App/DdaiStatusService.cs:59, which clones response.Payload
		# verbatim rather than deserializing into a whitelisted DTO), so no
		# further plumbing is needed for this key to reach ddai_status.
		"map_data_probe": _current_map_data_probe(),
		"active_mods": [],
		"active_mods_available": false,
		"supported_commands": SUPPORTED_COMMANDS,
	}


func _active_mods_payload():
	return {"available": false, "values": []}


func _certified_operation_types():
	var values = _runtime_certified_operation_types.duplicate()
	values.sort()
	return values


func _certify_operation_routes():
	var script_path = Global.Root + "scripts/ddai_operation_certifier.gd" if str(Global.Root).length() > 0 else "res://ddai_operation_certifier.gd"
	var certifier_script = load(script_path)
	if certifier_script == null:
		return []
	var certifier = certifier_script.new()
	if certifier == null or not certifier.has_method("certify_runtime"):
		return []
	return certifier.certify_runtime(
		Global.Editor,
		Global.World,
		Global.WorldUI,
		_runtime_identity(),
		_certified_operation_types())


# Recomputed on every status call rather than cached, because ModMapData is
# per-map state: _certify_operation_routes() above runs once per mod load
# (from start()), which can happen before any map is open or against a
# different map than the one now open. Caching the probe there would freeze
# a stale or pre-map reading for the entire process lifetime.
func _current_map_data_probe():
	var script_path = Global.Root + "scripts/ddai_operation_certifier.gd" if str(Global.Root).length() > 0 else "res://ddai_operation_certifier.gd"
	var certifier_script = load(script_path)
	if certifier_script == null:
		return {}
	var certifier = certifier_script.new()
	if certifier == null or not certifier.has_method("probe_map_data"):
		return {}
	return certifier.probe_map_data(Global)


func _runtime_identity():
	var executable_path = OS.get_executable_path()
	if typeof(executable_path) != TYPE_STRING or executable_path.length() == 0:
		return {"exact_dungeondraft_version": "unknown", "executable_sha256": ""}
	var executable_sha256 = File.new().get_sha256(executable_path).to_lower()
	if executable_sha256 != TARGET_DUNGEONDRAFT_EXECUTABLE_SHA256:
		return {"exact_dungeondraft_version": "unknown", "executable_sha256": executable_sha256}
	return {
		"exact_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"executable_sha256": executable_sha256,
	}


func _write_runtime_receipt():
	var receipt = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"event": "started",
		"mod_version": MOD_VERSION,
		"target_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"timestamp": _iso_timestamp(),
		"session_id": _session_id,
		"supported_commands": SUPPORTED_COMMANDS,
		"operation_certifications": _operation_certifications.duplicate(true),
	}
	_write_json_atomically(MAILBOX_ROOT + "/runtime-receipts/" + _session_id + ".json", receipt)
	var slot_path = RUNTIME_RECEIPT_SLOT_PATHS[_select_runtime_receipt_slot()]
	var slot_result = _replace_json_recoverably(slot_path, receipt)
	if slot_result != "replaced":
		return slot_result
	_replace_json_recoverably("user://ddai/runtime-receipt.json", receipt)


func _write_heartbeat():
	var heartbeat_path = MAILBOX_ROOT + "/runtime-heartbeats/heartbeat-slot-" + str(_heartbeat_slot) + ".json"
	_heartbeat_slot = (_heartbeat_slot + 1) % 8
	var heartbeat_remove_result = _remove_file(heartbeat_path)
	if heartbeat_remove_result != "removed" and heartbeat_remove_result != "missing":
		return
	_write_json_atomically(heartbeat_path, {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"session_id": _session_id,
		"mod_version": MOD_VERSION,
		"timestamp": _iso_timestamp(),
	})


func _write_failed_record(claim, error):
	var safe_name = claim.file_name.get_basename()
	var record = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"timestamp": _iso_timestamp(),
		"success": false,
		"source_file": claim.file_name,
		"error": error,
	}
	return _write_json_atomically(_unique_failed_path(safe_name, error.code), record)


func _write_json_atomically(destination_path, payload):
	return _write_text_atomically(destination_path, to_json(payload))


func _replace_json_recoverably(destination_path, payload):
	var directory = Directory.new()
	directory.make_dir_recursive(destination_path.get_base_dir())
	var temporary_path = destination_path + "." + str(OS.get_ticks_msec()) + ".next"
	directory.remove(temporary_path)
	var file = File.new()
	if file.open(temporary_path, File.WRITE) != OK:
		return "write_failed"
	file.store_string(to_json(payload))
	file.flush()
	file.close()
	if directory.rename(temporary_path, destination_path) == OK:
		return "replaced"
	directory.remove(temporary_path)
	return "write_failed"


func _select_runtime_receipt_slot():
	var receipts = [null, null]
	for index in range(RUNTIME_RECEIPT_SLOT_PATHS.size()):
		var path = RUNTIME_RECEIPT_SLOT_PATHS[index]
		var directory = Directory.new()
		if not directory.file_exists(path):
			return index
		var read_result = _read_bounded_text(path)
		if not read_result.ok:
			return index
		var parsed = JSON.parse(read_result.text)
		if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
			return index
		if typeof(parsed.result.get("session_id", null)) != TYPE_STRING or typeof(parsed.result.get("timestamp", null)) != TYPE_STRING:
			return index
		receipts[index] = parsed.result
	return 1 if _runtime_receipt_is_newer(receipts[0], receipts[1]) else 0


func _runtime_receipt_is_newer(candidate, current):
	var candidate_timestamp = str(candidate.get("timestamp", ""))
	var current_timestamp = str(current.get("timestamp", ""))
	if candidate_timestamp != current_timestamp:
		return candidate_timestamp > current_timestamp
	var candidate_parts = _runtime_session_sequence(str(candidate.get("session_id", "")))
	var current_parts = _runtime_session_sequence(str(current.get("session_id", "")))
	if candidate_parts[0] != current_parts[0]:
		return candidate_parts[0] > current_parts[0]
	if candidate_parts[1] != current_parts[1]:
		return candidate_parts[1] > current_parts[1]
	return str(candidate.get("session_id", "")) > str(current.get("session_id", ""))


func _runtime_session_sequence(value):
	var parts = value.split("-", false)
	if parts.size() != 2 or not str(parts[0]).is_valid_integer() or not str(parts[1]).is_valid_integer():
		return [-1, -1]
	return [int(parts[0]), int(parts[1])]


func _write_text_atomically(destination_path, text):
	var directory = Directory.new()
	directory.make_dir_recursive(destination_path.get_base_dir())
	if directory.file_exists(destination_path):
		return "existing"
	var temporary_path = destination_path + "." + str(OS.get_ticks_msec()) + ".tmp"
	var file = File.new()
	if file.open(temporary_path, File.WRITE) != OK:
		return "write_failed"
	file.store_string(text)
	file.flush()
	file.close()
	if directory.rename(temporary_path, destination_path) == OK:
		return "created"
	directory.remove(temporary_path)
	return "write_failed"


func _move_to_unique_failed(source_path, safe_name, reason):
	var directory = Directory.new()
	if not directory.file_exists(source_path):
		return "missing"
	var destination = _unique_failed_path(safe_name, reason)
	if directory.rename(source_path, destination) == OK:
		return "moved"
	return "move_failed"


func _unique_failed_path(safe_name, reason):
	var directory = Directory.new()
	var suffix = str(OS.get_unix_time()) + "-" + str(OS.get_ticks_msec())
	var candidate = MAILBOX_ROOT + "/failed/" + safe_name + "." + reason + "." + suffix + ".json"
	var collision = 0
	while directory.file_exists(candidate):
		collision += 1
		candidate = MAILBOX_ROOT + "/failed/" + safe_name + "." + reason + "." + suffix + "-" + str(collision) + ".json"
	return candidate


func _remove_file(path):
	var directory = Directory.new()
	if not directory.file_exists(path):
		return "missing"
	if directory.remove(path) == OK:
		return "removed"
	return "remove_failed"


func _error(code, message, path):
	return {"code": code, "message": message, "path": path}


func _iso_timestamp():
	var current = OS.get_datetime(true)
	return "%04d-%02d-%02dT%02d:%02d:%02dZ" % [
		current.year,
		current.month,
		current.day,
		current.hour,
		current.minute,
		current.second,
	]
