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
const MAXIMUM_INSPECTION_CURSOR_OFFSET = 100000
const RUNTIME_RECEIPT_SLOT_PATHS = ["user://ddai/runtime-receipt-slot-0.json", "user://ddai/runtime-receipt-slot-1.json"]

var _poll_elapsed = POLL_INTERVAL_SECONDS
var _heartbeat_elapsed = HEARTBEAT_INTERVAL_SECONDS
var _session_id = ""
var _heartbeat_slot = 0
var _prepared_mutation_keys = {}
var _mutation_active = false


# Called by Dungeondraft after the mod is loaded.
func start():
	_ensure_mailbox_directories()
	_session_id = str(OS.get_unix_time()) + "-" + str(OS.get_ticks_msec())
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
	for name in ["requests", "processing", "responses", "failed", "journal", "mutation-intents", "runtime-receipts", "runtime-heartbeats"]:
		directory.make_dir_recursive(MAILBOX_ROOT + "/" + name)


func _process_one_request():
	var claim = _claim_next_request()
	if not claim.has("path"):
		return
	_run_claim_state_machine(claim)


func _run_claim_state_machine(claim):
	# Each iteration crosses at most one durable boundary. Re-entry after any
	# boundary follows the same route during ordinary polling and startup recovery.
	for _step in range(8):
		var transition = _advance_claim_state(claim)
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
	if Global.World.Width == null or Global.World.Height == null or int(Global.World.Width) <= 0 or int(Global.World.Height) <= 0:
		return {"ok": false, "error": _error("map_not_available", "The current map canvas is unavailable.", "")}
	if Global.World.GridSize == null or float(Global.World.GridSize) <= 0.0 or is_nan(float(Global.World.GridSize)) or is_inf(float(Global.World.GridSize)):
		return {"ok": false, "error": _error("map_not_available", "The current map grid scale is unavailable.", "")}
	var current_level_id = int(Global.World.CurrentLevelId)
	if payload.level != null and int(payload.level) != current_level_id:
		return {"ok": false, "error": _error("unsupported_level", "Only the documented current level can be inspected.", "payload.level")}
	var level = Global.World.GetLevelByID(current_level_id)
	if level == null:
		return {"ok": false, "error": _error("active_level_unavailable", "The current map level is unavailable.", "")}
	if level.Walls == null or level.Pathways == null or level.Roofs == null or level.PatternShapes == null:
		return {"ok": false, "error": _error("active_level_unavailable", "A documented current-level inspection container is unavailable.", "")}

	var region_result = _inspection_region(payload.region, int(Global.World.Width), int(Global.World.Height))
	if not region_result.ok:
		return region_result
	var map_revision = int(Global.World.nextNodeID)
	if map_revision < 0 or map_revision > 9007199254740991:
		return {"ok": false, "error": _error("map_not_available", "The current map revision is unavailable.", "")}
	var map_id_input = _framed_string("session_id=", _session_id)
	map_id_input += _framed_string("title=", str(Global.World.Title))
	map_id_input += "width=" + str(int(Global.World.Width)) + "\n"
	map_id_input += "height=" + str(int(Global.World.Height)) + "\n"
	var map_id = map_id_input.sha256_text()
	var cursor_fingerprint = _inspection_cursor_fingerprint(map_id, map_revision, current_level_id, payload.region)
	var offset_result = _inspection_cursor_offset(payload.cursor, cursor_fingerprint)
	if not offset_result.ok:
		return offset_result

	var state = {
		"items": [],
		"seen": 0,
		"offset": offset_result.offset,
		"limit": int(payload.limit),
		"has_more": false,
	}
	var grid_size = float(Global.World.GridSize)
	# This is a fixed allowlist of documented public containers on the current
	# Level. It deliberately does not reflect over or recursively crawl the scene.
	_append_inspection_container(level.Walls, "wall", state, current_level_id, grid_size, region_result.rect)
	if not state.has_more:
		_append_inspection_container(level.Pathways, "path", state, current_level_id, grid_size, region_result.rect)
	if not state.has_more:
		_append_inspection_container(level.Roofs, "roof", state, current_level_id, grid_size, region_result.rect)
	if not state.has_more:
		_append_inspection_container(level.PatternShapes, "pattern_shape", state, current_level_id, grid_size, region_result.rect)
	var next_cursor = null
	if state.has_more:
		var next_offset = state.offset + state.items.size()
		if next_offset > MAXIMUM_INSPECTION_CURSOR_OFFSET:
			return {"ok": false, "error": _error("invalid_cursor", "The inspection cursor exceeds its bounded offset.", "payload.cursor")}
		next_cursor = cursor_fingerprint + ":" + str(next_offset)
	var label = str(level.Label).strip_edges()
	if label == "":
		label = "Level " + str(current_level_id)
	return {"ok": true, "payload": {
		"map_id": map_id,
		"map_revision": map_revision,
		"canvas": {"width": int(Global.World.Width), "height": int(Global.World.Height)},
		"grid_size": grid_size,
		"levels": [{"id": current_level_id, "label": label, "current": true}],
		"items": state.items,
		"next_cursor": next_cursor,
		"truncated": state.has_more,
		"unsupported_kinds": ["object", "portal", "light", "text", "material", "floor_shape"],
	}}


