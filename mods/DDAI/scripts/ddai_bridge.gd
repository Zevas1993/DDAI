var script_class = "tool"

const MOD_VERSION = "0.1.0"
const TARGET_DUNGEONDRAFT_VERSION = "1.2.0.1"
const MAILBOX_SCHEMA_VERSION = "1.0"
const MAILBOX_ROOT = "user://ddai"
const MAXIMUM_MESSAGE_BYTES = 1048576
const POLL_INTERVAL_SECONDS = 0.25
const SUPPORTED_COMMANDS = ["status"]

var _poll_elapsed = POLL_INTERVAL_SECONDS


# Called by Dungeondraft after the mod is loaded.
func start():
	_ensure_mailbox_directories()
	_write_runtime_receipt()


# Called by Dungeondraft every frame. This is a filesystem poller, never a network listener.
func update(delta):
	_poll_elapsed += delta
	if _poll_elapsed < POLL_INTERVAL_SECONDS:
		return
	_poll_elapsed = 0.0
	_process_one_request()


func _ensure_mailbox_directories():
	var directory = Directory.new()
	for name in ["requests", "processing", "responses", "failed", "journal"]:
		directory.make_dir_recursive(MAILBOX_ROOT + "/" + name)


func _process_one_request():
	var claim = _claim_next_request()
	if claim.empty():
		return

	var read_result = _read_bounded_text(claim.path)
	if not read_result.ok:
		_write_failed_record(claim, read_result.error)
		_remove_file(claim.path)
		return

	var parsed = JSON.parse(read_result.text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		_write_failed_record(claim, _error("malformed_request", "Request JSON must contain an object envelope.", ""))
		_remove_file(claim.path)
		return

	var request = parsed.result
	var validation_error = _validate_request(request, claim.file_name)
	if not validation_error.empty():
		if _can_correlate_failure(request):
			_write_response(request.request_id, request.command, false, {}, validation_error)
		else:
			_write_failed_record(claim, validation_error)
		_remove_file(claim.path)
		return

	if request.command == "status":
		_write_response(request.request_id, request.command, true, _status_payload(), {})
	else:
		_write_response(
			request.request_id,
			request.command,
			false,
			{},
			_error("unsupported_command", "This mod currently supports only the status command.", "command"))
	_remove_file(claim.path)


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
	if not request.has("timestamp") or typeof(request.timestamp) != TYPE_STRING or request.timestamp.strip_edges() == "":
		return _error("malformed_request", "timestamp is required.", "timestamp")
	if not request.has("payload"):
		return _error("malformed_request", "payload is required.", "payload")
	return {}


func _can_correlate_failure(request):
	return request.has("request_id") and request.has("command") and \
		typeof(request.request_id) == TYPE_STRING and _is_safe_request_id(request.request_id) and \
		typeof(request.command) == TYPE_STRING and request.command.strip_edges() != ""


func _is_safe_request_id(request_id):
	return request_id.strip_edges() != "" and request_id.find("/") == -1 and request_id.find("\\") == -1 and request_id != "." and request_id != ".."


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
	_write_json_atomically(MAILBOX_ROOT + "/runtime-receipt.json", {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"event": "started",
		"mod_version": MOD_VERSION,
		"target_dungeondraft_version": TARGET_DUNGEONDRAFT_VERSION,
		"timestamp": _iso_timestamp(),
		"supported_commands": SUPPORTED_COMMANDS,
	})


func _write_response(request_id, command, success, payload, error):
	var response = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"request_id": request_id,
		"command": command,
		"timestamp": _iso_timestamp(),
		"success": success,
		"payload": payload,
	}
	if not success:
		response.error = error
	_write_json_atomically(MAILBOX_ROOT + "/responses/" + request_id.sha256_text() + ".json", response)


func _write_failed_record(claim, error):
	var safe_name = claim.file_name.get_basename()
	var record = {
		"schema_version": MAILBOX_SCHEMA_VERSION,
		"timestamp": _iso_timestamp(),
		"success": false,
		"source_file": claim.file_name,
		"error": error,
	}
	_write_json_atomically(MAILBOX_ROOT + "/failed/" + safe_name + "." + error.code + ".json", record)


func _write_json_atomically(destination_path, payload):
	var directory = Directory.new()
	directory.make_dir_recursive(destination_path.get_base_dir())
	if directory.file_exists(destination_path):
		return true
	var temporary_path = destination_path + "." + str(OS.get_ticks_msec()) + ".tmp"
	var file = File.new()
	if file.open(temporary_path, File.WRITE) != OK:
		return false
	file.store_string(to_json(payload))
	file.flush()
	file.close()
	if directory.rename(temporary_path, destination_path) == OK:
		return true
	directory.remove(temporary_path)
	return false


func _remove_file(path):
	var directory = Directory.new()
	if directory.file_exists(path):
		directory.remove(path)


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
