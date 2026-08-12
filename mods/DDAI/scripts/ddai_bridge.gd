var script_class = "tool"

const MOD_VERSION = "0.2.1"
const TARGET_DUNGEONDRAFT_VERSION = "1.2.0.1"
const MAILBOX_SCHEMA_VERSION = "1.0"
const MAILBOX_ROOT = "user://ddai"
const MAXIMUM_MESSAGE_BYTES = 1048576
const POLL_INTERVAL_SECONDS = 0.25
const HEARTBEAT_INTERVAL_SECONDS = 10.0
const SUPPORTED_COMMANDS = ["status", "apply_plan", "inspect_map"]
const MAXIMUM_INSPECTION_LIMIT = 500
const MAXIMUM_INSPECTION_CURSOR_OFFSET = 10000
const MAXIMUM_INSPECTION_STATE_ITEMS = 10000
const MAXIMUM_INSPECTION_LEVELS = 128
const MAXIMUM_INSPECTION_LABEL_LENGTH = 256
const MAXIMUM_UNIVERSAL_OPERATIONS = 500
const MAXIMUM_UNIVERSAL_POINTS = 10000
const MINIMUM_UNIVERSAL_NONZERO_MAGNITUDE = 1e-300
const MAXIMUM_CATALOG_CHUNKS = 4096
const MAXIMUM_CATALOG_ENTRIES = 100000
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
var _certified_operation_executors = {}
var _asset_list_provider = null
var _texture_loader = null


# Called by Dungeondraft after the mod is loaded.
func start():
	_ensure_mailbox_directories()
	_session_id = str(OS.get_unix_time()) + "-" + str(OS.get_ticks_msec())
	_certified_operation_executors = {
		"wall_polyline": funcref(self, "_execute_wall_polyline"),
	}
	if _asset_list_provider == null:
		_asset_list_provider = funcref(self, "_get_live_asset_list")
	if _texture_loader == null:
		_texture_loader = funcref(self, "_load_live_texture")
	_recover_processing_claims()
	_write_runtime_receipt()
	_write_heartbeat()


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
	for name in ["requests", "processing", "responses", "failed", "journal", "mutation-intents", "map-jobs", "map-completed", "runtime-receipts", "runtime-heartbeats"]:
		directory.make_dir_recursive(MAILBOX_ROOT + "/" + name)


func _process_one_request():
	var claim = _claim_next_processing()
	if not claim.has("path"):
		claim = _claim_next_request()
	if not claim.has("path"):
		return
	_run_claim_state_machine(claim)


