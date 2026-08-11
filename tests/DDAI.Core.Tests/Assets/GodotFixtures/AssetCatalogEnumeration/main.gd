extends Node

const CatalogScript = preload("res://ddai_asset_catalog.gd")


class TypedArrayAdapter:
	func get_asset_list(category):
		if category != "Terrain":
			return PoolStringArray()
		return PoolStringArray([
			"res://private/zeta.png",
			"res://private/echo.png",
			"res://private/delta.png",
			"res://private/charlie.png",
			"res://private/bravo.png",
			"res://private/alpha.png",
			"res://private/hotel.png",
			"res://private/golf.png",
			"res://private/foxtrot.png",
			"res://private/alpha.png",
		])


class WrongTypeAdapter:
	func get_asset_list(category):
		return {"secret": "res://private/" + category + "-must-not-be-reflected.png"}


class ToolScopedScriptApi:
	func GetAssetList(category):
		if category != "Terrain":
			return PoolStringArray()
		return PoolStringArray([
			"res://tool-scope/zeta.png",
			"res://tool-scope/alpha.png",
		])


class FakeObjectLibraryPanel:
	var searchEngine = {}

	func _init(value):
		searchEngine = value


class FakeEditor:
	var ObjectLibraryPanel = null

	func _init(panel):
		ObjectLibraryPanel = panel


class ReadyHelperAdapter:
	func begin_helper_verification():
		return true

	func advance_helper_verification():
		return {"status": "ready"}

	func get_library_metadata_sources():
		return {"search_engine": {}, "tag_index_lookup": null}


class LibraryMetadataAdapter:
	var search_engine = {}
	var tag_index_lookup = null

	func _init(engine, tags):
		search_engine = engine
		tag_index_lookup = tags

	func get_library_metadata_sources():
		return {
			"search_engine": search_engine,
			"tag_index_lookup": tag_index_lookup,
		}


class ControlledWireClock:
	var current = ""
	var calls = 0

	func now():
		calls += 1
		return current


func _ready():
	var typed_array = _test_typed_array_enumeration()
	var wrong_type = _test_wrong_type_fails_closed()
	var tool_scope_wiring = _test_production_live_wiring_uses_tool_scope()
	var snapshot_finalization = _test_snapshot_timestamp_finalizes_once()
	var library_metadata = _test_library_metadata_index_is_bounded_and_exact()
	var hostile_metadata = _test_hostile_library_metadata_fails_closed()
	var shared_hostile_corpus = _test_shared_hostile_metadata_corpus()
	var pack_keyword_bounds = _test_pack_keywords_remain_searchable_and_fail_closed_over_bound()
	var key_validation_incremental = _test_library_key_validation_is_incremental()
	var progress_receipt = _test_progress_receipt_is_bounded_and_private()
	print("DDAI_TYPED_ARRAY_ENUMERATION:", typed_array)
	print("DDAI_WRONG_TYPE_FAILS_CLOSED:", wrong_type)
	print("DDAI_ENUMERATION_DIAGNOSTIC_CLOSED_WORLD:", wrong_type)
	print("DDAI_TOOL_SCOPE_LIVE_WIRING:", tool_scope_wiring)
	print("DDAI_SNAPSHOT_TIMESTAMP_FINALIZED_ONCE:", snapshot_finalization)
	print("DDAI_LIBRARY_METADATA_INDEX:", library_metadata)
	print("DDAI_LIBRARY_METADATA_HOSTILE_FAILS_CLOSED:", hostile_metadata)
	print("DDAI_LIBRARY_METADATA_SHARED_CORPUS:", shared_hostile_corpus)
	print("DDAI_PACK_KEYWORD_BOUNDS:", pack_keyword_bounds)
	print("DDAI_LIBRARY_KEY_VALIDATION_INCREMENTAL:", key_validation_incremental)
	print("DDAI_CATALOG_PROGRESS_RECEIPT:", progress_receipt)
	get_tree().quit(0 if typed_array and wrong_type and tool_scope_wiring and snapshot_finalization and library_metadata and hostile_metadata and shared_hostile_corpus and pack_keyword_bounds and key_validation_incremental and progress_receipt else 1)


