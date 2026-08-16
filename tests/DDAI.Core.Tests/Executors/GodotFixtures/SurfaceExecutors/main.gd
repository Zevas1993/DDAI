extends Node

const BridgeScript = preload("res://ddai_bridge.gd")

class RangeValue:
	extends Reference
	var value = 0.0

class MockTerrain:
	extends Reference
	var textures = []
	var state = Image.new()
	var state_2 = Image.new()
	var ExpandedSlots = true

	func _init():
		state.create(64, 64, false, Image.FORMAT_RGBA8)
		state.fill(Color(1.0, 0.0, 0.0, 0.0))
		state_2.create(64, 64, false, Image.FORMAT_RGBA8)
		state_2.fill(Color(0.0, 0.0, 0.0, 0.0))
		for slot in range(8):
			var texture = ImageTexture.new()
			texture.take_over_path("res://fixture.png" if slot == 3 else "res://terrain-" + str(slot) + ".png")
			textures.append(texture)
	func CloneSplatImage(): return state.duplicate()
	func CloneSplatImage2(): return state_2.duplicate()
	# Dungeondraft 1.2.0.1's GDScript/C# proxy exposes the documented
	# Texture[] property, but its method-return marshalling yields null here.
	func GetTexture(_slot): return null
	func Save(_copy=false): return null
	func WorldToTexture(value): return value / 64.0 + Vector2.ONE * 0.5
	func Paint(_terrain_id, _brush, _offset, _position, _rate):
		state.lock()
		state.set_pixel(1, 1, Color(0.0, 0.0, 0.0, 1.0))
		state.unlock()
	func RestoreSplat(value): state = value.duplicate()
	func RestoreSplat2(value, second):
		state = value.duplicate()
		state_2 = second.duplicate()

class MockTerrainTool:
	extends Reference
	var Enabled = false
	var BrushRadius = 64.0
	var IsPainting = false
	var brush = Image.new()
	var Size = 1.0
	var Intensity = 1.0
	var TerrainID = 0
	func _init():
		brush.create(1, 1, false, Image.FORMAT_RGBA8)
		brush.fill(Color.white)
	func SetSize(value):
		Size = value
		BrushRadius = Size * 64.0
	func GetBrushRadius(): return BrushRadius
	func Enable(): Enabled = true
	func Disable(): Enabled = false
	func _ContentInput(event): IsPainting = event.pressed
	func _Update(_delta):
		if IsPainting:
			Global.World.level.Terrain.Paint(TerrainID, brush, Vector2.ZERO, Global.WorldUI.MousePosition, Intensity)

class MockPatternNode:
	extends Node
	var GlobalRect = Rect2(70, 70, 70, 70)

class MockPatterns:
	extends Reference
	var shapes = []
	func GetShapes(): return shapes.duplicate()
	func DrawPolygon(_points, _invert):
		var node = MockPatternNode.new()
		shapes.append(node)
		Global.World.add_child(node)

class MockPatternTool:
	extends Reference
	var Texture = null
	var pattern_color = Color.white
	var Rotation = RangeValue.new()
	var ActiveLayer = 100
	var isDrawing = false
	var isDragging = false
	func SetLayer(value): ActiveLayer = value
	func _get(property): return pattern_color if property == "Color" else null
	func _set(property, value):
		if property == "Color":
			pattern_color = value
			return true
		return false
	func ChangeColor(value, _name): pattern_color = value

class MockCave:
	extends Reference
	var CaveFloor = ImageTexture.new()
	var GroundColor = Color.white
	var WallColor = Color.white
	var IsDrawing = false
	var IsMeshWorkerBusy = false
	var bitmap = BitMap.new()
	func _init():
		bitmap.create(Vector2(64, 64))
		CaveFloor.take_over_path("res://initial-cave.png")
	func SetGroundColor(value): GroundColor = value
	func SetWallColor(value): WallColor = value
	func SetFloorTexture(value): CaveFloor = value
	func OnDrawingBegin(): IsDrawing = true
	func SetCircle(position, _size, value): bitmap.set_bit(Vector2(int(position.x) % 64, int(position.y) % 64), value)
	func OnDrawingEnd(): IsDrawing = false
	func SetBitmap(value): bitmap = value.duplicate()

class MockCaveTool:
	extends Reference
	func ChangeTexture(value, _name): Global.World.level.CaveMesh.CaveFloor = value

class MockRoofNode:
	extends Node
	var GlobalRect = Rect2(70, 70, 140, 140)

class MockRoofs:
	extends Node

