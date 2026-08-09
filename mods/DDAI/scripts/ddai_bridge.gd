var script_class = "tool"

const MOD_VERSION = "0.1.0"
const TARGET_DUNGEONDRAFT_VERSION = "1.2.0.1"
const MAILBOX_SCHEMA_VERSION = "1.0"
const MAILBOX_ROOT = "user://ddai"
const MAXIMUM_MESSAGE_BYTES = 1048576
const POLL_INTERVAL_SECONDS = 0.25
const HEARTBEAT_INTERVAL_SECONDS = 10.0
const SUPPORTED_COMMANDS = ["status"]

var _poll_elapsed = POLL_INTERVAL_SECONDS
var _heartbeat_elapsed = HEARTBEAT_INTERVAL_SECONDS
var _session_id = ""
var _heartbeat_slot = 0


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
	for name in ["requests", "processing", "responses", "failed", "journal", "runtime-receipts", "runtime-heartbeats"]:
		directory.make_dir_recursive(MAILBOX_ROOT + "/" + name)


func _process_one_request():
	var claim = _claim_next_request()
	if claim.empty():
		return
	_run_claim_state_machine(claim)


func _run_claim_state_machine(claim):
	# Each iteration crosses at most one durable boundary. Re-entry after any
	# boundary follows the same route during ordinary polling and startup recovery.
	for _step in range(5):
		var transition = _advance_claim_state(claim)
		if transition == "blocked" or transition == "no_work" or transition == "journal_deleted" or transition == "invalid_claim_failed":
			return transition
	return "bounded_transition_limit"


