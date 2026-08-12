extends Node

const BridgeScript = preload("res://ddai_bridge.gd")
const OversizedBridgeScript = preload("res://oversized_bridge.gd")


class FakeItem:
	extends Node2D

	var _node_id = 0
	var _global_rect = Rect2()
	var mutation_calls = 0
	var GlobalRect setget _set_global_rect, _get_global_rect

	func _init(node_id, global_rect):
		_node_id = node_id
		_global_rect = global_rect
		set_meta("node_id", int(node_id))

	func GetNodeID():
		return null

	func _get_global_rect():
		return _global_rect

	func _set_global_rect(value):
		mutation_calls += 1
		_global_rect = value

	func test_move(value):
		_global_rect = value

	func Save():
		mutation_calls += 1
		return {}

	func Set(_value):
		mutation_calls += 1


class FakeContainer:
	extends Node2D

	var mutation_calls = 0

	func Save():
		mutation_calls += 1
		return []

	func Delete(_value):
		mutation_calls += 1


class FakePatternShapes:
	extends FakeContainer

	var shapes = []
	var get_shapes_calls = 0

	func GetShapes():
		get_shapes_calls += 1
		return shapes


class FakeSprite:
	var texture = null

	func _init(value):
		texture = value


class FakeProp:
	extends Node2D

	var _node_id = 0
	var _rect = Rect2()
	var Sprite = null
	var mutation_calls = 0
	var Rect setget _set_rect, _get_rect

	func _init(node_id, rect, texture):
		_node_id = node_id
		_rect = rect
		Sprite = FakeSprite.new(texture)
		set_meta("node_id", int(node_id))

	func GetNodeID():
		return null

	func _get_rect():
		return _rect

	func _set_rect(value):
		mutation_calls += 1
		_rect = value

	func test_move(value):
		_rect = value


class FakeLevel:
	var ID = 3
	var Label = "Ground"
	var Walls = FakeContainer.new()
	var Pathways = FakeContainer.new()
	var Roofs = FakeContainer.new()
	var PatternShapes = FakePatternShapes.new()
	var Objects = FakeContainer.new()
	var mutation_calls = 0

	func Save():
		mutation_calls += 1
		return {}


class FakeWorld:
	extends Node

	var Title = "Harness Map"
	var Width = 40
	var Height = 30
	var GridSize = 10
	var CurrentLevelId = 3
	var levels = []
	var mutation_calls = 0

	func GetLevelByID(level_id):
		for level in levels:
			if level.ID == level_id:
				return level
		return null

	func Save():
		mutation_calls += 1
		return {}

	func DeleteLevel(_level):
		mutation_calls += 1


var bridge = null
var world = null
var level = null
var nodes = []


func _ready():
	_setup()
	var pagination = _test_exact_stable_pagination_and_pattern_layers()
	var stale = _test_filter_move_and_delete_invalidate_cursors()
	var bounds = _test_bounds_and_response_size_fail_closed()
	var cursor_vector = _test_cross_runtime_cursor_vector()
	var map_identity = _test_runtime_world_identity_prevents_map_id_collision()
	var object_correlation = _test_documented_object_fingerprint_is_internal_and_read_only()
	var node_id_diagnostic = _test_node_id_boundary_diagnostic_is_bounded()
	var read_only = _mutation_count() == 0
	print("DDAI_INSPECTION_PAGINATION:", pagination)
	print("DDAI_INSPECTION_STALE_CURSOR:", stale)
	print("DDAI_INSPECTION_BOUNDS_AND_CAP:", bounds)
	print("DDAI_INSPECTION_CURSOR_VECTOR:", cursor_vector)
	print("DDAI_INSPECTION_MAP_IDENTITY:", map_identity)
	print("DDAI_INSPECTION_OBJECT_CORRELATION:", object_correlation)
	print("DDAI_INSPECTION_NODE_ID_DIAGNOSTIC:", node_id_diagnostic)
	print("DDAI_INSPECTION_READ_ONLY:", read_only)
	var exit_code = 0 if pagination and stale and bounds and cursor_vector and map_identity and object_correlation and node_id_diagnostic and read_only else 1
	Global.World = null
	for node in nodes:
		if is_instance_valid(node) and node.get_parent() == null:
			node.free()
	world.free()
	get_tree().quit(exit_code)