class MockRoofTool:
	extends Reference
	var Texture = null
	var Width = RangeValue.new()
	var Shade = false
	var ShadeContrast = RangeValue.new()
	var Mode = 0
	var isDrawing = false
	func SetShade(value): Shade = value
	func DrawRect(_rect): Global.World.create_roof()
	func FinishShape(): Global.World.create_roof()

class MockProp:
	extends Node2D
	var Rect = Rect2()
	var SelectRect = Rect2()
	var HasShadow = false
	var recorded = false
	func SetTexture(_value): pass
	func SetBlockLight(_value): pass
	func SetCustomColor(_value): pass

class MockObjects:
	extends Node
	var search_table = {}
	func CreateObject(_sorting):
		var prop = MockProp.new()
		add_child(prop)
		return prop
	func AddToSearchTable(prop, _refresh): search_table[prop] = true
	func RemoveFromSearchTable(prop): search_table.erase(prop)
	func Save():
		var saved = []
		for prop in get_children():
			if prop.recorded:
				saved.append({"node_id": int(prop.get_meta("node_id")), "layer": prop.z_index})
		return saved

class MockObjectTool:
	extends Reference
	var record_calls = 0
	func Record(prop):
		record_calls += 1
		if not prop.has_meta("node_id"):
			Global.World.AssignNodeID(prop)
		prop.recorded = true

class MockLight:
	extends Light2D
	func _exit_tree():
		if has_meta("node_id"):
			# Dungeondraft's native node registry does not necessarily unregister a
			# queued node before the current bridge transition returns. Mirror that
			# frame boundary so reversal must rely on its separate observation step.
			Global.World.pending_registry_erases.append(int(get_meta("node_id")))

class MockLights:
	extends Node
	var create_light_calls = 0
	func CreateLight(_show_widget=false):
		create_light_calls += 1
		var light = MockLight.new()
		light.set_meta("preview", true)
		add_child(light)
		return light
	func Save():
		var saved = []
		for light in get_children():
			if not bool(light.get_meta("preview")):
				saved.append({
					"node_id": int(light.get_meta("node_id")),
					"position": light.position,
					"range": light.texture_scale / 512.0 * float(light.texture.get_width()),
					"intensity": light.energy,
					"color": light.color,
					"shadows": light.shadow_enabled,
				})
		return saved

class MockLightTool:
	extends Reference
	var texture = null
	var Range = RangeValue.new()
	var Intensity = 1.0
	var light_color = Color.white
	var Shadows = false
	var preview = null
	func _get(property):
		return light_color if property == "Color" else null

class MockSelectTool:
	extends Reference
	var selected = []
	var Selected = selected
	func DeselectAll(): selected.clear()
	func SelectThing(node, _value):
		selected.append(node)
		return node
	func Delete():
		for node in selected:
			Global.World.registry.erase(int(node.get_meta("node_id")))
			Global.World.level.PatternShapes.shapes.erase(node)
			if node.get_parent() == Global.World.level.Roofs:
				Global.World.level.Roofs.remove_child(node)
			node.free()
		selected.clear()

class MockLevel:
	extends Reference
	var ID = 0
	var Terrain = MockTerrain.new()
	var PatternShapes = MockPatterns.new()
	var CaveMesh = MockCave.new()
	var Roofs = MockRoofs.new()
	var Objects = MockObjects.new()
	var Lights = MockLights.new()

class MockWorld:
	extends Node
	var GridSize = 64.0
	var Width = 1
	var Height = 1
	var level = MockLevel.new()
	var levels = [level]
	var registry = {}
	var pending_registry_erases = []
	var delete_node_calls = 0
	var next_id = 10
	func _init():
		add_child(level.Roofs)
		add_child(level.Objects)
		add_child(level.Lights)
	func GetLevelByID(value): return level if int(value) == 0 else null
	func AssignNodeID(node):
		next_id += 1
		node.set_meta("node_id", next_id)
		registry[next_id] = node
		return next_id
	func HasNodeID(value): return registry.has(int(value))
	func GetNodeByID(value): return registry.get(int(value), null)
	func DeleteNodeByID(value):
		var node_id = int(value)
		if not registry.has(node_id):
			return false
		var node = registry[node_id]
		if node == null or node.get_parent() == null:
			return false
		delete_node_calls += 1
		node.get_parent().remove_child(node)
		registry.erase(node_id)
		node.queue_free()
		return true
	func FlushPendingRegistryErases():
		for node_id in pending_registry_erases:
			registry.erase(int(node_id))
		pending_registry_erases.clear()
	func create_roof():
		var node = MockRoofNode.new()
		level.Roofs.add_child(node)

