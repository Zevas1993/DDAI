extends Reference

var script_class = "tool"

const TARGET_DUNGEONDRAFT_VERSION = "1.2.0.1"
const TARGET_DUNGEONDRAFT_EXECUTABLE_SHA256 = "c14ddddbaada43610e763f73f0c2786983ca4440578acde0ac9668cda358af02"
const DDAI_MAP_DATA_KEY = "org.ddai.status_bridge"
const OPERATION_ROUTES = [
	{"operation_type": "terrain_stroke", "tool_name": "TerrainBrush", "required_methods": ["SetBiome", "SetSize", "UpdateBrush"], "required_properties": ["IsPainting", "brush"], "level_property": "Terrain"},
	{"operation_type": "pattern_region", "tool_name": "PatternShapeTool", "required_methods": [], "required_properties": ["Texture"], "level_property": "PatternShapes"},
	{"operation_type": "colorable_pattern_region", "tool_name": "PatternShapeTool", "required_methods": [], "required_properties": ["Texture"], "level_property": "PatternShapes"},
	{"operation_type": "cave_region", "tool_name": "CaveBrush", "required_methods": [], "required_properties": [], "level_property": "CaveMesh"},
	{"operation_type": "roof_region", "tool_name": "RoofTool", "required_methods": ["DrawRect", "FinishShape"], "required_properties": ["isDrawing", "Texture"], "level_property": "Roofs"},
	{"operation_type": "object_placement", "tool_name": "ObjectTool", "required_methods": ["Confirm", "SetLayer", "SetSorting", "SetShadow", "SetBlockLight"], "required_properties": ["Texture", "Preview"], "level_property": "Objects"},
	{"operation_type": "wall_polyline", "tool_name": "WallTool", "required_methods": ["EndWall"], "required_properties": ["isDrawing", "Texture"], "level_property": "Walls"},
	{"operation_type": "material_stroke", "tool_name": "MaterialBrush", "required_methods": ["SetMaterial", "SetLayer", "SetSmooth"], "required_properties": ["Mesh"], "level_property": "MaterialMeshes"},
	{"operation_type": "portal_placement", "tool_name": "PortalTool", "required_methods": ["SetFreestanding", "FindBestLocation", "ChangeTexture"], "required_properties": ["Texture"], "level_property": "Portals"},
	{"operation_type": "path_polyline", "tool_name": "PathTool", "required_methods": ["StartPath", "EndPath", "SetLayer"], "required_properties": ["isDrawing", "Texture", "ActivePath"], "level_property": "Pathways"},
	{"operation_type": "light_placement", "tool_name": "LightTool", "required_methods": ["CreatePreview", "ChangeColor", "SetShadows"], "required_properties": ["texture", "preview"], "level_property": "Lights"},
	{"operation_type": "simple_tile_region", "tool_name": "FloorShapeTool", "required_methods": [], "required_properties": [], "level_property": "FloorShapes"},
	{"operation_type": "smart_tile_region", "tool_name": "FloorShapeTool", "required_methods": [], "required_properties": [], "level_property": "FloorShapes"},
	{"operation_type": "smart_tile_double_region", "tool_name": "FloorShapeTool", "required_methods": [], "required_properties": [], "level_property": "FloorShapes"},
]


func start():
	pass


func certify_runtime(editor, world, world_ui, runtime_identity, certified_operation_types):
	var records = []
	var exact_version = "unknown"
	var executable_sha256 = ""
	if typeof(runtime_identity) == TYPE_DICTIONARY:
		exact_version = runtime_identity.get("exact_dungeondraft_version", "unknown")
		executable_sha256 = runtime_identity.get("executable_sha256", "")
	var version_matches = (
		typeof(exact_version) == TYPE_STRING
		and exact_version == TARGET_DUNGEONDRAFT_VERSION
		and typeof(executable_sha256) == TYPE_STRING
		and executable_sha256 == TARGET_DUNGEONDRAFT_EXECUTABLE_SHA256)
	var level = null
	if world != null and world.has_method("GetLevelByID") and _has_readable_property(world, "CurrentLevelId"):
		level = world.GetLevelByID(world.CurrentLevelId)
	for route in OPERATION_ROUTES:
		var probe = _probe_route(route, editor, level, world_ui)
		var runtime_certified = version_matches and probe.route_present and certified_operation_types.has(route.operation_type)
		records.append({
			"operation_type": route.operation_type,
			"tool_name": route.tool_name,
			"required_methods": route.required_methods.duplicate(),
			"required_properties": route.required_properties.duplicate(),
			"route_present": probe.route_present,
			"methods_reflectable": probe.methods_reflectable,
			"properties_readable": probe.properties_readable,
			"busy": probe.busy,
			"exact_dungeondraft_version": exact_version,
			"runtime_certified": runtime_certified,
			"reason": _reason(version_matches, probe, runtime_certified),
		})
	return records