func _advance_claim_state(claim):
	var directory = Directory.new()
	var key = claim.file_name.get_basename()
	var journal_path = MAILBOX_ROOT + "/journal/" + claim.file_name
	var response_path = MAILBOX_ROOT + "/responses/" + claim.file_name
	if not directory.file_exists(claim.path):
		return _cleanup_stale_journal(claim.file_name)

	var read_result = _read_bounded_text(claim.path)
	if not read_result.ok:
		return _fail_claim_without_loss(claim, read_result.error)
	var parsed = JSON.parse(read_result.text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		return _fail_claim_without_loss(claim, _error("malformed_request", "Request JSON must contain an object envelope.", ""))
	var request = parsed.result
	var validation_error = _validate_request(request, claim.file_name)
	if not validation_error.empty():
		return _fail_claim_without_loss(claim, validation_error)

	var canonical_request_text = _canonical_request_text(request)
	var reconciliation_result = _reconcile_request_duplicate(claim.file_name, canonical_request_text)
	if reconciliation_result != "reconciled":
		return "blocked"
	var request_fingerprint = canonical_request_text.sha256_text()
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
	var zone_index = value.rfind("+")
	if zone_index < 19:
		zone_index = value.rfind("-")
	var main = value
	var zone_kind = "local"
	var zone_sign = 0
	var zone_hour_value = 0
	var zone_minute_value = 0
	if value.ends_with("Z"):
		zone_kind = "utc"
		main = value.substr(0, value.length() - 1)
	elif zone_index >= 19 and value.length() - zone_index == 6 and value[zone_index + 3] == ":":
		var zone_hour = value.substr(zone_index + 1, 2)
		var zone_minute = value.substr(zone_index + 4, 2)
		if not zone_hour.is_valid_integer() or not zone_minute.is_valid_integer():
			return false
		zone_kind = "explicit"
		zone_sign = 1 if value[zone_index] == "+" else -1
		zone_hour_value = int(zone_hour)
		zone_minute_value = int(zone_minute)
		if zone_hour_value > 14 or zone_minute_value > 59 or (zone_hour_value == 14 and zone_minute_value != 0):
			return false
		if zone_hour_value == 0 and zone_minute_value == 0:
			zone_sign = 0
		main = value.substr(0, zone_index)
	var decimal_index = main.find(".")
	var fraction_nonzero = false
	if decimal_index != -1:
		var fraction = main.substr(decimal_index + 1, main.length() - decimal_index - 1)
		if fraction == "" or fraction.length() > 16 or not fraction.is_valid_integer():
			return false
		# DateTimeOffset precision is 100 ns; System.Text.Json accepts additional
		# digits but they do not move the represented value past the first 7.
		fraction_nonzero = int(fraction.substr(0, min(7, fraction.length()))) != 0
		main = main.substr(0, decimal_index)
	if main.length() != 19 or main[4] != "-" or main[7] != "-" or main[10] != "T" or main[13] != ":" or main[16] != ":":
		return false
	var digits = main.substr(0, 4) + main.substr(5, 2) + main.substr(8, 2) + main.substr(11, 2) + main.substr(14, 2) + main.substr(17, 2)
	if not digits.is_valid_integer():
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
	# System.Text.Json accepts timezone-less values using the local offset. Keep
	# that canonical behavior; only explicit offsets need deterministic UTC bounds.
	if zone_kind == "local":
		return true
	var local_seconds = hour * 3600 + minute * 60 + second
	var offset_seconds = zone_hour_value * 3600 + zone_minute_value * 60
	var minimum_date = year == 1 and month == 1 and day == 1
	if minimum_date:
		if zone_kind == "utc" and local_seconds == 0 and not fraction_nonzero:
			return false
		if zone_kind == "explicit" and zone_sign >= 0:
			if local_seconds < offset_seconds:
				return false
			if local_seconds == offset_seconds and not fraction_nonzero:
				return false
	var maximum_date = year == 9999 and month == 12 and day == 31
	if maximum_date and zone_kind == "explicit" and zone_sign < 0 and local_seconds + offset_seconds >= 86400:
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
			if _validate_request(duplicate, file_name).empty() and _canonical_request_text(duplicate) == canonical_processing_text:
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
		return {
			"schema_version": MAILBOX_SCHEMA_VERSION,
			"request_id": request.request_id,
			"command": request.command,
			"timestamp": _iso_timestamp(),
			"success": true,
			"payload": _status_payload(),
		}
	return {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request.request_id,
		"command": request.command,
		"timestamp": _iso_timestamp(),
		"success": false,
		"payload": {},
		"error": _error("unsupported_command", "This mod currently supports only the status command.", "command"),
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
	var world = _safe_property(Global, "World")
	var levels = _safe_property(world, "levels")
	var map_loaded = typeof(levels) == TYPE_ARRAY and levels.size() > 0
	var dimensions = _vector2_payload(_safe_property(world, "WoxelDimensions"))
	var runtime_version = _first_safe_property(Global, ["Version", "version", "DungeondraftVersion"])
	var active_mods = _active_mods_payload()
	return {
		"mod_version": MOD_VERSION,
		"dungeondraft_version": runtime_version,
		"dungeondraft_version_available": runtime_version != null,
		"target_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"map_loaded": map_loaded,
		"current_level": _first_safe_property(world, ["current_level", "CurrentLevel"]),
		"dimensions": dimensions,
		"revision": _first_safe_property(world, ["revision", "Revision", "map_revision"]),
		"active_mods": active_mods.values,
		"active_mods_available": active_mods.available,
		"supported_commands": SUPPORTED_COMMANDS,
	}


func _active_mods_payload():
	var mods = _first_safe_property(Global, ["ActiveMods", "active_mods", "Mods"])
	if typeof(mods) == TYPE_ARRAY:
		return {"available": true, "values": mods}
	if typeof(mods) == TYPE_DICTIONARY:
		return {"available": true, "values": mods.keys()}
	return {"available": false, "values": []}


func _vector2_payload(value):
	if value is Vector2:
		return {"width": value.x, "height": value.y}
	return null


func _first_safe_property(target, names):
	for property_name in names:
		var value = _safe_property(target, property_name)
		if value != null:
			return value
	return null


func _safe_property(target, property_name):
	if target == null:
		return null
	for property_info in target.get_property_list():
		if property_info.name == property_name:
			return target.get(property_name)
	return null


func _write_runtime_receipt():
	_write_json_atomically(MAILBOX_ROOT + "/runtime-receipts/" + _session_id + ".json", {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"event": "started",
		"mod_version": MOD_VERSION,
		"target_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"timestamp": _iso_timestamp(),
		"session_id": _session_id,
		"supported_commands": SUPPORTED_COMMANDS,
	})


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