class MockEditor:
	extends Reference
	var ActiveToolName = ""
	var Tools = {"TerrainBrush": MockTerrainTool.new(), "PatternShapeTool": MockPatternTool.new(), "CaveBrush": MockCaveTool.new(), "RoofTool": MockRoofTool.new(), "ObjectTool": MockObjectTool.new(), "LightTool": MockLightTool.new(), "SelectTool": MockSelectTool.new()}

class MockWorldUI:
	extends Reference
	var EditArcPoint = false
	var Polyline = []
	var MousePosition = Vector2.ZERO
	func ClearPolyline(): Polyline.clear()
	func AddPolyPoint(value): Polyline.append(value)

func _ready():
	Global.World = MockWorld.new()
	add_child(Global.World)
	Global.Editor = MockEditor.new()
	Global.WorldUI = MockWorldUI.new()
	var bridge = BridgeScript.new()
	bridge._texture_loader = funcref(self, "_texture")
	bridge._ensure_mailbox_directories()
	# This fixture proves the surface families stay withheld. It deliberately does
	# not pin the whole certified list, so certifying an unrelated operation such
	# as object_placement does not falsely fail the surface capability check.
	var certified_types = bridge._certified_operation_types()
	var capability_withheld = true
	for withheld_type in ["terrain_stroke", "pattern_region", "colorable_pattern_region", "cave_region", "roof_region"]:
		if certified_types.has(withheld_type):
			capability_withheld = false
	var tool_state = _tool_state(Global.Editor)
	print("DDAI_STAGE:TERRAIN_BEGIN")
	var terrain_ok = _terrain(bridge)
	print("DDAI_STAGE:TERRAIN_END:" + str(terrain_ok))
	print("DDAI_STAGE:PATTERN_BEGIN")
	var pattern_ok = _pattern(bridge, false)
	print("DDAI_STAGE:PATTERN_END:" + str(pattern_ok))
	var color_pattern_ok = _pattern(bridge, true)
	print("DDAI_STAGE:CAVE_BEGIN")
	var cave_ok = _cave(bridge)
	print("DDAI_STAGE:CAVE_END:" + str(cave_ok))
	print("DDAI_STAGE:ROOF_BEGIN")
	var roof_ok = _roof(bridge)
	print("DDAI_STAGE:ROOF_END:" + str(roof_ok))
	var object_persistence_ok = _object_persistence(bridge)
	var light_persistence_ok = _light_persistence(bridge)
	var light_numeric_bounds_ok = _light_numeric_bounds(bridge)
	var light_fingerprint_ok = _light_fingerprint_fields(bridge)
	var state_restored = tool_state == _tool_state(Global.Editor)
	var tamper_ok = _rollback_tamper(bridge)
	print("DDAI_SURFACE_TERRAIN_FAILS_CLOSED:" + str(terrain_ok))
	print("DDAI_SURFACE_PATTERN:" + str(pattern_ok))
	print("DDAI_SURFACE_COLORABLE_PATTERN:" + str(color_pattern_ok))
	print("DDAI_SURFACE_CAVE:" + str(cave_ok))
	print("DDAI_SURFACE_ROOF:" + str(roof_ok))
	print("DDAI_SURFACE_TOOL_STATE:" + str(state_restored))
	print("DDAI_SURFACE_ROLLBACK_TAMPER:" + str(tamper_ok))
	print("DDAI_SURFACE_CAPABILITY_WITHHELD:" + str(capability_withheld))
	print("DDAI_OBJECT_SAVE_PERSISTENCE:" + str(object_persistence_ok))
	print("DDAI_LIGHT_SAVE_PERSISTENCE:" + str(light_persistence_ok))
	print("DDAI_LIGHT_NUMERIC_BOUNDS:" + str(light_numeric_bounds_ok))
	print("DDAI_LIGHT_FINGERPRINT_FIELDS:" + str(light_fingerprint_ok))
	get_tree().quit(0 if terrain_ok and pattern_ok and color_pattern_ok and cave_ok and roof_ok and state_restored and tamper_ok and capability_withheld and object_persistence_ok and light_persistence_ok and light_numeric_bounds_ok and light_fingerprint_ok else 1)