func _setup():
	bridge = BridgeScript.new()
	bridge._session_id = "behavior-session"
	world = FakeWorld.new()
	add_child(world)
	level = FakeLevel.new()
	world.levels = [level]
	Global.World = world
	world.add_child(level.Walls)
	world.add_child(level.Pathways)
	world.add_child(level.Roofs)
	world.add_child(level.PatternShapes)
	world.add_child(level.Objects)

	for index in range(6):
		nodes.append(FakeItem.new(index + 1, Rect2(index * 10, 0, 10, 10)))
	level.Walls.add_child(nodes[0])
	level.Walls.add_child(nodes[1])
	level.Pathways.add_child(nodes[2])
	level.Roofs.add_child(nodes[3])
	var pattern_layer = Node2D.new()
	level.PatternShapes.add_child(pattern_layer)
	pattern_layer.add_child(nodes[4])
	pattern_layer.add_child(nodes[5])
	level.PatternShapes.shapes = [nodes[4], nodes[5]]


func _test_documented_object_fingerprint_is_internal_and_read_only():
	var texture = ImageTexture.new()
	texture.set_path("res://private/objects/ritual-altar.png")
	var prop = FakeProp.new(7, Rect2(10, 20, 20, 10), texture)
	nodes.append(prop)
	level.Objects.add_child(prop)
	var first = bridge._inspect_map_payload(_query(10))
	if not first.ok:
		return false
	var object_item = null
	for item in first.payload.items:
		if item.node_id == 7:
			object_item = item
	if object_item == null or object_item.kind != "object" or object_item.asset_ref != null:
		return false
	if object_item.resource_fingerprint != texture.resource_path.sha256_text():
		return false
	var encoded = to_json(first.payload)
	if encoded.find("res://") >= 0 or encoded.find("ritual-altar") >= 0 or first.payload.unsupported_kinds.has("object"):
		return false
	prop.test_move(Rect2(20, 20, 20, 10))
	var moved = bridge._inspect_map_payload(_query(10))
	prop.test_move(Rect2(10, 20, 20, 10))
	level.Objects.remove_child(prop)
	return moved.ok and moved.payload.map_revision != first.payload.map_revision


func _test_node_id_boundary_diagnostic_is_bounded():
	var hostile = FakeItem.new(8, Rect2(0, 0, 10, 10))
	hostile.set_meta("node_id", {"private_path": "C:/Users/secret/map"})
	var result = bridge._inspection_item(hostile, "wall", 3, 10.0)
	hostile.free()
	if result.ok or result.error.code != "inspection_state_invalid":
		return false
	if result.error.message != "A native wall has an invalid node ID (type_code=18, reason=unsupported_type)." or result.error.message.find("private_path") >= 0 or result.error.message.length() > 128:
		return false
	for value in [1.0, "1", "+1", "-1", " 1", "1 ", "01", "１", -1, 9007199254740992]:
		if bridge._runtime_node_id(value).ok:
			return false
	for value in [0, 1, 9007199254740991]:
		var normalized = bridge._runtime_node_id(value)
		if not normalized.ok or normalized.value != value:
			return false
	return true


func _query(limit, cursor = null, region = null):
	return {"region": region, "level": 3.0, "limit": float(limit), "cursor": cursor}


func _test_exact_stable_pagination_and_pattern_layers():
	var first = bridge._inspect_map_payload(_query(2))
	if not first.ok or not _page_ids_equal(first.payload, [1, 2]) or not first.payload.truncated:
		return false
	if typeof(first.payload.map_revision) != TYPE_STRING or first.payload.map_revision.length() != 64:
		return false
	var repeat = bridge._inspect_map_payload(_query(2))
	if not repeat.ok or repeat.payload.map_revision != first.payload.map_revision or repeat.payload.next_cursor != first.payload.next_cursor:
		return false
	level.Walls.move_child(nodes[0], 1)
	var reordered = bridge._inspect_map_payload(_query(2))
	if not reordered.ok or reordered.payload.map_revision == first.payload.map_revision or not _page_ids_equal(reordered.payload, [2, 1]):
		return false
	level.Walls.move_child(nodes[0], 0)
	var restored = bridge._inspect_map_payload(_query(2))
	if not restored.ok or restored.payload.map_revision != first.payload.map_revision:
		return false
	var original_label = level.Label
	level.Label = "Renamed"
	var renamed = bridge._inspect_map_payload(_query(2))
	level.Label = original_label
	if not renamed.ok or renamed.payload.map_revision == first.payload.map_revision:
		return false
	var second = bridge._inspect_map_payload(_query(2, first.payload.next_cursor))
	if not second.ok or not _page_ids_equal(second.payload, [3, 4]) or not second.payload.truncated:
		return false
	var third = bridge._inspect_map_payload(_query(2, second.payload.next_cursor))
	if not third.ok or not _page_ids_equal(third.payload, [5, 6]) or third.payload.truncated or third.payload.next_cursor != null:
		return false
	return level.PatternShapes.get_shapes_calls >= 3


