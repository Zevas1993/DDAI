extends Node

const CertifierScript = preload("res://ddai_operation_certifier.gd")
const EXPECTED_OPERATIONS = [
	"terrain_stroke", "pattern_region", "colorable_pattern_region", "cave_region",
	"roof_region", "object_placement", "wall_polyline", "material_stroke",
	"portal_placement", "path_polyline", "light_placement", "simple_tile_region",
	"smart_tile_region", "smart_tile_double_region",
]
const EXPECTED_DUNGEONDRAFT_SHA256 = "c14ddddbaada43610e763f73f0c2786983ca4440578acde0ac9668cda358af02"

class FakeTool:
	extends Reference
	var mutation_calls = 0
	var isDrawing = false
	var IsPainting = false
	var isPainting = false
	var isDragging = false
	var IsMeshWorkerBusy = false
	var Texture = null
	var texture = null
	var Preview = null
	var preview = null
	var ActivePath = null
	var Mesh = null
	var brush = Image.new()
	var ActiveLayer = 0
	var ActiveToolName = ""

	func Enable(): mutation_calls += 1
	func Disable(): mutation_calls += 1
	func EndWall(_closed): mutation_calls += 1
	func Confirm(): mutation_calls += 1
	func StartPath(): mutation_calls += 1
	func EndPath(_loop): mutation_calls += 1
	func CreatePreview(): mutation_calls += 1
	func SetMaterial(_texture): mutation_calls += 1
	func SetBiome(_index): mutation_calls += 1
	func SetSize(_value): mutation_calls += 1
	func UpdateBrush(): mutation_calls += 1
	func SetLayer(_index): mutation_calls += 1
	func SetSorting(_sorting): mutation_calls += 1
	func SetShadow(_value): mutation_calls += 1
	func SetBlockLight(_value): mutation_calls += 1
	func SetSmooth(_value): mutation_calls += 1
	func FindBestLocation(_position): mutation_calls += 1
	func ChangeTexture(_texture, _property): mutation_calls += 1
	func ChangeColor(_color, _name): mutation_calls += 1
	func SetShadows(_shadows): mutation_calls += 1
	func DrawRect(_rect): mutation_calls += 1
	func FinishShape(): mutation_calls += 1
	func SetFreestanding(_enabled): mutation_calls += 1

class OpaqueProxyTool:
	extends Reference
	var mutation_calls = 0
	func _get_property_list(): return []
	func Enable(): mutation_calls += 1

class FakeContainer:
	extends Reference
	func GetShapes(): return []
	func DrawPolygon(_points, _outline): pass
	func CreateFreestandingPortal(_texture, _position, _closed, _radius, _rotation): pass

class FakeLevel:
	extends Reference
	var Terrain = FakeContainer.new()
	var PatternShapes = FakeContainer.new()
	var CaveMesh = FakeContainer.new()
	var Roofs = FakeContainer.new()
	var Objects = FakeContainer.new()
	var Walls = FakeContainer.new()
	var MaterialMeshes = FakeContainer.new()
	var Portals = FakeContainer.new()
	var Pathways = FakeContainer.new()
	var Lights = FakeContainer.new()
	var FloorShapes = FakeContainer.new()
	var TileMap = FakeContainer.new()

class FakeWorld:
	extends Reference
	var CurrentLevelId = 0
	var level = FakeLevel.new()
	func GetLevelByID(_level_id): return level

class FakeEditor:
	extends Reference
	var ActiveToolName = ""
	var Tools = {}

class FakeWorldUI:
	extends Reference
	var Polyline = []
	var EditArcPoint = false
	func AddPolyPoint(_point): pass
	func ClearPolyline(): pass

func _ready():
	var editor = FakeEditor.new()
	var tool_names = [
		"TerrainBrush", "PatternShapeTool", "CaveBrush", "RoofTool", "ObjectTool",
		"WallTool", "MaterialBrush", "PortalTool", "PathTool", "LightTool", "FloorShapeTool",
	]
	for tool_name in tool_names:
		editor.Tools[tool_name] = FakeTool.new()
	var certifier = CertifierScript.new()
	var runtime_identity = {
		"exact_dungeondraft_version": "1.2.0.1",
		"executable_sha256": EXPECTED_DUNGEONDRAFT_SHA256,
	}
	var records = certifier.certify_runtime(editor, FakeWorld.new(), FakeWorldUI.new(), runtime_identity, ["wall_polyline"])
	var opaque_editor = FakeEditor.new()
	for tool_name in tool_names:
		opaque_editor.Tools[tool_name] = OpaqueProxyTool.new()
	var opaque_records = certifier.certify_runtime(opaque_editor, FakeWorld.new(), FakeWorldUI.new(), runtime_identity, ["wall_polyline"])
	var mismatched_identity_records = certifier.certify_runtime(editor, FakeWorld.new(), FakeWorldUI.new(), {
		"exact_dungeondraft_version": "1.2.0.1",
		"executable_sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
	}, ["wall_polyline"])
	var names = []
	var all_available = true
	var only_wall_certified = true
	var version_bound = true
	for record in records:
		names.append(record.operation_type)
		all_available = all_available and record.route_present and not record.busy
		only_wall_certified = only_wall_certified and bool(record.runtime_certified) == (record.operation_type == "wall_polyline")
		version_bound = version_bound and record.exact_dungeondraft_version == "1.2.0.1"
	names.sort()
	var expected = EXPECTED_OPERATIONS.duplicate()
	expected.sort()
	var mutation_calls = 0
	for tool_name in tool_names:
		mutation_calls += editor.Tools[tool_name].mutation_calls
	var opaque_available = true
	var opaque_wall_certified = false
	for record in opaque_records:
		var method_evidence_matches = record.methods_reflectable == record.required_methods.empty()
		var property_evidence_matches = record.properties_readable == record.required_properties.empty()
		opaque_available = opaque_available and record.route_present and method_evidence_matches and property_evidence_matches
		if record.operation_type == "wall_polyline":
			opaque_wall_certified = record.runtime_certified
	var mismatched_identity_closed = true
	for record in mismatched_identity_records:
		mismatched_identity_closed = mismatched_identity_closed and not record.runtime_certified and record.reason == "runtime_version_mismatch"
	print("DDAI_CERTIFIER_EXACT_FOURTEEN:" + str(records.size() == 14 and names == expected))
	print("DDAI_CERTIFIER_ALL_AVAILABLE:" + str(all_available))
	print("DDAI_CERTIFIER_ONLY_WALL_CERTIFIED:" + str(only_wall_certified))
	print("DDAI_CERTIFIER_VERSION_BOUND:" + str(version_bound))
	print("DDAI_CERTIFIER_READ_ONLY:" + str(mutation_calls == 0))
	print("DDAI_CERTIFIER_OPAQUE_PROXY_AVAILABLE:" + str(opaque_available and opaque_wall_certified))
	print("DDAI_CERTIFIER_MISMATCHED_IDENTITY_CLOSED:" + str(mismatched_identity_closed))
	get_tree().quit(0 if records.size() == 14 and names == expected and all_available and only_wall_certified and version_bound and mutation_calls == 0 and opaque_available and opaque_wall_certified and mismatched_identity_closed else 1)