func _operation(kind, id):
	var base = {"operation_type": kind, "operation_id": id, "level_id": "0", "asset_ref": "sha256:" + "a".repeat(64)}
	if kind == "terrain_stroke": base.merge({"path":{"points":[{"x":1.0,"y":1.0},{"x":2.0,"y":2.0}]},"width":1.0,"strength":0.5})
	elif kind in ["pattern_region","colorable_pattern_region"]: base.merge({"region":{"points":[{"x":1.0,"y":1.0},{"x":3.0,"y":1.0},{"x":3.0,"y":3.0}]},"rotation_degrees":15.0,"layer":100.0})
	elif kind == "cave_region": base.merge({"region":{"points":[{"x":1.0,"y":1.0},{"x":3.0,"y":1.0},{"x":3.0,"y":3.0}]},"floor_color_rgba":"#112233ff","wall_color_rgba":"#445566ff"})
	elif kind == "roof_region": base.merge({"region":{"points":[{"x":1.0,"y":1.0},{"x":3.0,"y":1.0},{"x":3.0,"y":3.0},{"x":1.0,"y":3.0}]},"width":1.0,"shade":0.5})
	elif kind == "object_placement": base.merge({"position":{"x":2.0,"y":2.0},"rotation_degrees":0.0,"scale":1.0,"layer":100,"sorting":"over","shadow":true,"block_light":false,"custom_color_rgba":null})
	elif kind == "light_placement": base.merge({"position":{"x":3.0,"y":4.0},"range":6.0,"intensity":0.75,"color_rgba":"#ffeeddcc","shadows":true})
	if kind == "colorable_pattern_region": base["color_rgba"] = "#aabbccdd"
	return base

func _resolve(bridge, key, operation): bridge._resolved_job_assets[key] = {operation.operation_id:"res://fixture.png"}
func _texture(identity):
	var image = Image.new()
	image.create(512, 512, false, Image.FORMAT_RGBA8)
	image.fill(Color.white)
	var texture = ImageTexture.new()
	texture.create_from_image(image)
	texture.take_over_path(identity)
	return texture
func _tool_state(editor): return str(editor.Tools["TerrainBrush"].Enabled)+"|"+str(editor.Tools["TerrainBrush"].Size)+"|"+str(editor.Tools["PatternShapeTool"].ActiveLayer)+"|"+str(editor.Tools["RoofTool"].Mode)
func _terrain(bridge):
	var op=_operation("terrain_stroke","terrain")
	var key="terrain-key"
	_resolve(bridge,key,op)
	var before=bridge._terrain_state_hash(Global.World.level.Terrain)
	var result=bridge._execute_terrain_stroke(op,key,{"width":16.0,"height":16.0})
	var current=bridge._terrain_state_hash(Global.World.level.Terrain)
	var evidence=bridge._read_bounded_dictionary("user://ddai/failed/"+key+".surface-stage.json")
	return not result.ok and not result.get("untracked_change",true) and current==before and evidence!=null and evidence.stage=="native_state_managed_adapter_required"

func _pattern(bridge,colorable):
	var kind="colorable_pattern_region" if colorable else "pattern_region"
	var op=_operation(kind,kind)
	var key=kind+"-key"
	_resolve(bridge,key,op)
	var result=bridge._execute_pattern_region(op,key)
	var observed=bridge._observe_operation(op,result.node_ids,key)=="observed"
	var reversed=bridge._reverse_operation(op,result.node_ids,key) and bridge._observe_reversal(op,result.node_ids,key)
	return result.ok and observed and reversed

func _cave(bridge):
	var op=_operation("cave_region","cave")
	var key="cave-key"
	_resolve(bridge,key,op)
	var before=bridge._cave_state_hash(Global.World.level.CaveMesh)
	var result=bridge._execute_cave_region(op,key)
	var observed=bridge._observe_operation(op,result.node_ids,key)=="observed"
	var reversed_called=bridge._reverse_operation(op,result.node_ids,key)
	var reversed_observed=bridge._observe_reversal(op,result.node_ids,key)
	var reversed=reversed_called and reversed_observed
	var current=bridge._cave_state_hash(Global.World.level.CaveMesh)
	return result.ok and observed and reversed and current==before

func _roof(bridge):
	var op=_operation("roof_region","roof")
	var key="roof-key"
	_resolve(bridge,key,op)
	var result=bridge._execute_roof_region(op,key)
	var observed=bridge._observe_operation(op,result.node_ids,key)=="observed"
	Global.Editor.Tools["SelectTool"].selected.clear()
	var reversed=bridge._reverse_operation(op,result.node_ids,key) and bridge._observe_reversal(op,result.node_ids,key)
	return result.ok and observed and reversed

func _object_persistence(bridge):
	var op = _operation("object_placement", "object")
	var key = "object-persistence-key"
	_resolve(bridge, key, op)
	var result = bridge._execute_object_placement(op, key)
	var saved = Global.World.level.Objects.Save()
	return result.ok and result.node_ids.size() == 1 and saved.size() == 1 and int(saved[0].node_id) == int(result.node_ids[0]) and saved[0].layer == 100 and Global.Editor.Tools["ObjectTool"].record_calls == 1