func _run_claim_state_machine(claim):
	# Each iteration crosses at most one durable boundary. Re-entry after any
	# boundary follows the same route during ordinary polling and startup recovery.
	for _step in range(8):
		var transition = _advance_claim_state(claim)
		if str(transition).begins_with("map_job_"):
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
		var operation_result = _execute_operation(plan.operations[int(job.next_operation_index)], key)
		_pending_operation_results[key] = operation_result
		return "map_job_native_operation_called"
	if job.state == "operation_applied":
		if _pending_observation_results.has(key):
			var observed = _pending_observation_results[key]
			_pending_observation_results.erase(key)
			if observed:
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
		_pending_observation_results[key] = _observe_operation(job.current_operation_node_ids)
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
			if _observe_reversal(pending_reversal.node_ids):
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
		if not _reverse_operation(plan.operations[int(job.reversal_operation_index)], job.current_operation_node_ids):
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
		if typeof(operation) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(operation, ["operation_type", "operation_id", "level_id", "asset_ref", "path", "closed", "color_rgba"]):
			return {"ok": false, "error": _error("malformed_operation", "Wall operation fields are missing or unsupported.", operation_path)}
		if operation.operation_type != "wall_polyline" or not _certified_operation_executors.has(operation.operation_type):
			return {"ok": false, "error": _error("unsupported_operation", "The operation type is not certified by this runtime.", operation_path + ".operation_type")}
		if typeof(operation.operation_id) != TYPE_STRING or not _is_safe_request_id(operation.operation_id) or operation_ids.has(operation.operation_id):
			return {"ok": false, "error": _error("invalid_operation_id", "operation_id must be safe and unique.", operation_path + ".operation_id")}
		operation_ids[operation.operation_id] = true
		if typeof(operation.level_id) != TYPE_STRING or not _is_safe_request_id(operation.level_id):
			return {"ok": false, "error": _error("invalid_level_id", "level_id must be safe.", operation_path + ".level_id")}
		if typeof(operation.asset_ref) != TYPE_STRING or operation.asset_ref.length() != 71 or not operation.asset_ref.begins_with("sha256:") or not _is_sha256(operation.asset_ref.substr(7, 64)):
			return {"ok": false, "error": _error("invalid_asset_ref", "asset_ref must be a canonical opaque SHA-256 reference.", operation_path + ".asset_ref")}
		if typeof(operation.path) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(operation.path, ["points"]) or typeof(operation.path.points) != TYPE_ARRAY:
			return {"ok": false, "error": _error("invalid_path", "Wall path must contain a points array.", operation_path + ".path")}
		var points = operation.path.points
		if points.size() < 2 or points.size() > MAXIMUM_UNIVERSAL_POINTS or total_points > MAXIMUM_UNIVERSAL_POINTS - points.size():
			return {"ok": false, "error": _error("invalid_path", "Wall path point count is outside the bounded range.", operation_path + ".path.points")}
		total_points += points.size()
		for point_index in range(points.size()):
			var point = points[point_index]
			if typeof(point) != TYPE_DICTIONARY or not _dictionary_has_exact_keys(point, ["x", "y"]) or not _is_bounded_grid_point(point, plan.canvas):
				return {"ok": false, "error": _error("invalid_point", "Wall points must be finite and inside the canvas.", operation_path + ".path.points[" + str(point_index) + "]")}
		if typeof(operation.closed) != TYPE_BOOL or typeof(operation.color_rgba) != TYPE_STRING or not _is_rgba(operation.color_rgba):
			return {"ok": false, "error": _error("invalid_wall_style", "Wall closed and color values are invalid.", operation_path)}
	return {"ok": true, "plan": plan}


func _preflight_universal_plan(plan):
	if Global.World == null or not _is_runtime_positive_int32(Global.World.Width) or not _is_runtime_positive_int32(Global.World.Height) or not _is_runtime_positive_finite_number(Global.World.GridSize):
		return {"ok": false, "error": _error("map_not_available", "No usable Dungeondraft map is available.", "")}
	if _current_map_id() != plan.expected_map_id:
		return {"ok": false, "error": _error("map_id_mismatch", "The open map identity changed.", "payload.expected_map_id")}
	var current_state_fingerprint = _capture_map_job_state_fingerprint()
	if current_state_fingerprint == "" or _map_job_state_fingerprint == "" or current_state_fingerprint != _map_job_state_fingerprint:
		return {"ok": false, "error": _error("map_revision_mismatch", "The represented map state changed after capability discovery.", "payload.base_revision")}
	if _map_job_revision != int(plan.base_revision):
		return {"ok": false, "error": _error("map_revision_mismatch", "The open map revision changed.", "payload.base_revision")}
	if int(Global.World.Width) != int(plan.canvas.width) or int(Global.World.Height) != int(plan.canvas.height):
		return {"ok": false, "error": _error("canvas_mismatch", "The open canvas does not match the plan.", "payload.canvas")}
	if Global.Editor == null or typeof(Global.Editor.Tools) != TYPE_DICTIONARY or not Global.Editor.Tools.has("WallTool") or Global.Editor.Tools["WallTool"] == null or Global.WorldUI == null:
		return {"ok": false, "error": _error("wall_tool_unavailable", "The documented wall editing state is unavailable.", "")}
	var wall_tool = Global.Editor.Tools["WallTool"]
	if wall_tool.isDrawing or Global.WorldUI.EditArcPoint or Global.WorldUI.Polyline.size() > 0:
		return {"ok": false, "error": _error("wall_tool_busy", "Finish or cancel the current manual wall before applying an AI plan.", "")}
	var catalog = _read_accepted_catalog()
	if not catalog.ok:
		return catalog
	if int(catalog.catalog_revision) != int(plan.expected_catalog_revision):
		return {"ok": false, "error": _error("catalog_revision_mismatch", "The accepted asset catalog changed.", "payload.expected_catalog_revision")}
	var resolved_assets = {}
	var resolved_references = {}
	for operation in plan.operations:
		if not _certified_operation_executors.has(operation.operation_type):
			return {"ok": false, "error": _error("unsupported_operation", "The operation executor is not certified.", "payload.operations")}
		if not _current_level_ids().has(operation.level_id) or Global.World.GetLevelByID(int(operation.level_id)) == null:
			return {"ok": false, "error": _error("unsupported_level", "The requested level is unavailable.", "payload.operations.level_id")}
		if not catalog.entries.has(operation.asset_ref):
			return {"ok": false, "error": _error("asset_not_found", "The opaque asset reference is not in the accepted catalog.", "payload.operations.asset_ref")}
		var entry = catalog.entries[operation.asset_ref]
		if entry.category != "Walls" or not _is_sha256(entry.resource_fingerprint):
			return {"ok": false, "error": _error("asset_category_mismatch", "The asset is not a certified wall asset.", "payload.operations.asset_ref")}
		if not resolved_references.has(operation.asset_ref):
			var live_asset = _resolve_live_asset("Walls", entry.resource_fingerprint)
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
		var manifest = _read_bounded_dictionary(MAILBOX_ROOT + "/catalog/snapshots/" + pointer.manifest)
		if manifest == null or manifest.get("session_id", "") != _session_id or not manifest.get("complete", false) or not _is_nonnegative_json_safe_integer(manifest.get("catalog_revision", null)) or int(manifest.catalog_revision) != expected_revision or not _is_sha256(manifest.get("catalog_fingerprint", "")):
			continue
		if pointer.has("catalog_revision") and (not _is_nonnegative_json_safe_integer(pointer.catalog_revision) or int(pointer.catalog_revision) != expected_revision):
			continue
		if matched_fingerprint != null and matched_fingerprint != manifest.catalog_fingerprint:
			return {"ok": false}
		matched_fingerprint = manifest.catalog_fingerprint
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


