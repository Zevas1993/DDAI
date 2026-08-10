var script_class = "tool"

const CATALOG_SCHEMA_VERSION = "1.0"
const RUNTIME_RECEIPT_PATH = "user://ddai/runtime-receipt.json"
const CATALOG_ROOT = "user://ddai/catalog"
const PREVIEWS_ROOT = "user://ddai/catalog/previews"
const RUNTIME_RECEIPT_SLOT_PATHS = ["user://ddai/runtime-receipt-slot-0.json", "user://ddai/runtime-receipt-slot-1.json"]
const MANIFEST_FILE_NAME = "manifest.json"
const MAX_ASSETS_PER_TICK = 8
const MAX_PREVIEW_EDGE = 256
const MAX_PREVIEW_BYTES = 262144
const MAX_CHUNK_BYTES = 900000
const MAX_RECEIPT_BYTES = 65536
const MAX_MANIFEST_BYTES = 1048576
const MAX_ERROR_RECORDS = 128
const MAX_ERROR_CODE_BYTES = 64
const MAX_ERROR_MESSAGE_BYTES = 512
const MAX_NORMALIZATION_BYTES = 4096
const HELPER_RECEIPT_PATH = "user://ddai/private/asset-helper.json"
const HELPER_HASH_BYTES_PER_TICK = 65536
const HELPER_LAUNCH_INTERVAL_MSEC = 2000
const HELPER_MAX_LAUNCH_ATTEMPTS = 3
const HELPER_RESPONSE_DEADLINE_MSEC = 10000