func _light_persistence(bridge):
	var op = _operation("light_placement", "light")
	var key = "light-persistence-key"
	_resolve(bridge, key, op)
	var tool = Global.Editor.Tools["LightTool"]
	var before = str(tool.texture) + "|" + str(tool.Range.value) + "|" + str(tool.Intensity) + "|" + str(tool.Color) + "|" + str(tool.Shadows) + "|" + str(tool.preview)
	var result = bridge._execute_light_placement(op, key)
	var observed = result.ok and bridge._observe_operation(op, result.node_ids, key) == "observed"
	var saved = Global.World.level.Lights.Save()
	var persisted_color = saved[0].color if saved.size() == 1 else Color.black
	var persisted = saved.size() == 1 and int(saved[0].node_id) == int(result.node_ids[0]) and saved[0].position == Vector2(192.0, 256.0) and is_equal_approx(float(saved[0].range), 6.0) and is_equal_approx(float(saved[0].intensity), 0.75) and is_equal_approx(persisted_color.r, 1.0) and is_equal_approx(persisted_color.g, 238.0 / 255.0) and is_equal_approx(persisted_color.b, 221.0 / 255.0) and is_equal_approx(persisted_color.a, 204.0 / 255.0) and saved[0].shadows
	var reverse_started = observed and bridge._reverse_operation(op, result.node_ids, key)
	Global.World.FlushPendingRegistryErases()
	var reversed = reverse_started and bridge._observe_reversal(op, result.node_ids, key)
	var after = str(tool.texture) + "|" + str(tool.Range.value) + "|" + str(tool.Intensity) + "|" + str(tool.Color) + "|" + str(tool.Shadows) + "|" + str(tool.preview)
	return persisted and reversed and Global.World.level.Lights.get_child_count() == 0 and Global.World.delete_node_calls == 1 and before == after

func _light_numeric_bounds(bridge):
	var lights = Global.World.level.Lights
	var before_children = lights.get_child_count()
	var before_calls = lights.create_light_calls
	var overflow = _operation("light_placement", "light-overflow")
	overflow.range = 1.0e37
	_resolve(bridge, "light-overflow-key", overflow)
	var overflow_result = bridge._execute_light_placement(overflow, "light-overflow-key")
	var range_underflow = _operation("light_placement", "light-range-underflow")
	range_underflow.range = 1.0e-300
	_resolve(bridge, "light-range-underflow-key", range_underflow)
	var range_underflow_result = bridge._execute_light_placement(range_underflow, "light-range-underflow-key")
	var intensity_underflow = _operation("light_placement", "light-intensity-underflow")
	intensity_underflow.intensity = 1.0e-300
	_resolve(bridge, "light-intensity-underflow-key", intensity_underflow)
	var intensity_underflow_result = bridge._execute_light_placement(intensity_underflow, "light-intensity-underflow-key")
	return not overflow_result.ok and not range_underflow_result.ok and not intensity_underflow_result.ok and lights.get_child_count() == before_children and lights.create_light_calls == before_calls

func _light_fingerprint_fields(bridge):
	var operation = _operation("light_placement", "light-fingerprint")
	var plan = {
		"schema_version":"2.0", "request_id":"light-fingerprint-001",
		"expected_map_id":"map-live", "base_revision":0.0,
		"expected_catalog_revision":1.0, "mode":"add",
		"coordinate_system":"grid", "canvas":{"width":40.0,"height":30.0},
		"operations":[operation],
	}
	var text = bridge._universal_plan_fingerprint_input(plan)
	print("DDAI_LIGHT_PLAN_FINGERPRINT:" + text.sha256_text())
	for field in [".position.x=", ".position.y=", ".range=", ".intensity=", ".color_rgba=", ".shadows=true"]:
		if text.find(field) < 0:
			return false
	return text.find(".region.") < 0 and text.sha256_text().length() == 64

func _rollback_tamper(bridge):
	var op=_operation("cave_region","tamper")
	var key="tamper-key"
	_resolve(bridge,key,op)
	var result=bridge._execute_cave_region(op,key)
	if not result.ok:
		return false
	var directory=bridge._surface_rollback_directory(key,op.operation_id)
	var record=bridge._read_bounded_dictionary(directory+"/rollback.json")
	if record==null:
		return false
	var path=directory+"/primary."+("png" if record.primary_format=="png" else "res")
	var file=File.new()
	file.open(path,File.WRITE)
	file.store_string("tampered")
	file.close()
	return result.ok and not bridge._reverse_operation(op,result.node_ids,key)