func _test_typed_array_enumeration():
	var catalog = CatalogScript.new()
	catalog._runtime_adapter = TypedArrayAdapter.new()
	catalog._state = "enumerating"
	catalog._category_counts["Terrain"] = 0
	catalog._advance_enumerating_state()
	if catalog._errors.size() != 0:
		return false
	while catalog._category_index == 0:
		catalog._last_update_entry_operations = 0
		catalog._advance_enumerating_state()
		if catalog._last_update_entry_operations > CatalogScript.MAX_ASSETS_PER_TICK:
			return false
	if catalog._category_resources.size() != 1 or catalog._category_counts["Terrain"] != 9:
		return false
	var expected = [
		"res://private/alpha.png",
		"res://private/bravo.png",
		"res://private/charlie.png",
		"res://private/delta.png",
		"res://private/echo.png",
		"res://private/foxtrot.png",
		"res://private/golf.png",
		"res://private/hotel.png",
		"res://private/zeta.png",
	]
	return catalog._category_resources[0].identities == expected


func _test_wrong_type_fails_closed():
	var catalog = CatalogScript.new()
	catalog._runtime_adapter = WrongTypeAdapter.new()
	catalog._state = "enumerating"
	for category in CatalogScript.CATEGORIES:
		catalog._category_counts[category] = 0
	var advances = 0
	while catalog._category_index < CatalogScript.CATEGORIES.size() and advances < CatalogScript.CATEGORIES.size() * 3:
		catalog._advance_enumerating_state()
		advances += 1
	if catalog._category_index != CatalogScript.CATEGORIES.size():
		return false
	if catalog._category_counts.size() != CatalogScript.CATEGORIES.size():
		return false
	if catalog._category_resources.size() != CatalogScript.CATEGORIES.size():
		return false
	if catalog._errors.size() != CatalogScript.CATEGORIES.size():
		return false
	var expected_message = (
		"Dungeondraft asset-list boundary diagnostic: adapter_return_type_code=" +
		str(TYPE_DICTIONARY) + "; reason=unsupported_collection_type.")
	for index in range(CatalogScript.CATEGORIES.size()):
		var category = CatalogScript.CATEGORIES[index]
		var error = catalog._errors[index]
		var category_resources = catalog._category_resources[index]
		if error.size() != 3:
			return false
		if error.code != "asset_enumeration_failed" or error.category != category:
			return false
		if error.message != expected_message:
			return false
		if error.message.to_utf8().size() > CatalogScript.MAX_ERROR_MESSAGE_BYTES:
			return false
		if error.message.find("res://") >= 0 or error.message.find("must-not-be-reflected") >= 0:
			return false
		if catalog._category_counts[category] != 0:
			return false
		if category_resources.category != category or category_resources.identities.size() != 0:
			return false
	return true


func _test_production_live_wiring_uses_tool_scope():
	var catalog = CatalogScript.new()
	catalog.Script = ToolScopedScriptApi.new()
	var library_texture = _texture("res://tool-scope/alpha.png")
	Global.Editor = null
	var unavailable_sources = catalog._get_live_library_metadata_sources()
	if unavailable_sources.search_engine != null or unavailable_sources.tag_index_lookup != null:
		return false
	Global.Editor = FakeEditor.new(FakeObjectLibraryPanel.new({"semantic": [library_texture]}))
	catalog.start()
	var sources = catalog._runtime_adapter.get_library_metadata_sources()
	if sources.search_engine.get("semantic", []) != [library_texture] or sources.tag_index_lookup != null:
		return false
	catalog._state = "enumerating"
	for category in CatalogScript.CATEGORIES:
		catalog._category_counts[category] = 0
	var advances = 0
	while catalog._category_index == 0 and advances < 8:
		catalog._advance_enumerating_state()
		advances += 1
	if catalog._category_index != 1 or catalog._errors.size() != 0:
		return false
	if catalog._category_resources.size() != 1 or catalog._category_counts["Terrain"] != 2:
		return false
	return catalog._category_resources[0].identities == [
		"res://tool-scope/alpha.png",
		"res://tool-scope/zeta.png",
	]