func _execute_operation(operation, job_key):
	if typeof(operation) != TYPE_DICTIONARY or not _certified_operation_executors.has(operation.operation_type):
		return {"ok": false, "node_ids": []}
	return _certified_operation_executors[operation.operation_type].call_func(operation, job_key)


func _execute_wall_polyline(operation, job_key):
	if not _resolved_job_assets.has(job_key) or not _resolved_job_assets[job_key].has(operation.operation_id):
		return {"ok": false, "node_ids": []}
	var texture = _texture_loader.call_func(_resolved_job_assets[job_key][operation.operation_id])
	var level = Global.World.GetLevelByID(int(operation.level_id))
	if texture == null or level == null or level.Walls == null:
		return {"ok": false, "node_ids": []}
	var before = {}
	for wall in level.Walls.get_children():
		var existing_node_id = wall.GetNodeID()
		if _is_runtime_nonnegative_safe_integer(existing_node_id):
			before[int(existing_node_id)] = true
	var points = []
	for point in operation.path.points:
		points.append(Vector2(float(point.x) * float(Global.World.GridSize), float(point.y) * float(Global.World.GridSize)))
	level.Walls.AddWall(points, texture, _rgba_color(operation.color_rgba), operation.closed)
	var created = []
	for wall in level.Walls.get_children():
		var raw_node_id = wall.GetNodeID()
		if _is_runtime_nonnegative_safe_integer(raw_node_id):
			var node_id = int(raw_node_id)
			if node_id > 0 and not before.has(node_id):
				created.append(node_id)
	if created.size() != 1:
		return {"ok": false, "node_ids": created}
	return {"ok": true, "node_ids": created}


func _observe_operation(native_node_ids):
	if typeof(native_node_ids) != TYPE_ARRAY or native_node_ids.size() == 0:
		return false
	for node_id in native_node_ids:
		if not Global.World.HasNodeID(int(node_id)) or Global.World.GetNodeByID(int(node_id)) == null:
			return false
	return true


func _reverse_operation(operation, native_node_ids):
	if operation.operation_type != "wall_polyline":
		return false
	return _reverse_wall_polyline(native_node_ids)


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


