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
	func get_asset_list(_category):
		return {"secret": "res://private/must-not-be-reflected.png"}


func _ready():
	var typed_array = _test_typed_array_enumeration()
	var wrong_type = _test_wrong_type_fails_closed()
	print("DDAI_TYPED_ARRAY_ENUMERATION:", typed_array)
	print("DDAI_WRONG_TYPE_FAILS_CLOSED:", wrong_type)
	get_tree().quit(0 if typed_array and wrong_type else 1)


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
	catalog._category_counts["Terrain"] = 0
	catalog._advance_enumerating_state()
	catalog._advance_enumerating_state()
	if catalog._category_counts["Terrain"] != 0 or catalog._errors.size() != 1:
		return false
	var error = catalog._errors[0]
	return (
		error.code == "asset_enumeration_failed" and
		error.category == "Terrain" and
		error.message.to_utf8().size() <= CatalogScript.MAX_ERROR_MESSAGE_BYTES and
		error.message.find("res://") < 0 and
		error.message.find("must-not-be-reflected") < 0)