const CATEGORIES = [
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

var catalog_root = CATALOG_ROOT
var _runtime_adapter = null
var _state = "waiting_for_receipt"
var _session_id = ""
var _catalog_revision = 0
var _snapshot_at = ""
var _category_index = 0
var _enumeration_loaded = false
var _enumeration_raw = []
var _enumeration_raw_index = 0
var _enumeration_sorted = []
var _enumeration_seen = {}
var _category_resources = []
var _category_counts = {}
var _asset_category_index = 0
var _asset_index = 0
var _entries = []
var _resource_lookup = {}
var _preview_results = {}
var _errors = []
var _suppressed_error_count = 0
var _preview_work = null
var _normalization_request_pending = null
var _normalization_active_request_id = ""
var _normalization_active_request_hash = ""
var _pack_normalization_values = {}
var _candidate_fingerprint = ""
var _candidate_root = ""
var _candidate_write_index = 0
var _commit_request_pending = null
var _commit_request_id = ""
var _commit_request_hash = ""
var _helper_verification_started = false
var _helper_ready = false
var _helper_launch_attempts = 0
var _helper_launch_started_at = 0
var _helper_last_launch_at = 0
var _chunk_build_index = 0
var _chunk_entry_json = []
var _chunk_payload_bytes = 2
var _pending_chunks = []
var _chunk_write_index = 0
var _chunk_receipts = []
var _chunk_phase = "building"
var _manifest_text = ""
var _last_update_entry_operations = 0
var _last_update_file_publications = 0


class LiveRuntimeAdapter:
	var _helper_executable_path = ""
	var _helper_file = null
	var _helper_hash = null
	var _helper_expected_hash = ""

	func read_runtime_receipt():
		var newest = null
		for path in [RUNTIME_RECEIPT_PATH] + RUNTIME_RECEIPT_SLOT_PATHS:
			var candidate = _read_receipt(path)
			if candidate != null and (newest == null or _receipt_is_newer(candidate, newest)):
				newest = candidate
		return {"ok": newest != null, "receipt": newest}

	func _receipt_is_newer(candidate, current):
		var candidate_timestamp = str(candidate.get("timestamp", ""))
		var current_timestamp = str(current.get("timestamp", ""))
		if candidate_timestamp != current_timestamp:
			return candidate_timestamp > current_timestamp
		var candidate_parts = _session_sequence(str(candidate.get("session_id", "")))
		var current_parts = _session_sequence(str(current.get("session_id", "")))
		if candidate_parts[0] != current_parts[0]:
			return candidate_parts[0] > current_parts[0]
		if candidate_parts[1] != current_parts[1]:
			return candidate_parts[1] > current_parts[1]
		return str(candidate.get("session_id", "")) > str(current.get("session_id", ""))

	func _session_sequence(value):
		var parts = value.split("-", false)
		if parts.size() != 2 or not str(parts[0]).is_valid_integer() or not str(parts[1]).is_valid_integer():
			return [-1, -1]
		return [int(parts[0]), int(parts[1])]

	func _read_receipt(path):
		var file = File.new()
		if not file.file_exists(path):
			return null
		if file.open(path, File.READ) != OK:
			return null
		if file.get_len() > MAX_RECEIPT_BYTES:
			file.close()
			return null
		var text = file.get_as_text()
		file.close()
		var parsed = JSON.parse(text)
		if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
			return null
		if typeof(parsed.result.get("session_id", null)) != TYPE_STRING or typeof(parsed.result.get("timestamp", null)) != TYPE_STRING:
			return null
		return parsed.result

	func begin_helper_verification():
		var receipt_file = File.new()
		if not receipt_file.file_exists(HELPER_RECEIPT_PATH) or receipt_file.open(HELPER_RECEIPT_PATH, File.READ) != OK:
			return false
		if receipt_file.get_len() > MAX_NORMALIZATION_BYTES:
			receipt_file.close()
			return false
		var parsed = JSON.parse(receipt_file.get_as_text())
		receipt_file.close()
		if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
			return false
		var receipt = parsed.result
		var fixed_root = (OS.get_environment("LOCALAPPDATA") + "/DDAI/helpers/").replace("\\", "/")
		var receipt_path = str(receipt.get("executable_path", "")).replace("\\", "/")
		var expected_hash = str(receipt.get("sha256", ""))
		if receipt.get("schema_version", "") != CATALOG_SCHEMA_VERSION or receipt.get("owner", "") != "org.ddai.connector":
			return false
		if not receipt_path.is_abs_path() or receipt_path.find("..") >= 0 or not receipt_path.to_lower().begins_with(fixed_root.to_lower()) or not _is_sha256_value(expected_hash):
			return false
		if receipt_path.get_file().to_lower() != "ddai-" + expected_hash + ".exe":
			return false
		_helper_file = File.new()
		if _helper_file.open(receipt_path, File.READ) != OK:
			_helper_file = null
			return false
		_helper_hash = HashingContext.new()
		if _helper_hash.start(HashingContext.HASH_SHA256) != OK:
			_helper_file.close()
			_helper_file = null
			return false
		_helper_executable_path = receipt_path
		_helper_expected_hash = expected_hash
		return true

	func advance_helper_verification():
		if _helper_file == null or _helper_hash == null:
			return {"status": "failed"}
		var remaining = _helper_file.get_len() - _helper_file.get_position()
		if remaining > 0:
			var bytes = _helper_file.get_buffer(min(HELPER_HASH_BYTES_PER_TICK, remaining))
			if bytes.size() <= 0 or _helper_hash.update(bytes) != OK:
				_helper_file.close()
				_helper_file = null
				return {"status": "failed"}
			return {"status": "pending"}
		_helper_file.close()
		_helper_file = null
		var actual_hash = _helper_hash.finish().hex_encode()
		_helper_hash = null
		return {"status": "ready" if actual_hash == _helper_expected_hash else "failed"}

	func launch_asset_helper(mailbox_path):
		if _helper_executable_path.length() == 0:
			return false
		var mailbox_root = ProjectSettings.globalize_path(mailbox_path)
		var pid = OS.execute(
			_helper_executable_path,
			["asset-helper", "--mailbox-root", mailbox_root],
			false,
			[],
			false,
			false)
		return pid >= 0

	func _is_sha256_value(value):
		if value.length() != 64:
			return false
		for index in range(value.length()):
			var code = value.ord_at(index)
			if not (code >= 48 and code <= 57) and not (code >= 97 and code <= 102):
				return false
		return true

	func get_asset_list(category):
		return Script.GetAssetList(category)

	func get_pack_metadata(resource_identity):
		var owner = null
		var owner_path_length = -1
		for pack in Global.Header.AssetManifest:
			var pack_path = str(pack.Path).replace("\\", "/")
			var normalized_identity = resource_identity.replace("\\", "/")
			if pack_path.length() > owner_path_length and normalized_identity.begins_with(pack_path):
				owner = pack
				owner_path_length = pack_path.length()
		if owner == null:
			return {
				"pack_id": null,
				"pack_name": null,
				"keywords": [],
				"allow_third_party_use": true,
			}
		return {
			"pack_id": owner.ID,
			"pack_name": owner.Name,
			"keywords": owner.Keywords,
			"allow_third_party_use": owner.AllowThirdPartyUse,
		}

	func load_texture(resource_identity):
		return load(resource_identity)


# Called by Dungeondraft after the map and its drawing assets have loaded.
func start():
	if _runtime_adapter == null:
		_runtime_adapter = LiveRuntimeAdapter.new()
	_ensure_catalog_directories()


# Every update performs at most eight lightweight entry operations or one logical file publication.
func update(_delta):
	_last_update_entry_operations = 0
	_last_update_file_publications = 0
	if _state == "waiting_for_receipt":
		_advance_waiting_for_receipt_state()
	elif _state == "helper_verification":
		_advance_helper_verification_state()
	elif _state == "enumerating":
		_advance_enumerating_state()
	elif _state == "previewing":
		_advance_previewing_state()
	elif _state == "normalizing_pack_id":
		_advance_pack_normalization_state()
	elif _state == "preview_publication":
		_advance_preview_publication_state()
	elif _state == "preview_finalization":
		_advance_preview_finalization_state()
	elif _state == "writing_chunks":
		_advance_writing_chunks_state()
	elif _state == "publishing_candidate":
		_advance_publishing_candidate_state()
	elif _state == "requesting_catalog_commit":
		_advance_catalog_commit_request_state()
	elif _state == "waiting_catalog_commit":
		_advance_waiting_catalog_commit_state()
	if _last_update_entry_operations > MAX_ASSETS_PER_TICK or _last_update_file_publications > 1 or (_last_update_entry_operations > 0 and _last_update_file_publications > 0):
		_state = "failed"


func resolve_asset_ref(asset_ref):
	return _resource_lookup.get(asset_ref, null)


func get_preview_result(asset_ref):
	return _preview_results.get(asset_ref, {
		"ok": false,
		"error": {
			"code": "asset_not_found",
			"message": "The opaque asset reference is not present in the live catalog.",
			"category": null,
		},
	})


func _advance_waiting_for_receipt_state():
	var result = _runtime_adapter.read_runtime_receipt()
	if typeof(result) != TYPE_DICTIONARY or not result.get("ok", false):
		return
	var receipt = result.get("receipt", null)
	if typeof(receipt) != TYPE_DICTIONARY:
		return
	var candidate_session = str(receipt.get("session_id", ""))
	if not _is_safe_segment(candidate_session):
		return
	_session_id = candidate_session
	_state = "helper_verification"


func _advance_helper_verification_state():
	if not _helper_verification_started:
		_helper_verification_started = true
		if not _runtime_adapter.begin_helper_verification():
			_record_error("asset_helper_unavailable", "The owned local asset helper receipt or executable could not be verified.", null)
			_state = "failed"
		return
	var result = _runtime_adapter.advance_helper_verification()
	if typeof(result) != TYPE_DICTIONARY or result.get("status", "failed") == "failed":
		_record_error("asset_helper_unavailable", "The owned local asset helper hash did not verify.", null)
		_state = "failed"
		return
	if result.get("status", "") != "ready":
		return
	_helper_ready = true
	_catalog_revision = 0
	_snapshot_at = _utc_wire_timestamp()
	for category in CATEGORIES:
		_category_counts[category] = 0
	_state = "enumerating"


func _reset_helper_launch_window():
	_helper_launch_attempts = 0
	_helper_launch_started_at = OS.get_ticks_msec()
	_helper_last_launch_at = _helper_launch_started_at - HELPER_LAUNCH_INTERVAL_MSEC


func _advance_helper_launch_window():
	var now = OS.get_ticks_msec()
	if _helper_launch_attempts < HELPER_MAX_LAUNCH_ATTEMPTS and now - _helper_last_launch_at >= HELPER_LAUNCH_INTERVAL_MSEC:
		_helper_last_launch_at = now
		_helper_launch_attempts += 1
		_runtime_adapter.launch_asset_helper(catalog_root.get_base_dir())
		return true
	return _helper_launch_attempts < HELPER_MAX_LAUNCH_ATTEMPTS or now - _helper_launch_started_at < HELPER_RESPONSE_DEADLINE_MSEC


func _advance_enumerating_state():
	if _category_index >= CATEGORIES.size():
		_asset_category_index = 0
		_asset_index = 0
		_state = "previewing"
		return
	var category = CATEGORIES[_category_index]
	if not _enumeration_loaded:
		var listed = _runtime_adapter.get_asset_list(category)
		if typeof(listed) != TYPE_ARRAY:
			_record_error("asset_enumeration_failed", "Dungeondraft did not return an asset list for this category.", category)
			listed = []
		_enumeration_raw = listed
		_enumeration_raw_index = 0
		_enumeration_sorted = []
		_enumeration_seen = {}
		_enumeration_loaded = true
		return
	var processed = 0
	while processed < MAX_ASSETS_PER_TICK and _enumeration_raw_index < _enumeration_raw.size():
		var resource_identity = str(_enumeration_raw[_enumeration_raw_index])
		_enumeration_raw_index += 1
		processed += 1
		_last_update_entry_operations += 1
		if resource_identity.length() == 0 or _enumeration_seen.has(resource_identity):
			continue
		_enumeration_seen[resource_identity] = true
		var insertion_index = _enumeration_sorted.bsearch(resource_identity)
		_enumeration_sorted.insert(insertion_index, resource_identity)
	if _enumeration_raw_index < _enumeration_raw.size():
		return
	_category_resources.append({"category": category, "identities": _enumeration_sorted})
	_category_counts[category] = _enumeration_sorted.size()
	_category_index += 1
	_enumeration_loaded = false
	_enumeration_raw = []
	_enumeration_sorted = []
	_enumeration_seen = {}


func _advance_previewing_state():
	if _asset_category_index >= _category_resources.size():
		_state = "writing_chunks"
		return
	var category_group = _category_resources[_asset_category_index]
	var identities = category_group.identities
	if _asset_index >= identities.size():
		_asset_category_index += 1
		_asset_index = 0
		return
	# Preparation is in-memory only; publication and finalization have their own updates.
	var category = category_group.category
	var resource_identity = identities[_asset_index]
	var pack_metadata = _safe_pack_metadata(resource_identity)
	var pack_id_result = _resolve_pack_id(pack_metadata.pack_id)
	if not pack_id_result.ok:
		return
	var pack_id = pack_id_result.value
	var resource_fingerprint = _sha256_text(resource_identity)
	var asset_ref = "sha256:" + _sha256_text(_pack_id_for_hash(pack_id) + "\n" + category + "\n" + resource_identity)
	_preview_work = {
		"category": category,
		"resource_identity": resource_identity,
		"resource_fingerprint": resource_fingerprint,
		"asset_ref": asset_ref,
		"pack_metadata": pack_metadata,
		"pack_id": pack_id,
		"preview": _prepare_preview(resource_identity),
	}
	_last_update_entry_operations = 1
	_state = "preview_publication"


func _advance_preview_publication_state():
	var preview = _preview_work.preview
	if preview.ok and preview.needs_write:
		if _write_bytes_immutable(preview.path, preview.png) != "created":
			_preview_work.preview = {"ok": false, "message": "The bounded preview could not be published."}
	_state = "preview_finalization"


func _advance_preview_finalization_state():
	var asset_ref = _preview_work.asset_ref
	var category = _preview_work.category
	var preview = _preview_work.preview
	var preview_hash = null
	if preview.ok:
		preview_hash = preview.hash
		_preview_results[asset_ref] = {"ok": true, "preview_hash": preview_hash}
	else:
		var error = _catalog_error("preview_not_available", "Preview unavailable for opaque asset " + asset_ref + ": " + preview.message, category)
		_record_error(error.code, error.message, error.category)
		_preview_results[asset_ref] = {"ok": false, "error": error}
	var entry = _build_catalog_entry(
		asset_ref,
		category,
		_preview_work.resource_identity,
		_preview_work.resource_fingerprint,
		_preview_work.pack_metadata,
		_preview_work.pack_id,
		preview_hash)
	_entries.append(entry)
	_resource_lookup[asset_ref] = _preview_work.resource_identity
	_asset_index += 1
	_preview_work = null
	_last_update_entry_operations = 1
	_state = "previewing"


func _build_catalog_entry(asset_ref, category, resource_identity, resource_fingerprint, pack_metadata, pack_id, preview_hash):
	var display_name = _display_name(resource_identity, category)
	var tags = _normalized_tags(pack_metadata.keywords)
	var search_terms = [display_name]
	if pack_metadata.pack_name != null:
		var bounded_pack_name = _bounded_nonblank_text(pack_metadata.pack_name, 256)
		if bounded_pack_name != null:
			search_terms.append(bounded_pack_name)
	for tag in tags:
		if not search_terms.has(tag):
			search_terms.append(tag)
	return {
		"asset_ref": asset_ref,
		"category": category,
		"display_name": display_name,
		"resource_fingerprint": resource_fingerprint,
		"pack_id": pack_id,
		"pack_name": _bounded_nonblank_text(pack_metadata.pack_name, 256),
		"search_terms": search_terms,
		"tags": tags,
		"preview_hash": preview_hash,
		"allow_third_party_use": bool(pack_metadata.allow_third_party_use),
		"generated": false,
	}


func _advance_writing_chunks_state():
	if _chunk_phase == "building":
		var processed = 0
		while processed < MAX_ASSETS_PER_TICK and _chunk_build_index < _entries.size():
			var entry_text = to_json(_entries[_chunk_build_index])
			var entry_bytes = entry_text.to_utf8().size()
			var separator_bytes = 0 if _chunk_entry_json.size() == 0 else 1
			if _chunk_entry_json.size() > 0 and _chunk_payload_bytes + separator_bytes + entry_bytes > MAX_CHUNK_BYTES:
				_queue_current_chunk()
				separator_bytes = 0
			_chunk_entry_json.append(entry_text)
			_chunk_payload_bytes += separator_bytes + entry_bytes
			_chunk_build_index += 1
			processed += 1
			_last_update_entry_operations += 1
		if _chunk_build_index < _entries.size():
			return
		if _chunk_entry_json.size() > 0:
			_queue_current_chunk()
		_manifest_text = _build_manifest_text()
		if _manifest_text.to_utf8().size() > MAX_MANIFEST_BYTES:
			_record_error("catalog_manifest_too_large", "The bounded catalog manifest exceeds the reader limit.", null)
			_state = "failed"
			return
		_candidate_fingerprint = _sha256_text(_manifest_text)
		_candidate_root = _catalog_commit_root() + "/candidates/" + _candidate_fingerprint
		_candidate_write_index = 0
		_state = "publishing_candidate"


func _queue_current_chunk():
	var chunk_text = "[" + PoolStringArray(_chunk_entry_json).join(",") + "]"
	var payload = chunk_text.to_utf8()
	var file_name = "chunk-%04d.json" % _pending_chunks.size()
	_pending_chunks.append({"text": chunk_text, "entry_count": _chunk_entry_json.size(), "file_name": file_name})
	_chunk_receipts.append({
		"file_name": file_name,
		"sha256": _sha256_bytes(payload),
		"entry_count": _chunk_entry_json.size(),
		"byte_count": payload.size(),
	})
	_chunk_entry_json = []
	_chunk_payload_bytes = 2


func _advance_publishing_candidate_state():
	if _candidate_write_index < _pending_chunks.size():
		var pending = _pending_chunks[_candidate_write_index]
		var payload = pending.text.to_utf8()
		var expected = _chunk_receipts[_candidate_write_index]
		if _write_bytes_immutable_bound(_candidate_root + "/" + pending.file_name, payload, expected.sha256, expected.byte_count) != "valid":
			_record_error("catalog_candidate_conflict", "A staged catalog chunk did not match its content binding.", null)
			_state = "failed"
			return
		_candidate_write_index += 1
		return
	if _write_bytes_immutable_bound(_candidate_root + "/" + MANIFEST_FILE_NAME, _manifest_text.to_utf8(), _candidate_fingerprint, _manifest_text.to_utf8().size()) != "valid":
		_record_error("catalog_candidate_conflict", "The staged catalog manifest did not match its candidate fingerprint.", null)
		_state = "failed"
		return
	_state = "requesting_catalog_commit"


func _advance_catalog_commit_request_state():
	if _commit_request_pending == null:
		var framed = ""
		framed += _framed_string("schema_version", CATALOG_SCHEMA_VERSION)
		framed += _framed_string("session_id", _session_id)
		framed += _framed_string("snapshot_at", _snapshot_at)
		framed += _framed_string("candidate_fingerprint", _candidate_fingerprint)
		_commit_request_hash = _sha256_text(framed)
		_commit_request_id = _commit_request_hash
		_commit_request_pending = {
			"schema_version": CATALOG_SCHEMA_VERSION,
			"request_id": _commit_request_id,
			"request_content_hash": _commit_request_hash,
			"candidate_fingerprint": _candidate_fingerprint,
			"session_id": _session_id,
			"snapshot_at": _snapshot_at,
		}
	var payload = to_json(_commit_request_pending).to_utf8()
	var request_path = _catalog_commit_root() + "/requests/" + _commit_request_id + ".json"
	if _write_bytes_immutable_bound(request_path, payload, _sha256_bytes(payload), payload.size()) != "valid":
		_record_error("catalog_commit_request_failed", "The catalog commit request could not be staged without a binding conflict.", null)
		_state = "failed"
		return
	_commit_request_pending = null
	_reset_helper_launch_window()
	_state = "waiting_catalog_commit"


func _advance_waiting_catalog_commit_state():
	var response = {"ok": false}
	if not _private_request_is_pending(_catalog_commit_root(), _commit_request_id):
		response = _read_catalog_commit_response(_commit_request_id)
	if response.ok:
		_catalog_revision = response.catalog_revision
		_state = "published"
		return
	if response.has("terminal_error"):
		_record_error(response.terminal_error, "The serialized local catalog commit was rejected; the last good snapshot remains current.", null)
		_state = "failed"
		return
	if _advance_helper_launch_window():
		return
	_record_error("catalog_commit_failed", "The serialized local catalog commit did not complete before its bounded deadline; the last good snapshot remains current.", null)
	_state = "failed"


func _read_catalog_commit_response(request_id):
	var response = _read_bounded_dictionary(_catalog_commit_root() + "/responses/" + request_id + ".json", MAX_NORMALIZATION_BYTES)
	if response == null:
		return {"ok": false}
	if response.get("schema_version", "") != CATALOG_SCHEMA_VERSION or response.get("request_id", "") != request_id:
		return {"ok": false, "terminal_error": "catalog_commit_response_invalid"}
	if response.get("request_content_hash", "") != _commit_request_hash or response.get("candidate_fingerprint", "") != _candidate_fingerprint:
		return {"ok": false, "terminal_error": "catalog_commit_response_invalid"}
	if typeof(response.get("success", null)) != TYPE_BOOL:
		return {"ok": false, "terminal_error": "catalog_commit_response_invalid"}
	if not response.success:
		return {"ok": false, "terminal_error": str(response.get("error_code", "catalog_commit_failed"))}
	var revision = response.get("catalog_revision", null)
	var fingerprint = str(response.get("catalog_fingerprint", ""))
	var slot_index = response.get("slot_index", null)
	var state_token = str(response.get("state_token", ""))
	if not _is_json_nonnegative_integer(revision) or not _is_sha256_value(fingerprint) or not _is_json_nonnegative_integer(slot_index) or int(slot_index) > 1:
		return {"ok": false, "terminal_error": "catalog_commit_response_invalid"}
	if not _is_sha256_value(state_token) or state_token != _sha256_text(str(int(revision)) + "\n" + fingerprint + "\n" + str(int(slot_index))):
		return {"ok": false, "terminal_error": "catalog_commit_response_invalid"}
	return {"ok": true, "catalog_revision": int(revision)}


func _build_manifest_text():
	var manifest = {
		"schema_version": CATALOG_SCHEMA_VERSION,
		"session_id": _session_id,
		"catalog_revision": _catalog_revision,
		"catalog_fingerprint": "",
		"snapshot_at": _snapshot_at,
		"complete": true,
		"category_counts": _category_counts,
		"chunks": _chunk_receipts,
		"errors": _published_errors(),
	}
	manifest.catalog_fingerprint = _catalog_fingerprint(manifest)
	return to_json(manifest)


func _catalog_fingerprint(manifest):
	var framed = ""
	framed += _framed_string("schema_version", manifest.schema_version)
	framed += _framed_string("session_id", manifest.session_id)
	framed += _framed_integer("catalog_revision", manifest.catalog_revision)
	framed += _framed_string("snapshot_at", manifest.snapshot_at)
	framed += _framed_boolean("complete", manifest.complete)
	for category in CATEGORIES:
		framed += _framed_integer("category[" + category + "]", manifest.category_counts.get(category, -1))
	framed += _framed_integer("chunks_count", manifest.chunks.size())
	for index in range(manifest.chunks.size()):
		var chunk = manifest.chunks[index]
		framed += _framed_string("chunk[" + str(index) + "].file_name", chunk.file_name)
		framed += _framed_string("chunk[" + str(index) + "].sha256", chunk.sha256)
		framed += _framed_integer("chunk[" + str(index) + "].entry_count", chunk.entry_count)
		framed += _framed_integer("chunk[" + str(index) + "].byte_count", chunk.byte_count)
	framed += _framed_integer("errors_count", manifest.errors.size())
	for index in range(manifest.errors.size()):
		var error = manifest.errors[index]
		framed += _framed_string("error[" + str(index) + "].code", error.code)
		framed += _framed_string("error[" + str(index) + "].message", error.message)
		framed += _framed_string("error[" + str(index) + "].category", error.category)
	return _sha256_text(framed)


func _framed_string(name, value):
	if value == null:
		return name + "=null\n"
	var text = str(value)
	return name + "=" + str(text.to_utf8().size()) + ":" + text + "\n"


func _framed_integer(name, value):
	return name + "=" + str(int(value)) + "\n"


func _framed_boolean(name, value):
	return name + "=" + ("true" if value else "false") + "\n"


func _prepare_preview(resource_identity):
	var texture = _runtime_adapter.load_texture(resource_identity)
	if texture == null or not texture.has_method("get_data"):
		return {"ok": false, "message": "The loaded asset is not a readable texture."}
	var source_image = texture.get_data()
	if source_image == null:
		return {"ok": false, "message": "The loaded texture did not expose image data."}
	var image = source_image.duplicate()
	if image.get_width() <= 0 or image.get_height() <= 0:
		return {"ok": false, "message": "The loaded texture did not expose image data."}
	if image.get_width() > MAX_PREVIEW_EDGE or image.get_height() > MAX_PREVIEW_EDGE:
		var scale = min(float(MAX_PREVIEW_EDGE) / float(image.get_width()), float(MAX_PREVIEW_EDGE) / float(image.get_height()))
		var width = max(1, int(round(float(image.get_width()) * scale)))
		var height = max(1, int(round(float(image.get_height()) * scale)))
		image.resize(width, height, Image.INTERPOLATE_LANCZOS)
	var png = image.save_png_to_buffer()
	if png.size() <= 0:
		return {"ok": false, "message": "PNG encoding failed."}
	if png.size() > MAX_PREVIEW_BYTES:
		return {"ok": false, "message": "The encoded preview exceeds the 262144-byte limit."}
	var preview_hash = _sha256_bytes(png)
	var preview_path = _previews_root() + "/" + preview_hash + ".png"
	var validation = _validate_existing_preview(preview_path, preview_hash)
	if validation == "valid":
		return {"ok": true, "hash": preview_hash, "needs_write": false}
	if validation == "invalid":
		return {"ok": false, "message": "A conflicting content-addressed preview already exists."}
	return {"ok": true, "hash": preview_hash, "needs_write": true, "path": preview_path, "png": png}


func _validate_existing_preview(path, expected_hash):
	var file = File.new()
	if not file.file_exists(path):
		return "missing"
	if file.open(path, File.READ) != OK:
		return "invalid"
	if file.get_len() > MAX_PREVIEW_BYTES:
		file.close()
		return "invalid"
	var bytes = file.get_buffer(file.get_len())
	file.close()
	return "valid" if _sha256_bytes(bytes) == expected_hash else "invalid"


func _safe_pack_metadata(resource_identity):
	var metadata = _runtime_adapter.get_pack_metadata(resource_identity)
	if typeof(metadata) != TYPE_DICTIONARY:
		return {"pack_id": null, "pack_name": null, "keywords": [], "allow_third_party_use": true}
	return {
		"pack_id": metadata.get("pack_id", null),
		"pack_name": metadata.get("pack_name", null),
		"keywords": metadata.get("keywords", []),
		"allow_third_party_use": metadata.get("allow_third_party_use", true),
	}


func _resolve_pack_id(value):
	if value == null:
		return {"ok": true, "value": null}
	var raw_value = str(value)
	var ascii_only = true
	for index in range(raw_value.length()):
		if raw_value.ord_at(index) > 127:
			ascii_only = false
			break
	if ascii_only:
		var normalized_ascii = raw_value.strip_edges().to_lower()
		return {"ok": true, "value": null if normalized_ascii.length() == 0 else normalized_ascii}
	var request_id = _sha256_text(raw_value)
	if _pack_normalization_values.has(request_id):
		return {"ok": true, "value": _pack_normalization_values[request_id]}
	_normalization_active_request_id = request_id
	_normalization_active_request_hash = _sha256_text(
		"schema_version=3:1.0\nrequest_id=64:" + request_id + "\nvalue=" +
		str(raw_value.to_utf8().size()) + ":" + raw_value + "\n")
	var request_path = _pack_normalization_root() + "/requests/" + request_id + ".json"
	_normalization_request_pending = {
		"path": request_path,
		"payload": {
			"schema_version": CATALOG_SCHEMA_VERSION,
			"request_id": request_id,
			"request_content_hash": _normalization_active_request_hash,
			"value": raw_value,
		},
	}
	_state = "normalizing_pack_id"
	return {"ok": false}


func _advance_pack_normalization_state():
	if _normalization_request_pending != null:
		var pending = _normalization_request_pending
		var payload = to_json(pending.payload).to_utf8()
		var result = _write_bytes_immutable_bound(pending.path, payload, _sha256_bytes(payload), payload.size())
		if result != "valid":
			_record_error("pack_normalization_failed", "The non-ASCII pack normalization request conflicted with private mailbox state; the last good catalog remains current.", null)
			_normalization_request_pending = null
			_normalization_active_request_id = ""
			_normalization_active_request_hash = ""
			_state = "failed"
			return
		_normalization_request_pending = null
		_reset_helper_launch_window()
		return
	var response = {"ok": false}
	if not _private_request_is_pending(_pack_normalization_root(), _normalization_active_request_id):
		response = _read_pack_normalization_response(_normalization_active_request_id)
	if response.ok:
		_pack_normalization_values[_normalization_active_request_id] = response.value
		_normalization_active_request_id = ""
		_normalization_active_request_hash = ""
		_state = "previewing"
		return
	if response.has("terminal_error"):
		_record_error("pack_normalization_failed", "The non-ASCII pack normalization response was not bound to its request; the last good catalog remains current.", null)
		_normalization_active_request_id = ""
		_normalization_active_request_hash = ""
		_state = "failed"
		return
	if _advance_helper_launch_window():
		return
	_record_error("pack_normalization_failed", "The non-ASCII pack normalization request exceeded its bounded deadline; the last good catalog remains current.", null)
	_normalization_active_request_id = ""
	_normalization_active_request_hash = ""
	_state = "failed"


func _read_pack_normalization_response(request_id):
	var path = _pack_normalization_root() + "/responses/" + request_id + ".json"
	var response = _read_bounded_dictionary(path, MAX_NORMALIZATION_BYTES)
	if response == null:
		return {"ok": false}
	if response.get("schema_version", "") != CATALOG_SCHEMA_VERSION or response.get("request_id", "") != request_id:
		return {"ok": false, "terminal_error": "pack_normalization_failed"}
	if response.get("request_content_hash", "") != _normalization_active_request_hash:
		return {"ok": false, "terminal_error": "pack_normalization_failed"}
	if not response.has("normalized_pack_id"):
		return {"ok": false, "terminal_error": "pack_normalization_failed"}
	var normalized = response.normalized_pack_id
	if normalized == null:
		return {"ok": true, "value": null}
	if typeof(normalized) != TYPE_STRING or normalized.strip_edges().length() == 0:
		return {"ok": false, "terminal_error": "pack_normalization_failed"}
	return {"ok": true, "value": normalized}


func _pack_id_for_hash(pack_id):
	return "" if pack_id == null else pack_id


func _normalized_tags(raw_keywords):
	var candidates = []
	if typeof(raw_keywords) == TYPE_ARRAY or typeof(raw_keywords) == TYPE_STRING_ARRAY:
		candidates = raw_keywords
	elif raw_keywords != null:
		candidates = str(raw_keywords).replace(";", ",").split(",")
	var tags = []
	var seen = {}
	for candidate in candidates:
		if tags.size() >= 64:
			break
		var tag = str(candidate).strip_edges().substr(0, 128)
		if tag.length() == 0 or seen.has(tag):
			continue
		seen[tag] = true
		tags.append(tag)
	return tags


func _display_name(resource_identity, category):
	var value = resource_identity.get_file().get_basename().replace("_", " ").replace("-", " ").strip_edges()
	if value.length() == 0:
		value = category + " asset"
	return value.substr(0, 256)


func _bounded_nonblank_text(value, maximum_characters):
	if value == null:
		return null
	var text = str(value).strip_edges()
	if text.length() == 0:
		return null
	return text.substr(0, maximum_characters)


func _catalog_error(code, message, category):
	return {
		"code": _bounded_utf8_text(code, MAX_ERROR_CODE_BYTES, "catalog_error"),
		"message": _bounded_utf8_text(message, MAX_ERROR_MESSAGE_BYTES, "Catalog error."),
		"category": category if CATEGORIES.has(category) else null,
	}


func _record_error(code, message, category):
	if _errors.size() < MAX_ERROR_RECORDS - 1:
		_errors.append(_catalog_error(code, message, category))
	else:
		_suppressed_error_count += 1


func _published_errors():
	var published = _errors.duplicate(true)
	if _suppressed_error_count > 0:
		published.append(_catalog_error(
			"errors_truncated",
			str(_suppressed_error_count) + " additional bounded catalog errors were suppressed.",
			null))
	return published


func _bounded_utf8_text(value, maximum_bytes, fallback):
	var text = str(value).strip_edges().substr(0, maximum_bytes)
	while text.length() > 0 and text.to_utf8().size() > maximum_bytes:
		text = text.substr(0, text.length() - 1)
	return fallback if text.length() == 0 else text


func _sha256_text(text):
	return _sha256_bytes(str(text).to_utf8())


func _sha256_bytes(bytes):
	var context = HashingContext.new()
	context.start(HashingContext.HASH_SHA256)
	context.update(bytes)
	return context.finish().hex_encode()


func _write_bytes_immutable(path, bytes):
	var directory = Directory.new()
	directory.make_dir_recursive(path.get_base_dir())
	if directory.file_exists(path):
		return "existing"
	_last_update_file_publications += 1
	var temporary_path = path + "." + str(OS.get_ticks_msec()) + ".tmp"
	directory.remove(temporary_path)
	var file = File.new()
	if file.open(temporary_path, File.WRITE) != OK:
		return "write_failed"
	file.store_buffer(bytes)
	file.flush()
	file.close()
	if directory.rename(temporary_path, path) == OK:
		return "created"
	directory.remove(temporary_path)
	return "write_failed"


func _write_bytes_immutable_bound(path, bytes, expected_hash, expected_byte_count):
	var existing = _read_bounded_bytes(path, expected_byte_count)
	if existing != null:
		return "valid" if existing.size() == expected_byte_count and _sha256_bytes(existing) == expected_hash else "conflict"
	return "valid" if _write_bytes_immutable(path, bytes) == "created" else "write_failed"


func _read_bounded_bytes(path, maximum_bytes):
	var file = File.new()
	if not file.file_exists(path) or file.open(path, File.READ) != OK:
		return null
	if file.get_len() > maximum_bytes:
		file.close()
		return null
	var bytes = file.get_buffer(file.get_len())
	file.close()
	return bytes


func _ensure_catalog_directories():
	var directory = Directory.new()
	directory.make_dir_recursive(catalog_root)
	directory.make_dir_recursive(_previews_root())


func _previews_root():
	return catalog_root + "/previews"


func _pack_normalization_root():
	return catalog_root.get_base_dir() + "/private/pack-normalization"


func _catalog_commit_root():
	return catalog_root.get_base_dir() + "/private/catalog-commit"


func _read_bounded_dictionary(path, maximum_bytes):
	var file = File.new()
	if not file.file_exists(path) or file.open(path, File.READ) != OK:
		return null
	if file.get_len() > maximum_bytes:
		file.close()
		return null
	var parsed = JSON.parse(file.get_as_text())
	file.close()
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		return null
	return parsed.result


func _private_request_is_pending(private_root, request_id):
	return Directory.new().file_exists(private_root + "/requests/" + request_id + ".json")


func _is_json_nonnegative_integer(value):
	return typeof(value) == TYPE_REAL and not is_nan(value) and not is_inf(value) and value >= 0.0 and value <= 9007199254740991.0 and value == floor(value)


func _is_sha256_value(value):
	if typeof(value) != TYPE_STRING or value.length() != 64:
		return false
	for index in range(value.length()):
		var code = value.ord_at(index)
		if not (code >= 48 and code <= 57) and not (code >= 97 and code <= 102):
			return false
	return true


func _is_safe_segment(value):
	if value.length() == 0 or value.length() > 128 or value == "." or value == "..":
		return false
	for index in range(value.length()):
		var code = value.ord_at(index)
		if not (code >= 48 and code <= 57) and not (code >= 65 and code <= 90) and not (code >= 97 and code <= 122) and code != 45 and code != 46 and code != 95:
			return false
	return true


func _utc_wire_timestamp():
	var current = OS.get_datetime_from_unix_time(OS.get_unix_time())
	return "%04d-%02d-%02dT%02d:%02d:%02d.0000000+00:00" % [
		current.year,
		current.month,
		current.day,
		current.hour,
		current.minute,
		current.second,
	]