func _append_inspection_container(container, kind, state, level_id, grid_size, region_rect):
	for node in container.get_children():
		var item = _inspection_item(node, kind, level_id, grid_size)
		if item == null or not _inspection_bounds_intersect(item.bounds, region_rect):
			continue
		if state.seen < state.offset:
			state.seen += 1
			continue
		if state.items.size() >= state.limit:
			state.has_more = true
			return
		state.items.append(item)
		state.seen += 1


func _inspection_item(node, kind, level_id, grid_size):
	if node == null:
		return null
	var global_rect = null
	if kind == "wall":
		global_rect = node.GlobalRect
	elif kind == "path":
		global_rect = node.GlobalRect
	elif kind == "roof":
		global_rect = node.GlobalRect
	elif kind == "pattern_shape":
		global_rect = node.GlobalRect
	else:
		return null
	if typeof(global_rect) != TYPE_RECT2 or global_rect.size.x <= 0.0 or global_rect.size.y <= 0.0:
		return null
	var node_id = int(node.GetNodeID())
	if node_id < 0 or node_id > 9007199254740991:
		return null
	return {
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
	}


func _inspection_region(region, canvas_width, canvas_height):
	if region == null:
		return {"ok": true, "rect": null}
	if typeof(region) != TYPE_DICTIONARY or region.size() != 4:
		return {"ok": false, "error": _error("invalid_region", "Inspection region must contain exactly x, y, width, and height.", "payload.region")}
	for key in ["x", "y", "width", "height"]:
		if not region.has(key) or typeof(region[key]) != TYPE_REAL or is_nan(region[key]) or is_inf(region[key]):
			return {"ok": false, "error": _error("invalid_region", "Inspection region values must be finite numbers.", "payload.region." + key)}
	if region.x < 0.0 or region.y < 0.0 or region.width <= 0.0 or region.height <= 0.0:
		return {"ok": false, "error": _error("invalid_region", "Inspection region must be non-negative and have positive size.", "payload.region")}
	if region.x > canvas_width or region.y > canvas_height or region.width > canvas_width - region.x or region.height > canvas_height - region.y:
		return {"ok": false, "error": _error("invalid_region", "Inspection region must fit within the current map canvas.", "payload.region")}
	return {"ok": true, "rect": Rect2(region.x, region.y, region.width, region.height)}


func _inspection_bounds_intersect(bounds, region_rect):
	if region_rect == null:
		return true
	return bounds.x < region_rect.position.x + region_rect.size.x and bounds.x + bounds.width > region_rect.position.x and bounds.y < region_rect.position.y + region_rect.size.y and bounds.y + bounds.height > region_rect.position.y


func _inspection_cursor_fingerprint(map_id, map_revision, level_id, region):
	var text = _framed_string("map_id=", map_id)
	text += "map_revision=" + str(map_revision) + "\n"
	text += "level=" + str(level_id) + "\n"
	if region == null:
		text += "region=null\n"
	else:
		text += "region_x=" + str(region.x) + "\n"
		text += "region_y=" + str(region.y) + "\n"
		text += "region_width=" + str(region.width) + "\n"
		text += "region_height=" + str(region.height) + "\n"
	return text.sha256_text()


func _inspection_cursor_offset(cursor, expected_fingerprint):
	if cursor == null:
		return {"ok": true, "offset": 0}
	if typeof(cursor) != TYPE_STRING or cursor.length() < 66 or cursor.length() > 71 or cursor.substr(64, 1) != ":":
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor is malformed.", "payload.cursor")}
	var fingerprint = cursor.substr(0, 64)
	var digits = cursor.substr(65, cursor.length() - 65)
	if not _is_sha256(fingerprint) or fingerprint != expected_fingerprint or not _is_ascii_decimal_digits(digits) or (digits.length() > 1 and digits.begins_with("0")):
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor does not match this map, revision, level, and region.", "payload.cursor")}
	var offset = int(digits)
	if offset < 0 or offset > MAXIMUM_INSPECTION_CURSOR_OFFSET:
		return {"ok": false, "error": _error("invalid_cursor", "Inspection cursor offset is outside the bounded range.", "payload.cursor")}
	return {"ok": true, "offset": offset}


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
	return {
		"mod_version": MOD_VERSION,
		"dungeondraft_version": null,
		"dungeondraft_version_available": false,
		"target_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"map_loaded": true,
		"current_level": null,
		"dimensions": null,
		"revision": null,
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