func _probe_route(route, editor, level, world_ui):
	if editor == null or not _has_readable_property(editor, "Tools") or typeof(editor.Tools) != TYPE_DICTIONARY or not editor.Tools.has(route.tool_name):
		return _probe(false, false, false, false, "tool_unavailable")
	var runtime_tool = editor.Tools[route.tool_name]
	if runtime_tool == null:
		return _probe(false, false, false, false, "tool_unavailable")
	if level == null or not _has_readable_property(level, route.level_property) or level.get(route.level_property) == null:
		return _probe(false, false, false, false, "level_contract_unavailable")
	if route.operation_type == "wall_polyline" and world_ui == null:
		return _probe(false, false, false, false, "world_ui_contract_unavailable")
	return _probe(
		true,
		_has_methods(runtime_tool, route.required_methods),
		_has_properties(runtime_tool, route.required_properties),
		_tool_is_busy(runtime_tool),
		"route_present")


func _probe(route_present, methods_reflectable, properties_readable, busy, reason):
	return {
		"route_present": route_present,
		"methods_reflectable": methods_reflectable,
		"properties_readable": properties_readable,
		"busy": busy,
		"reason": reason,
	}


func _has_methods(value, method_names):
	for method_name in method_names:
		if not value.has_method(method_name):
			return false
	return true


func _has_properties(value, property_names):
	for property_name in property_names:
		if not _has_readable_property(value, property_name):
			return false
	return true


func _has_readable_property(value, property_name):
	if value == null or typeof(property_name) != TYPE_STRING:
		return false
	for property in value.get_property_list():
		if typeof(property) == TYPE_DICTIONARY and property.get("name", "") == property_name:
			return true
	return false


func _tool_is_busy(runtime_tool):
	for property_name in ["isDrawing", "IsPainting", "isPainting", "isDragging", "IsMeshWorkerBusy"]:
		if _has_readable_property(runtime_tool, property_name) and bool(runtime_tool.get(property_name)):
			return true
	return false


func _reason(version_matches, probe, runtime_certified):
	if not version_matches:
		return "runtime_version_mismatch"
	if not probe.route_present:
		return probe.reason
	if not runtime_certified:
		return "executor_not_live_certified"
	return "tool_busy" if probe.busy else "runtime_certified"


func probe_map_data(global_object):
	var record = {
		"available": false,
		"is_dictionary": false,
		"ddai_key_present": false,
		"stored_uuid_valid": false,
		"foreign_key_count": 0,
		"reason": "map_data_unavailable",
	}
	if global_object == null or not _has_readable_property(global_object, "ModMapData"):
		return record
	var data = global_object.get("ModMapData")
	record.available = true
	if typeof(data) != TYPE_DICTIONARY:
		record.reason = "map_data_not_dictionary"
		return record
	record.is_dictionary = true
	record.foreign_key_count = data.keys().size()
	if data.has(DDAI_MAP_DATA_KEY):
		record.ddai_key_present = true
		record.foreign_key_count = max(0, record.foreign_key_count - 1)
		var owned = data[DDAI_MAP_DATA_KEY]
		if typeof(owned) == TYPE_DICTIONARY and _is_valid_uuid(owned.get("map_uuid", null)):
			record.stored_uuid_valid = true
	record.reason = "map_data_present"
	return record


func _is_valid_uuid(value):
	if typeof(value) != TYPE_STRING or value.length() != 64:
		return false
	for index in range(64):
		if "0123456789abcdef".find(value[index]) < 0:
			return false
	return true
