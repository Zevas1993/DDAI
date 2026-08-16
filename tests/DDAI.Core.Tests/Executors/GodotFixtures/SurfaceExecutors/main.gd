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

class MockWorld:
	extends Node
	var GridSize = 64.0
	var Width = 1
	var Height = 1
	var level = MockLevel.new()
	var levels = [level]
	var registry = {}
	var next_id = 10
	func _init(): add_child(level.Roofs)
	func GetLevelByID(value): return level if int(value) == 0 else null
	func AssignNodeID(node):
		next_id += 1
		node.set_meta("node_id", next_id)
		registry[next_id] = node
		return next_id
	func HasNodeID(value): return registry.has(int(value))
	func GetNodeByID(value): return registry.get(int(value), null)
	func create_roof():
		var node = MockRoofNode.new()
		level.Roofs.add_child(node)

class MockEditor:
	extends Reference
	var ActiveToolName = ""
	var Tools = {"TerrainBrush": MockTerrainTool.new(), "PatternShapeTool": MockPatternTool.new(), "CaveBrush": MockCaveTool.new(), "RoofTool": MockRoofTool.new(), "SelectTool": MockSelectTool.new()}

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
	get_tree().quit(0 if terrain_ok and pattern_ok and color_pattern_ok and cave_ok and roof_ok and state_restored and tamper_ok and capability_withheld else 1)

func _operation(kind, id):
	var base = {"operation_type": kind, "operation_id": id, "level_id": "0", "asset_ref": "sha256:" + "a".repeat(64)}
	if kind == "terrain_stroke": base.merge({"path":{"points":[{"x":1.0,"y":1.0},{"x":2.0,"y":2.0}]},"width":1.0,"strength":0.5})
	elif kind in ["pattern_region","colorable_pattern_region"]: base.merge({"region":{"points":[{"x":1.0,"y":1.0},{"x":3.0,"y":1.0},{"x":3.0,"y":3.0}]},"rotation_degrees":15.0,"layer":100.0})
	elif kind == "cave_region": base.merge({"region":{"points":[{"x":1.0,"y":1.0},{"x":3.0,"y":1.0},{"x":3.0,"y":3.0}]},"floor_color_rgba":"#112233ff","wall_color_rgba":"#445566ff"})
	elif kind == "roof_region": base.merge({"region":{"points":[{"x":1.0,"y":1.0},{"x":3.0,"y":1.0},{"x":3.0,"y":3.0},{"x":1.0,"y":3.0}]},"width":1.0,"shade":0.5})
	if kind == "colorable_pattern_region": base["color_rgba"] = "#aabbccdd"
	return base

func _resolve(bridge, key, operation): bridge._resolved_job_assets[key] = {operation.operation_id:"res://fixture.png"}
func _texture(identity):
	var texture = ImageTexture.new()
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
