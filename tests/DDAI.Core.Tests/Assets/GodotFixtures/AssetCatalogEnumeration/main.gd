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


func _ready():
	var typed_array = _test_typed_array_enumeration()
	var wrong_type = _test_wrong_type_fails_closed()
	var tool_scope_wiring = _test_production_live_wiring_uses_tool_scope()
	print("DDAI_TYPED_ARRAY_ENUMERATION:", typed_array)
	print("DDAI_WRONG_TYPE_FAILS_CLOSED:", wrong_type)
	print("DDAI_ENUMERATION_DIAGNOSTIC_CLOSED_WORLD:", wrong_type)
	print("DDAI_TOOL_SCOPE_LIVE_WIRING:", tool_scope_wiring)
	get_tree().quit(0 if typed_array and wrong_type and tool_scope_wiring else 1)


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
	catalog.start()
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