func _test_snapshot_timestamp_finalizes_once():
	var catalog = CatalogScript.new()
	catalog.catalog_root = "res://snapshot-finalization/catalog"
	var clock = ControlledWireClock.new()
	clock.current = "2026-08-11T12:00:00.0000000+00:00"
	catalog.DdaiTestClock = clock
	catalog._runtime_adapter = ReadyHelperAdapter.new()
	catalog._state = "helper_verification"
	catalog._advance_helper_verification_state()
	catalog._advance_helper_verification_state()
	if catalog._state != "indexing_library_metadata" or not _advance_metadata(catalog):
		return false

	clock.current = "2026-08-11T12:02:00.0000000+00:00"
	catalog._category_index = CatalogScript.CATEGORIES.size()
	catalog._advance_enumerating_state()
	catalog._advance_previewing_state()
	catalog._advance_writing_chunks_state()
	if catalog._state != "publishing_candidate":
		return false
	if catalog._snapshot_at != clock.current or clock.calls != 1:
		return false
	var parsed_manifest = JSON.parse(catalog._manifest_text)
	if parsed_manifest.error != OK or parsed_manifest.result.snapshot_at != clock.current:
		return false
	var snapshot_at = catalog._snapshot_at
	var manifest_text = catalog._manifest_text
	var candidate_fingerprint = catalog._candidate_fingerprint

	catalog._last_update_file_publications = 0
	catalog._advance_publishing_candidate_state()
	catalog._last_update_file_publications = 0
	catalog._advance_publishing_candidate_state()
	if catalog._snapshot_at != snapshot_at or catalog._manifest_text != manifest_text:
		return false
	if catalog._candidate_fingerprint != candidate_fingerprint or clock.calls != 1:
		return false

	catalog._last_update_file_publications = 0
	catalog._advance_catalog_commit_request_state()
	var request_id = catalog._commit_request_id
	var request_hash = catalog._commit_request_hash
	catalog._last_update_file_publications = 0
	catalog._advance_catalog_commit_request_state()
	return (
		catalog._snapshot_at == snapshot_at and
		catalog._manifest_text == manifest_text and
		catalog._candidate_fingerprint == candidate_fingerprint and
		catalog._commit_request_id == request_id and
		catalog._commit_request_hash == request_hash and
		clock.calls == 1)


func _texture(identity):
	var texture = ImageTexture.new()
	texture.set_path(identity)
	return texture


func _advance_metadata(catalog):
	var advances = 0
	while catalog._state == "indexing_library_metadata" and advances < 128:
		catalog._last_update_entry_operations = 0
		catalog._advance_library_metadata_state()
		if catalog._last_update_entry_operations > CatalogScript.MAX_ASSETS_PER_TICK:
			return false
		advances += 1
	return catalog._state == "enumerating"


func _build_metadata_entry(catalog, asset_ref, category, identity, fingerprint, pack_metadata, pack_id):
	catalog._preview_work = {
		"asset_ref": asset_ref,
		"category": category,
		"resource_identity": identity,
		"resource_fingerprint": fingerprint,
		"pack_metadata": pack_metadata,
		"pack_id": pack_id,
	}
	catalog._state = "preview_metadata"
	for _index in range(128):
		catalog._last_update_entry_operations = 0
		catalog._advance_preview_metadata_state()
		if catalog._last_update_entry_operations > CatalogScript.MAX_ASSETS_PER_TICK:
			return null
		if catalog._state == "failed":
			return null
		if catalog._state == "preview_finalization":
			return catalog._build_catalog_entry_from_metadata(null)
	return null


func _test_library_metadata_index_is_bounded_and_exact():
	var chair = _texture("res://private/object-chair.png")
	var altar = _texture("res://private/object-altar.png")
	var catalog = CatalogScript.new()
	catalog._runtime_adapter = LibraryMetadataAdapter.new({
		"  Shrine  ": [altar],
		"shrine": [altar, altar],
		"Ancient": [altar],
		"seating": [chair],
	}, {"ancient": 0, "unused": 1})
	catalog._state = "indexing_library_metadata"
	if not _advance_metadata(catalog):
		return false
	var altar_terms = catalog._library_search_terms_by_resource.get(altar.resource_path, [])
	var altar_tags = catalog._library_tags_by_resource.get(altar.resource_path, [])
	var chair_terms = catalog._library_search_terms_by_resource.get(chair.resource_path, [])
	var entry = _build_metadata_entry(catalog,
		"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		"Objects",
		altar.resource_path,
		"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
		{"pack_name": "Temple Pack", "keywords": "Religious; Ceremonial", "allow_third_party_use": true},
		null)
	var public_json = to_json(entry)
	return (
		entry.search_terms == ["ancient", "ceremonial", "object altar", "religious", "shrine", "temple pack"] and
		entry.tags == ["ancient"] and
		altar_terms == ["ancient", "shrine"] and
		altar_tags == ["ancient"] and
		chair_terms == ["seating"] and
		catalog._errors.size() == 0 and
		to_json(catalog._library_search_terms_by_resource).find("res://") >= 0 and
		public_json.find("res://") < 0 and public_json.find("object-altar") < 0)