func _test_filter_move_and_delete_invalidate_cursors():
	var first = bridge._inspect_map_payload(_query(2))
	if not first.ok:
		return false
	var changed_filter = bridge._inspect_map_payload(_query(
		2,
		first.payload.next_cursor,
		{"x": 0.0, "y": 0.0, "width": 1.5, "height": 2.0}))
	if changed_filter.ok or changed_filter.error.code != "invalid_cursor":
		return false

	var original_revision = first.payload.map_revision
	nodes[2].test_move(Rect2(200, 0, 10, 10))
	var stale_after_move = bridge._inspect_map_payload(_query(2, first.payload.next_cursor))
	var moved = bridge._inspect_map_payload(_query(2))
	if stale_after_move.ok or stale_after_move.error.code != "invalid_cursor" or not moved.ok or moved.payload.map_revision == original_revision:
		return false

	var moved_cursor = moved.payload.next_cursor
	level.Walls.remove_child(nodes[1])
	var stale_after_delete = bridge._inspect_map_payload(_query(2, moved_cursor))
	var deleted = bridge._inspect_map_payload(_query(2))
	return not stale_after_delete.ok and stale_after_delete.error.code == "invalid_cursor" and deleted.ok and deleted.payload.map_revision != moved.payload.map_revision


func _test_bounds_and_response_size_fail_closed():
	var original_grid_size = world.GridSize
	world.GridSize = "10"
	var invalid_grid = bridge._inspect_map_payload(_query(2))
	world.GridSize = original_grid_size
	if invalid_grid.ok or invalid_grid.error.code != "map_not_available":
		return false

	var original_rect = nodes[0]._global_rect
	nodes[0].test_move(Rect2(0, 0, 0, 0))
	var invalid_bounds = bridge._inspect_map_payload(_query(2))
	nodes[0].test_move(original_rect)
	if invalid_bounds.ok or invalid_bounds.error.code != "inspection_state_invalid":
		return false

	var original_label = level.Label
	var oversized_label = "x"
	for _index in range(20):
		oversized_label += oversized_label
	level.Label = oversized_label
	var response = bridge._inspect_map_response({
		"request_id": "behavior-oversize",
		"command": "inspect_map",
		"payload": _query(2),
	})
	level.Label = original_label
	if response.success or response.error.code != "inspection_state_invalid" or to_json(response).to_utf8().size() > BridgeScript.MAXIMUM_MESSAGE_BYTES:
		return false

	# This subclass supplies only the walker result so the inherited production
	# response builder itself is exercised at its exact 1 MiB fail-closed branch.
	var oversized_bridge = OversizedBridgeScript.new()
	var capped = oversized_bridge._inspect_map_response({
		"request_id": "behavior-response-cap",
		"command": "inspect_map",
		"payload": _query(2),
	})
	return not capped.success and capped.error.code == "response_too_large" and to_json(capped).to_utf8().size() <= BridgeScript.MAXIMUM_MESSAGE_BYTES


func _test_cross_runtime_cursor_vector():
	var cursor = bridge._inspection_cursor_value(
		"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
		3,
		{"x": 1.0, "y": 2.0, "width": 10.0, "height": 8.0},
		5)
	return cursor == "9da31f0d36cb0547a1cf21211cc2d894fd9da2de4a94385331a7f156361d5265:5"


func _test_runtime_world_identity_prevents_map_id_collision():
	var first = bridge._inspect_map_payload(_query(1))
	if not first.ok:
		return false
	var other_world = FakeWorld.new()
	var other_level = FakeLevel.new()
	other_world.levels = [other_level]
	add_child(other_world)
	other_world.add_child(other_level.Walls)
	other_world.add_child(other_level.Pathways)
	other_world.add_child(other_level.Roofs)
	other_world.add_child(other_level.PatternShapes)
	other_world.add_child(other_level.Objects)
	Global.World = other_world
	var second = bridge._inspect_map_payload(_query(1))
	Global.World = world
	other_world.free()
	return second.ok and second.payload.map_id != first.payload.map_id


func _page_ids_equal(page, expected):
	if page.items.size() != expected.size():
		return false
	for index in range(expected.size()):
		if page.items[index].node_id != expected[index]:
			return false
	return true


func _mutation_count():
	var total = world.mutation_calls + level.mutation_calls
	total += level.Walls.mutation_calls + level.Pathways.mutation_calls
	total += level.Roofs.mutation_calls + level.PatternShapes.mutation_calls
	total += level.Objects.mutation_calls
	for node in nodes:
		total += node.mutation_calls
	return total