func _observe_reversal(native_node_ids):
	for node_id in native_node_ids:
		if Global.World.HasNodeID(int(node_id)) or Global.World.GetNodeByID(int(node_id)) != null:
			return false
	if typeof(Global.World.levels) != TYPE_ARRAY or Global.World.levels.size() > MAXIMUM_INSPECTION_LEVELS:
		return false
	var targets = {}
	for node_id in native_node_ids:
		targets[int(node_id)] = true
	for level in Global.World.levels:
		if level == null or level.Walls == null or level.Walls.get_child_count() > MAXIMUM_INSPECTION_STATE_ITEMS:
			return false
		for wall in level.Walls.get_children():
			var wall_id = wall.GetNodeID()
			if _is_runtime_nonnegative_safe_integer(wall_id) and targets.has(int(wall_id)):
				return false
	return true


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
	var manifest_path = identity.manifest_path
	var manifest = identity.manifest
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
		"catalog_revision": int(manifest.catalog_revision),
		"catalog_fingerprint": manifest.catalog_fingerprint,
		"entries": entries,
	}


func _catalog_identity_matches(job):
	var identity = _read_current_catalog_identity()
	return identity.ok and int(identity.catalog_revision) == int(job.catalog_revision) and identity.catalog_fingerprint == job.catalog_fingerprint


func _read_current_catalog_identity():
	var pointer = _read_bounded_dictionary(MAILBOX_ROOT + "/catalog/current.json")
	var pointer_shape_ok = _dictionary_has_exact_keys(pointer, ["session_id", "manifest"]) or _dictionary_has_exact_keys(pointer, ["session_id", "manifest", "catalog_revision"])
	if not pointer_shape_ok or pointer.session_id != _session_id or not _is_safe_relative_catalog_path(pointer.manifest):
		return {"ok": false}
	var manifest_path = MAILBOX_ROOT + "/catalog/snapshots/" + pointer.manifest
	var manifest = _read_bounded_dictionary(manifest_path)
	if not _catalog_manifest_is_valid(manifest) or manifest.session_id != _session_id:
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
		text += prefix + ".path.points_count=" + str(operation.path.points.size()) + "\n"
		for point_index in range(operation.path.points.size()):
			var point = operation.path.points[point_index]
			text += prefix + ".path.point[" + str(point_index) + "].x=" + _double_fingerprint_text(float(point.x)) + "\n"
			text += prefix + ".path.point[" + str(point_index) + "].y=" + _double_fingerprint_text(float(point.y)) + "\n"
		text += prefix + ".closed=" + ("true" if operation.closed else "false") + "\n"
		text += _framed_string(prefix + ".color_rgba=", operation.color_rgba)
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
	if Global.World == null:
		return ""
	return (_framed_string("session_id=", _session_id) + "world_instance_id=" + str(Global.World.get_instance_id()) + "\n").sha256_text()


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


func _cleanup_wall_tool(wall_tool):
	Global.WorldUI.ClearPolyline()
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
	directory.list_dir_begin(true, true)
	var file_name = directory.get_next()
	while file_name != "":
		if not directory.current_is_dir() and file_name.ends_with(".json"):
			directory.list_dir_end()
			return {"file_name": file_name, "path": MAILBOX_ROOT + "/processing/" + file_name}
		file_name = directory.get_next()
	directory.list_dir_end()
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
	var raw_node_id = node.GetNodeID()
	if not _is_runtime_nonnegative_safe_integer(raw_node_id):
		return {"ok": false, "error": _error("inspection_state_invalid", "A native inspection item has an invalid node ID.", "")}
	var node_id = int(raw_node_id)
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
	return rect.size.x > 0.0 and rect.size.y > 0.0


func _is_runtime_nonnegative_safe_integer(value):
	return (typeof(value) == TYPE_INT or typeof(value) == TYPE_REAL) and not is_nan(float(value)) and not is_inf(float(value)) and float(value) == floor(float(value)) and float(value) >= 0.0 and float(value) <= 9007199254740991.0


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
	return "map_job_journal_deleted" if _remove_file(job_path) in ["removed", "missing"] else "blocked"


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
		"certified_operation_types": ["wall_polyline"],
		"active_mods": [],
		"active_mods_available": false,
		"supported_commands": SUPPORTED_COMMANDS,
	}


func _active_mods_payload():
	return {"available": false, "values": []}


func _write_runtime_receipt():
	var receipt = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"event": "started",
		"mod_version": MOD_VERSION,
		"target_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"timestamp": _iso_timestamp(),
		"session_id": _session_id,
		"supported_commands": SUPPORTED_COMMANDS,
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