func _test_hostile_library_metadata_fails_closed():
	var secret = _texture("res://private/do-not-leak.png")
	for invalid_value in ["not-an-array", [null]]:
		var unsafe_structural_catalog = CatalogScript.new()
		unsafe_structural_catalog._runtime_adapter = LibraryMetadataAdapter.new({"../secret": invalid_value}, null)
		unsafe_structural_catalog._state = "indexing_library_metadata"
		for _index in range(16):
			if unsafe_structural_catalog._state != "indexing_library_metadata":
				break
			unsafe_structural_catalog._advance_library_metadata_state()
		if unsafe_structural_catalog._state != "failed":
			return false
	var unsafe_term_catalog = CatalogScript.new()
	unsafe_term_catalog._runtime_adapter = LibraryMetadataAdapter.new({"../secret": [secret]}, null)
	unsafe_term_catalog._state = "indexing_library_metadata"
	for _index in range(16):
		if unsafe_term_catalog._state != "indexing_library_metadata":
			break
		unsafe_term_catalog._advance_library_metadata_state()
	if unsafe_term_catalog._state != "enumerating" or unsafe_term_catalog._errors.size() != 1:
		return false
	if unsafe_term_catalog._errors[0].code != "library_metadata_term_ignored":
		return false
	if to_json(unsafe_term_catalog._errors).find("res://") >= 0 or unsafe_term_catalog._library_search_terms_by_resource.size() != 0:
		return false
	var null_search_key = {}
	null_search_key[null] = [secret]
	var null_tag_key = {}
	null_tag_key[null] = 0
	for fixture in [
		{"engine": {"valid": "res://private/do-not-leak.png"}, "tags": null},
		{"engine": {"valid": [null]}, "tags": null},
		{"engine": {"valid": [secret]}, "tags": {"valid": "wrong"}},
		{"engine": null_search_key, "tags": null},
		{"engine": {"valid": [secret]}, "tags": null_tag_key},
	]:
		var catalog = CatalogScript.new()
		catalog._runtime_adapter = LibraryMetadataAdapter.new(fixture.engine, fixture.tags)
		catalog._state = "indexing_library_metadata"
		for _index in range(16):
			if catalog._state != "indexing_library_metadata":
				break
			catalog._advance_library_metadata_state()
		if catalog._state != "failed" or catalog._errors.size() != 1:
			return false
		var encoded = to_json(catalog._errors)
		if encoded.find("res://") >= 0 or encoded.find("do-not-leak") >= 0:
			return false
	return true


func _test_shared_hostile_metadata_corpus():
	var file = File.new()
	if file.open("res://metadata-hostile-corpus.json", File.READ) != OK:
		return false
	var parsed = JSON.parse(file.get_as_text())
	file.close()
	if parsed.error != OK or typeof(parsed.result) != TYPE_ARRAY:
		return false
	var catalog = CatalogScript.new()
	if catalog._canonical_library_term(char(0xd800)) != null:
		return false
	for fixture in parsed.result:
		var raw = str(fixture.get("raw", ""))
		if fixture.has("repeat"):
			raw = _repeat_text(str(fixture.repeat), int(fixture.count))
		if catalog._canonical_library_term(raw) != fixture.get("expected", null):
			return false
		if fixture.has("tag_index"):
			var texture = _texture("res://private/tag-index.png")
			var invalid_catalog = CatalogScript.new()
			invalid_catalog._runtime_adapter = LibraryMetadataAdapter.new(
				{str(fixture.expected): [texture]},
				{str(fixture.expected): int(fixture.tag_index)})
			invalid_catalog._state = "indexing_library_metadata"
			for _index in range(16):
				if invalid_catalog._state != "indexing_library_metadata":
					break
				invalid_catalog._advance_library_metadata_state()
			if invalid_catalog._state != "failed":
				return false
	return true


func _test_pack_keywords_remain_searchable_and_fail_closed_over_bound():
	var identity = "res://private/saturated-keywords.png"
	var catalog = CatalogScript.new()
	var library_terms = []
	for index in range(CatalogScript.MAX_LIBRARY_SEARCH_TERMS_PER_ASSET):
		library_terms.append("library term " + str(index))
	catalog._library_search_terms_by_resource[identity] = library_terms
	var keywords = []
	for index in range(8):
		keywords.append("pack keyword " + str(index))
	var entry = _build_metadata_entry(catalog,
		"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		"Objects",
		identity,
		"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
		{"pack_name": "Fixture Pack", "keywords": keywords, "allow_third_party_use": true},
		null)
	if entry == null or entry.search_terms.size() != CatalogScript.MAX_CATALOG_SEARCH_TERMS_PER_ASSET:
		return false
	for keyword in keywords:
		if not entry.search_terms.has(keyword):
			return false
	if entry.tags.size() != 0:
		return false
	var oversized = keywords.duplicate()
	oversized.append("pack keyword 8")
	var invalid_catalog = CatalogScript.new()
	var invalid_entry = _build_metadata_entry(invalid_catalog,
		"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
		"Objects",
		"res://private/oversized-keywords.png",
		"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
		{"pack_name": null, "keywords": oversized, "allow_third_party_use": true},
		null)
	return invalid_entry == null and invalid_catalog._state == "failed"


func _test_library_key_validation_is_incremental():
	var search_engine = {}
	var tag_index_lookup = {}
	for index in range(CatalogScript.MAX_ASSETS_PER_TICK * 2 + 1):
		var term = "incremental term " + str(index)
		search_engine[term] = []
		tag_index_lookup[term] = index
	var catalog = CatalogScript.new()
	catalog._runtime_adapter = LibraryMetadataAdapter.new(search_engine, tag_index_lookup)
	catalog._state = "indexing_library_metadata"
	catalog._advance_library_metadata_state()
	if catalog._library_keys.size() != CatalogScript.MAX_ASSETS_PER_TICK * 2 + 1:
		return false
	catalog._last_update_entry_operations = 0
	catalog._advance_library_metadata_state()
	if catalog._last_update_entry_operations != CatalogScript.MAX_ASSETS_PER_TICK:
		return false
	if catalog._library_key_index != CatalogScript.MAX_ASSETS_PER_TICK:
		return false
	if catalog._library_metadata_phase != "validating_search_keys":
		return false
	return _advance_metadata(catalog)


func _test_progress_receipt_is_bounded_and_private():
	var terminal_catalog = CatalogScript.new()
	terminal_catalog._category_index = CatalogScript.CATEGORIES.size() - 1
	terminal_catalog._enumeration_loaded = true
	terminal_catalog._enumeration_raw = ["res://terminal.png"]
	terminal_catalog._enumeration_raw_index = 1
	terminal_catalog._enumeration_sorted = ["res://terminal.png"]
	terminal_catalog._advance_enumerating_state()
	if terminal_catalog._enumeration_raw.size() != 0 or terminal_catalog._enumeration_raw_index != 0:
		return false
	var catalog = CatalogScript.new()
	catalog.catalog_root = "res://progress-receipt/catalog"
	catalog._session_id = "fixture-session"
	catalog._state = "indexing_library_metadata"
	catalog._library_metadata_phase = "indexing"
	catalog._library_keys = ["private-term"]
	catalog._library_key_index = 0
	catalog._library_texture_index = 17
	catalog._library_associations_processed = 17
	catalog._category_index = 2
	catalog._asset_category_index = 1
	catalog._asset_index = 3
	catalog._enumeration_raw = ["one", "two"]
	catalog._enumeration_raw_index = 1
	catalog._enumerated_asset_count = 5
	catalog._entries = [{"private": "res://must-not-leak.png"}]
	catalog._errors = [{"code": "library_metadata_invalid", "message": "bounded failure", "category": "Objects"}]
	catalog._ensure_catalog_directories()
	if not catalog._publish_progress_receipt():
		return false
	if catalog._last_update_entry_operations != 0 or catalog._last_update_file_publications != 1:
		return false
	var path = "res://progress-receipt/private/asset-catalog-progress/progress-slot-0.json"
	var file = File.new()
	if file.open(path, File.READ) != OK or file.get_len() > CatalogScript.MAX_PROGRESS_RECEIPT_BYTES:
		return false
	var text = file.get_as_text()
	file.close()
	var parsed = JSON.parse(text)
	if parsed.error != OK or typeof(parsed.result) != TYPE_DICTIONARY:
		return false
	var receipt = parsed.result
	return (
		receipt.keys().size() == 20 and
		receipt.schema_version == CatalogScript.CATALOG_SCHEMA_VERSION and
		receipt.session_id == "fixture-session" and
		receipt.state == "indexing_library_metadata" and
		receipt.phase == "indexing" and
		receipt.library_key_count == 1 and
		receipt.library_key_index == 0 and
		receipt.library_texture_index == 17 and
		receipt.library_associations_processed == 17 and
		receipt.category_index == 2 and
		receipt.category_count == CatalogScript.CATEGORIES.size() and
		receipt.enumeration_raw_count == 2 and
		receipt.enumeration_raw_index == 1 and
		receipt.enumerated_asset_count == 5 and
		receipt.asset_category_index == 1 and
		receipt.asset_index == 3 and
		receipt.entry_count == 1 and
		receipt.error_count == 1 and
		receipt.last_error_code == "library_metadata_invalid" and
		receipt.last_error_message == "bounded failure" and
		text.find("res://") < 0 and text.find("private-term") < 0 and text.find("must-not-leak") < 0)


func _repeat_text(value, count):
	var result = ""
	for _index in range(count):
		result += value
	return result
