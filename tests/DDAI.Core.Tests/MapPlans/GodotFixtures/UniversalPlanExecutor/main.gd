extends Node

const BridgeScript = preload("res://ddai_bridge.gd")
const RESOURCE_IDENTITY = "res://fixture-wall.png"
const CATALOG_CATEGORIES = [
	"Terrain", "Patterns", "Patterns Colorable", "Caves", "Roofs", "Objects", "Walls",
	"Materials", "Portals", "Paths", "Lights", "Simple Tiles", "Smart Tiles", "Smart Tiles Double",
]
var _asset_list_calls = 0
var _accepted_catalog_fingerprint = ""

class MockWall:
	extends Node
	var native_id = 0
	var GlobalRect = Rect2(70, 70, 490, 0)

	func _init(value):
		native_id = value

	func Clear():
		Global.World.remove_node_id(native_id)

	func GetNodeID():
		return null


class MockWalls:
	extends Node

	func AddWall(_points, _texture, _color, _closed):
		var wall = Global.World.create_wall()
		Global.World.attach_wall(wall)
		return wall


class MockLevel:
	extends Reference
	var ID = 0
	var Label = "Ground"
	var Walls = MockWalls.new()
	var Pathways = Node.new()
	var Roofs = Node.new()
	var Objects = Node.new()
	var PatternShapes = MockPatternShapes.new()


class MockPatternShapes:
	extends Reference

	func GetShapes():
		return []


class MockWorld:
	extends Node
	var Width = 40
	var Height = 30
	var GridSize = 70.0
	var CurrentLevelId = 0
	var level = MockLevel.new()
	var levels = [level]
	var registry = {}
	var next_id = 100
	var last_created_id = 0
	var fail_has_node_call = -1
	var has_node_calls = 0
	var create_count = 1
	var fail_assignment_after = -1
	var assignment_calls = 0
	var preset_created_meta_id = 0

	func _init():
		add_child(level.Walls)
		add_child(level.Pathways)
		add_child(level.Roofs)
		add_child(level.Objects)

	func GetLevelByID(level_id):
		return level if int(level_id) == 0 else null

	func HasNodeID(node_id):
		has_node_calls += 1
		if fail_has_node_call > 0 and has_node_calls == fail_has_node_call:
			return false
		return registry.has(int(node_id))

	func GetNodeByID(node_id):
		return registry.get(int(node_id), null)

	func create_wall():
		next_id += 1
		last_created_id = next_id
		var wall = MockWall.new(next_id)
		if preset_created_meta_id > 0:
			wall.set_meta("node_id", preset_created_meta_id)
			preset_created_meta_id = 0
		return wall

	func attach_wall(wall):
		if wall.get_parent() == null:
			level.Walls.add_child(wall)

	func AssignNodeID(node):
		assignment_calls += 1
		if fail_assignment_after >= 0 and assignment_calls > fail_assignment_after:
			return null
		if registry.has(node.native_id) and registry[node.native_id] != node:
			next_id += 1
			node.native_id = next_id
		node.set_meta("node_id", node.native_id)
		registry[node.native_id] = node
		return node.native_id

	func remove_node_id(node_id):
		registry.erase(int(node_id))


class MockWallTool:
	extends Reference
	var isDrawing = false
	var Texture = null
	var wall_color = null
	var enabled = false

	func _get(property):
		if property == "Color":
			return wall_color
		return null

	func _set(property, value):
		if property == "Color":
			wall_color = value
			return true
		return false

	func Enable():
		enabled = true

	func Disable():
		enabled = false

	func EndWall(_closed):
		var wall = Global.World.create_wall()
		Global.World.attach_wall(wall)
		Global.World.AssignNodeID(wall)


class MockEditor:
	extends Reference
	var ActiveToolName = "WallTool"
	var Tools = {"WallTool": MockWallTool.new()}


class MockWorldUI:
	extends Reference
	var EditArcPoint = false
	var Polyline = []

	func ClearPolyline():
		Polyline.clear()

	func AddPolyPoint(point):
		Polyline.append(point)


func _ready():
	Global.World = MockWorld.new()
	add_child(Global.World)
	Global.Editor = MockEditor.new()
	Global.WorldUI = MockWorldUI.new()
	var bridge = BridgeScript.new()
	bridge._session_id = "executor-session"
	bridge._certified_operation_executors = {"wall_polyline": funcref(bridge, "_execute_wall_polyline"), "cave_region": funcref(bridge, "_execute_cave_region")}
	bridge._runtime_certified_operation_types = ["wall_polyline"]
	bridge._asset_list_provider = funcref(self, "_asset_list")
	bridge._texture_loader = funcref(self, "_texture")
	bridge._ensure_mailbox_directories()
	_clear_request_state("universal-wall-success")
	_clear_request_state("universal-wall-crash")
	_clear_request_state("universal-wall-observe-fail")
	_clear_request_state("universal-wall-registration-fail")
	_publish_catalog(bridge)
	bridge._status_payload()

	var initial_wall_texture = ImageTexture.new()
	var initial_wall_color = Color("#12345678")
	Global.Editor.Tools["WallTool"].Texture = initial_wall_texture
	Global.Editor.Tools["WallTool"].wall_color = initial_wall_color
	Global.Editor.Tools["WallTool"].enabled = true
	var plan = _plan(bridge, "universal-wall-success", 0)
	var plan_fingerprint = bridge._universal_plan_fingerprint_input(plan).sha256_text()
	var fingerprint_ok = plan_fingerprint.length() == 64
	var validation_ok = bridge._validate_universal_plan(plan, plan.request_id).ok
	var preflight_ok = bridge._preflight_universal_plan(plan).ok
	var uncertified_plan = plan.duplicate(true)
	uncertified_plan.operations[0].operation_type = "cave_region"
	var uncertified_preflight = bridge._preflight_universal_plan(uncertified_plan)
	var uncertified_executor_rejected = not uncertified_preflight.ok and uncertified_preflight.error.code == "unsupported_operation"
	Global.Editor.ActiveToolName = ""
	var unnamed_inactive_tool_preflight_ok = bridge._preflight_universal_plan(plan).ok
	Global.Editor.ActiveToolName = null
	var null_inactive_tool_preflight_ok = bridge._preflight_universal_plan(plan).ok
	Global.Editor.ActiveToolName = "WallTool"
	var result = yield(_run_job(bridge, plan), "completed")
	var active_tool_success_restored = Global.Editor.Tools["WallTool"].enabled and Global.Editor.Tools["WallTool"].Texture == initial_wall_texture and Global.Editor.Tools["WallTool"].wall_color == initial_wall_color
	var native_id = Global.World.last_created_id
	var observed_ok = result.success and Global.World.HasNodeID(native_id) and bridge._map_job_revision == 1
	var success_key = plan.request_id.sha256_text()
	var success_request = _request(plan)
	var success_claim = {"file_name": success_key + ".json", "path": "user://ddai/processing/" + success_key + ".json"}
	var success_request_fingerprint = bridge._canonical_request_text(success_request).sha256_text()
	var completion_transition = bridge._advance_universal_plan_claim(success_claim, success_request, success_request_fingerprint, "user://ddai/journal/" + success_key + ".json", "user://ddai/responses/" + success_key + ".json", success_key)
	var success_job_path = "user://ddai/map-jobs/" + success_key + ".json"
	var tampered_completed_job = bridge._read_bounded_dictionary(success_job_path)
	tampered_completed_job.map_state_fingerprint = "e".repeat(64)
	bridge._replace_json_recoverably(success_job_path, tampered_completed_job)
	var completion_tamper_blocked = completion_transition == "map_job_completion_recorded" and bridge._cleanup_completed_map_job(success_key + ".json") == "blocked" and Directory.new().file_exists(success_job_path)
	var reversal_ok = bridge._reverse_operation(plan.operations[0], [native_id]) and not Global.World.HasNodeID(native_id)
	yield(get_tree(), "idle_frame")

	bridge._status_payload()
	var observe_fail_plan = _plan(bridge, "universal-wall-observe-fail", bridge._map_job_revision)
	Global.World.has_node_calls = 0
	Global.World.fail_has_node_call = 3
	var observe_fail_result = yield(_run_job(bridge, observe_fail_plan), "completed")
	Global.World.fail_has_node_call = -1
	var failed_native_id = Global.World.last_created_id
	var observe_failure_reversed = not observe_fail_result.success and not observe_fail_result.payload.outcome_unknown and observe_fail_result.error.code == "operation_failed_reversed" and not Global.World.HasNodeID(failed_native_id)
	var observe_fail_key = observe_fail_plan.request_id.sha256_text()
	var observe_fail_request = _request(observe_fail_plan)
	var observe_fail_claim = {"file_name": observe_fail_key + ".json", "path": "user://ddai/processing/" + observe_fail_key + ".json"}
	var observe_fail_request_fingerprint = bridge._canonical_request_text(observe_fail_request).sha256_text()
	var completion_recorded = bridge._advance_universal_plan_claim(observe_fail_claim, observe_fail_request, observe_fail_request_fingerprint, "user://ddai/journal/" + observe_fail_key + ".json", "user://ddai/responses/" + observe_fail_key + ".json", observe_fail_key) == "map_job_completion_recorded"
	var original_claim_deleted = bridge._advance_universal_plan_claim(observe_fail_claim, observe_fail_request, observe_fail_request_fingerprint, "user://ddai/journal/" + observe_fail_key + ".json", "user://ddai/responses/" + observe_fail_key + ".json", observe_fail_key) == "map_job_claim_deleted"
	var journal_deleted = bridge._cleanup_completed_map_job(observe_fail_key + ".json") == "map_job_journal_deleted"
	_write_claim(bridge, observe_fail_request, observe_fail_key)
	var duplicate_claim = {"file_name": observe_fail_key + ".json", "path": "user://ddai/processing/" + observe_fail_key + ".json"}
	var duplicate_after_cleanup_ok = completion_recorded and original_claim_deleted and journal_deleted and bridge._advance_universal_plan_claim(duplicate_claim, observe_fail_request, observe_fail_request_fingerprint, "user://ddai/journal/" + observe_fail_key + ".json", "user://ddai/responses/" + observe_fail_key + ".json", observe_fail_key) == "map_job_claim_deleted"
	var stable_status = bridge._status_payload()
	yield(get_tree(), "idle_frame")
	var stable_status_after_frame = bridge._status_payload()
	var reversal_revision_stable = int(stable_status.map_job_revision) == int(stable_status_after_frame.map_job_revision)
	var registration_fail_plan = _plan(bridge, "universal-wall-registration-fail", bridge._map_job_revision)
	var failure_wall_texture = ImageTexture.new()
	var failure_wall_color = Color("#87654321")
	Global.Editor.ActiveToolName = "ObjectTool"
	Global.Editor.Tools["WallTool"].Texture = failure_wall_texture
	Global.Editor.Tools["WallTool"].wall_color = failure_wall_color
	Global.Editor.Tools["WallTool"].enabled = false
	Global.World.fail_assignment_after = 0
	var registration_fail_result = yield(_run_job(bridge, registration_fail_plan), "completed")
	var partial_registration_is_unknown = not registration_fail_result.success and registration_fail_result.payload.outcome_unknown and registration_fail_result.error.code == "outcome_unknown"
	var inactive_tool_failure_restored = not Global.Editor.Tools["WallTool"].enabled and Global.Editor.Tools["WallTool"].Texture == failure_wall_texture and Global.Editor.Tools["WallTool"].wall_color == failure_wall_color
	Global.World.fail_assignment_after = -1
	# The production contract deliberately retains untrackable native state for
	# user inspection. Isolate the remaining recovery scenarios in this harness.
	for wall in Global.World.level.Walls.get_children():
		wall.free()
	Global.World.registry.clear()
	bridge._status_payload()

	Global.World.next_id = 400
	var existing_collision_wall = MockWall.new(401)
	existing_collision_wall.set_meta("node_id", 401)
	Global.World.level.Walls.add_child(existing_collision_wall)
	Global.World.registry[401] = existing_collision_wall
	Global.World.preset_created_meta_id = 401
	bridge._resolved_job_assets["collision-probe"] = {"wall-a": RESOURCE_IDENTITY}
	var collision_result = bridge._execute_wall_polyline(_plan(bridge, "universal-wall-id-collision", bridge._map_job_revision).operations[0], "collision-probe")
	var collision_created_wall = null
	for candidate in Global.World.level.Walls.get_children():
		if candidate != existing_collision_wall:
			collision_created_wall = candidate
			break
	var collision_created_id = collision_created_wall.get_meta("node_id") if collision_created_wall != null and collision_created_wall.has_meta("node_id") else 0
	var node_id_collision_recovered = collision_result.ok and collision_result.node_ids.size() == 1 and collision_result.node_ids[0] == collision_created_id and Global.World.GetNodeByID(401) == existing_collision_wall and collision_created_wall != null and collision_created_wall != existing_collision_wall and collision_created_wall.get_parent() == Global.World.level.Walls and collision_created_id > 0 and Global.World.GetNodeByID(collision_created_id) == collision_created_wall
	for wall in Global.World.level.Walls.get_children():
		wall.free()
	Global.World.registry.clear()
	bridge._status_payload()

	bridge._status_payload()
	var crash_plan = _plan(bridge, "universal-wall-crash", bridge._map_job_revision)
	var crash_request = _request(crash_plan)
	var crash_key = crash_plan.request_id.sha256_text()
	_write_claim(bridge, crash_request, crash_key)
	var crash_claim = {"file_name": crash_key + ".json", "path": "user://ddai/processing/" + crash_key + ".json"}
	var crash_request_fingerprint = bridge._canonical_request_text(crash_request).sha256_text()
	var first_transition = bridge._advance_universal_plan_claim(crash_claim, crash_request, crash_request_fingerprint, "", "user://ddai/responses/" + crash_key + ".json", crash_key)
	var restarted = BridgeScript.new()
	restarted._session_id = "executor-session-restarted"
	restarted._map_job_revision = 0
	restarted._certified_operation_executors = {"wall_polyline": funcref(restarted, "_execute_wall_polyline")}
	restarted._asset_list_provider = funcref(self, "_asset_list")
	restarted._texture_loader = funcref(self, "_texture")
	var restart_transition = restarted._advance_universal_plan_claim(crash_claim, crash_request, crash_request_fingerprint, "", "user://ddai/responses/" + crash_key + ".json", crash_key)
	var recovered_response = null
	for _step in range(8):
		restarted._advance_universal_plan_claim(crash_claim, crash_request, crash_request_fingerprint, "", "user://ddai/responses/" + crash_key + ".json", crash_key)
		recovered_response = restarted._read_bounded_dictionary("user://ddai/responses/" + crash_key + ".json")
		if recovered_response != null:
			break
	var recovery_ok = first_transition == "map_job_prepared" and restart_transition == "map_job_outcome_unknown" and recovered_response != null and not recovered_response.success and recovered_response.payload.outcome_unknown and int(recovered_response.payload.map_revision) >= int(crash_plan.base_revision) and Global.World.registry.size() == 0

	var empty_plan = _plan(bridge, "universal-empty", bridge._map_job_revision)
	empty_plan.operations = []
	var bad_level_plan = _plan(bridge, "universal-bad-level", bridge._map_job_revision)
	bad_level_plan.operations[0].level_id = "foo"
	var trailing_asset_plan = _plan(bridge, "universal-trailing-asset", bridge._map_job_revision)
	trailing_asset_plan.operations[0].asset_ref += "junk"
	_publish_bad_catalog(bridge)
	var bad_catalog_rejected = not bridge._read_accepted_catalog().ok
	_publish_tampered_catalog(bridge)
	var catalog_manifest_integrity_ok = not bridge._read_accepted_catalog().ok
	bridge._replace_json_recoverably("user://ddai/catalog/current.json", {"session_id": bridge._session_id, "manifest": "executor-session-7-test/manifest.json", "catalog_revision": 7})
	var strict_failure_ok = not bridge._validate_universal_plan(empty_plan, empty_plan.request_id).ok and not bridge._validate_universal_plan(trailing_asset_plan, trailing_asset_plan.request_id).ok and bad_catalog_rejected and catalog_manifest_integrity_ok and bridge._validate_universal_plan(bad_level_plan, bad_level_plan.request_id).ok and not bridge._preflight_universal_plan(bad_level_plan).ok
	var repeated_asset_plan = _plan(bridge, "universal-repeated-asset", bridge._map_job_revision)
	var repeated_operation = repeated_asset_plan.operations[0].duplicate(true)
	repeated_operation.operation_id = "wall-b"
	repeated_asset_plan.operations.append(repeated_operation)
	var calls_before_repeated = _asset_list_calls
	var repeated_preflight_ok = bridge._preflight_universal_plan(repeated_asset_plan).ok and _asset_list_calls - calls_before_repeated == 1
	_clear_request_state("universal-bad-level")
	var correlated_failure = yield(_run_job(bridge, bad_level_plan), "completed")
	var preflight_correlation_ok = typeof(correlated_failure) == TYPE_DICTIONARY and correlated_failure.has("success") and not correlated_failure.success and correlated_failure.has("error") and typeof(correlated_failure.error) == TYPE_DICTIONARY and correlated_failure.has("payload") and typeof(correlated_failure.payload) == TYPE_DICTIONARY
	if preflight_correlation_ok:
		preflight_correlation_ok = correlated_failure.error.get("code", "") == "unsupported_level"
	if preflight_correlation_ok:
		preflight_correlation_ok = correlated_failure.payload.get("map_id", "") == bad_level_plan.expected_map_id
	if preflight_correlation_ok:
		preflight_correlation_ok = int(correlated_failure.payload.get("starting_map_revision", -1)) == int(bad_level_plan.base_revision)
	if preflight_correlation_ok:
		preflight_correlation_ok = int(correlated_failure.payload.get("catalog_revision", -1)) == int(bad_level_plan.expected_catalog_revision)
	if preflight_correlation_ok:
		preflight_correlation_ok = correlated_failure.payload.get("catalog_fingerprint", "") == _accepted_catalog_fingerprint
	var catalog_race_plan = _plan(bridge, "universal-catalog-race", bridge._map_job_revision)
	_clear_request_state("universal-catalog-race")
	_publish_newer_catalog(bridge)
	var catalog_race_failure = yield(_run_job(bridge, catalog_race_plan), "completed")
	var catalog_race_correlation_ok = typeof(catalog_race_failure) == TYPE_DICTIONARY and catalog_race_failure.has("success") and not catalog_race_failure.success and catalog_race_failure.has("error") and typeof(catalog_race_failure.error) == TYPE_DICTIONARY and catalog_race_failure.has("payload") and typeof(catalog_race_failure.payload) == TYPE_DICTIONARY
	if catalog_race_correlation_ok:
		catalog_race_correlation_ok = catalog_race_failure.error.get("code", "") == "catalog_revision_mismatch" and int(catalog_race_failure.payload.get("catalog_revision", -1)) == 7 and catalog_race_failure.payload.get("catalog_fingerprint", "") == _accepted_catalog_fingerprint

	var tampered = bridge._new_map_job_journal(crash_request, crash_request_fingerprint, bridge._universal_plan_fingerprint_input(crash_plan).sha256_text(), {
		"map_id": crash_plan.expected_map_id,
		"starting_map_revision": crash_plan.base_revision,
		"catalog_revision": crash_plan.expected_catalog_revision,
		"catalog_fingerprint": _accepted_catalog_fingerprint,
		"map_state_fingerprint": "d".repeat(64),
	})
	tampered.state = "operation_applied"
	tampered.current_operation_node_ids = [201, 201]
	var duplicate_nodes_rejected = not bridge._map_job_state_is_consistent(tampered, 1)
	tampered.current_operation_node_ids = [201]
	tampered.state = "reversing"
	tampered.reversal_operation_index = 0
	var reversing_current_accepted = bridge._map_job_state_is_consistent(tampered, 1)
	tampered.current_operation_node_ids = []
	var missing_reversal_nodes_rejected = not bridge._map_job_state_is_consistent(tampered, 1)
	var observed_tamper = tampered.duplicate(true)
	observed_tamper.state = "operation_observed"
	observed_tamper.next_operation_index = 1
	observed_tamper.reversal_operation_index = null
	observed_tamper.observed_native_node_ids = [301]
	observed_tamper.operation_observations = [{"operation_index": 0, "operation_id": "wall-a", "native_node_ids": [301]}]
	var valid_observation_accepted = bridge._map_job_state_is_consistent(observed_tamper, 1)
	observed_tamper.operation_observations[0].native_node_ids = []
	var empty_observation_rejected = not bridge._map_job_state_is_consistent(observed_tamper, 1)
	observed_tamper.operation_observations[0].native_node_ids = [301]
	observed_tamper.state = "reversing"
	observed_tamper.reversal_operation_index = 0
	observed_tamper.current_operation_node_ids = [302]
	var wrong_reversal_ids_rejected = not bridge._map_job_state_is_consistent(observed_tamper, 1)
	observed_tamper.current_operation_node_ids = [301]
	var exact_reversal_ids_accepted = bridge._map_job_state_is_consistent(observed_tamper, 1)
	var committed_tamper = observed_tamper.duplicate(true)
	committed_tamper.state = "committed"
	committed_tamper.current_operation_node_ids = []
	committed_tamper.reversal_operation_index = null
	committed_tamper.canonical_response = "{}"
	var committed_tamper_path = "user://ddai/map-jobs/committed-tamper.json"
	bridge._replace_json_recoverably(committed_tamper_path, committed_tamper)
	var committed_response_tamper_rejected = bridge._read_map_job_journal(committed_tamper_path, crash_request, crash_request_fingerprint, committed_tamper.plan_fingerprint) == null
	Directory.new().remove(committed_tamper_path)
	var journal_invariants_ok = duplicate_nodes_rejected and reversing_current_accepted and missing_reversal_nodes_rejected and valid_observation_accepted and empty_observation_rejected and wrong_reversal_ids_rejected and exact_reversal_ids_accepted and committed_response_tamper_rejected
	var number_texts = []
	for number in [0.0, 1e-300, 1e-18, 0.0000001, 0.00001, 0.1, 1.0000001, 1.23456789012345, 40.0, 2147483647.0]:
		number_texts.append(bridge._double_fingerprint_text(number))

	# Isolate the job-scoped undo proof from prior failure-injection scenarios.
	for wall in Global.World.level.Walls.get_children():
		wall.free()
	Global.World.registry.clear()
	_publish_catalog(bridge)
	bridge._status_payload()
	var undo_plan = _plan(bridge, "universal-wall-undoable", bridge._map_job_revision)
	_clear_request_state(undo_plan.request_id)
	var undo_apply_result = yield(_run_job(bridge, undo_plan), "completed")
	var undo_target_id = Global.World.last_created_id
	var undo_completion_recorded = _complete_apply_job(bridge, undo_plan)
	var undo_request = _undo_request("undo-exact-001", undo_plan.request_id, undo_plan.expected_map_id, bridge._map_job_revision)
	var undo_response = yield(_run_undo(bridge, undo_request), "completed")
	yield(get_tree(), "idle_frame")
	var exact_undo_ok = undo_apply_result.success and undo_completion_recorded and undo_response.success and not Global.World.HasNodeID(undo_target_id) and int(undo_response.payload.map_revision) == int(undo_request.payload.expected_map_revision) + 1
	var first_undo_text = to_json(undo_response)
	_write_claim(bridge, undo_request, undo_request.request_id.sha256_text())
	var duplicate_undo = yield(_run_undo(bridge, undo_request, false), "completed")
	var duplicate_undo_ok = to_json(duplicate_undo) == first_undo_text and not Global.World.HasNodeID(undo_target_id)

	var stale_plan = _plan(bridge, "universal-wall-stale-undo", bridge._map_job_revision)
	_clear_request_state(stale_plan.request_id)
	var stale_apply_result = yield(_run_job(bridge, stale_plan), "completed")
	var stale_completion_recorded = _complete_apply_job(bridge, stale_plan)
	var stale_revision = bridge._map_job_revision
	var untracked = MockWall.new(9001)
	untracked.set_meta("node_id", 9001)
	Global.World.level.Walls.add_child(untracked)
	Global.World.registry[9001] = untracked
	bridge._status_payload()
	var stale_undo_request = _undo_request("undo-stale-001", stale_plan.request_id, stale_plan.expected_map_id, stale_revision)
	var stale_validation = bridge._prepare_undo_job(stale_undo_request, bridge._canonical_request_text(stale_undo_request).sha256_text(), stale_undo_request.payload)
	var intervening_edit_rejected = stale_apply_result.success and stale_completion_recorded and not stale_validation.ok and stale_validation.error.code == "map_revision_mismatch"

	var missing_plan = _plan(bridge, "universal-wall-missing-node", bridge._map_job_revision)
	_clear_request_state(missing_plan.request_id)
	var missing_apply_result = yield(_run_job(bridge, missing_plan), "completed")
	var missing_completion_recorded = _complete_apply_job(bridge, missing_plan)
	var missing_target_id = Global.World.last_created_id
	Global.World.registry.erase(missing_target_id)
	var missing_request = _undo_request("undo-missing-001", missing_plan.request_id, missing_plan.expected_map_id, bridge._map_job_revision)
	var missing_prepare = bridge._prepare_undo_job(missing_request, bridge._canonical_request_text(missing_request).sha256_text(), missing_request.payload)
	var missing_node_rejected = missing_apply_result.success and missing_completion_recorded and not missing_prepare.ok and missing_prepare.error.code == "undo_evidence_missing"

	for wall in Global.World.level.Walls.get_children():
		wall.free()
	Global.World.registry.clear()
	bridge._status_payload()
	var interrupted_plan = _plan(bridge, "universal-wall-interrupted-undo", bridge._map_job_revision)
	_clear_request_state(interrupted_plan.request_id)
	var interrupted_apply = yield(_run_job(bridge, interrupted_plan), "completed")
	var interrupted_completion = _complete_apply_job(bridge, interrupted_plan)
	var interrupted_target_id = Global.World.last_created_id
	var interrupted_request = _undo_request("undo-interrupted-001", interrupted_plan.request_id, interrupted_plan.expected_map_id, bridge._map_job_revision)
	var interrupted_key = interrupted_request.request_id.sha256_text()
	_write_claim(bridge, interrupted_request, interrupted_key)
	var interrupted_claim = {"file_name": interrupted_key + ".json", "path": "user://ddai/processing/" + interrupted_key + ".json"}
	var interrupted_fingerprint = bridge._canonical_request_text(interrupted_request).sha256_text()
	var interrupted_prepared = bridge._advance_undo_last_job_claim(interrupted_claim, interrupted_request, interrupted_fingerprint, "user://ddai/responses/" + interrupted_key + ".json", interrupted_key)
	var interrupted_reversing = bridge._advance_undo_last_job_claim(interrupted_claim, interrupted_request, interrupted_fingerprint, "user://ddai/responses/" + interrupted_key + ".json", interrupted_key)
	var undo_restarted = BridgeScript.new()
	undo_restarted._session_id = bridge._session_id
	undo_restarted._map_job_revision = bridge._map_job_revision
	undo_restarted._map_job_state_fingerprint = bridge._map_job_state_fingerprint
	undo_restarted._certified_operation_executors = {"wall_polyline": funcref(undo_restarted, "_execute_wall_polyline")}
	undo_restarted._asset_list_provider = funcref(self, "_asset_list")
	undo_restarted._texture_loader = funcref(self, "_texture")
	var interrupted_restart = undo_restarted._advance_undo_last_job_claim(interrupted_claim, interrupted_request, interrupted_fingerprint, "user://ddai/responses/" + interrupted_key + ".json", interrupted_key)
	var interrupted_response = null
	for _step in range(8):
		undo_restarted._advance_undo_last_job_claim(interrupted_claim, interrupted_request, interrupted_fingerprint, "user://ddai/responses/" + interrupted_key + ".json", interrupted_key)
		interrupted_response = undo_restarted._read_bounded_dictionary("user://ddai/responses/" + interrupted_key + ".json")
		if interrupted_response != null:
			break
	var interrupted_undo_unknown = interrupted_apply.success and interrupted_completion and interrupted_prepared == "undo_job_prepared" and interrupted_reversing == "undo_job_reversing" and interrupted_restart == "undo_job_outcome_unknown" and interrupted_response != null and not interrupted_response.success and interrupted_response.payload.outcome_unknown and Global.World.HasNodeID(interrupted_target_id)

	print("DDAI_UNIVERSAL_FINGERPRINT:", fingerprint_ok)
	print("DDAI_UNIVERSAL_PLAN_JSON:", to_json(plan))
	print("DDAI_UNIVERSAL_PLAN_FINGERPRINT:", plan_fingerprint)
	print("DDAI_UNIVERSAL_VALIDATION_PREFLIGHT:", validation_ok and preflight_ok and unnamed_inactive_tool_preflight_ok and null_inactive_tool_preflight_ok)
	print("DDAI_UNIVERSAL_UNCERTIFIED_EXECUTOR_REJECTED:", uncertified_executor_rejected)
	print("DDAI_UNIVERSAL_APPLY_OBSERVE:", observed_ok)
	print("DDAI_UNIVERSAL_COMPLETION_TAMPER_BLOCKED:", completion_tamper_blocked)
	print("DDAI_UNIVERSAL_REVERSAL:", reversal_ok)
	print("DDAI_UNIVERSAL_OBSERVE_FAILURE_REVERSED:", observe_failure_reversed)
	print("DDAI_UNIVERSAL_DUPLICATE_AFTER_CLEANUP:", duplicate_after_cleanup_ok)
	print("DDAI_UNIVERSAL_REVERSAL_REVISION_STABLE:", reversal_revision_stable)
	print("DDAI_UNIVERSAL_PARTIAL_REGISTRATION_UNKNOWN:", partial_registration_is_unknown)
	print("DDAI_UNIVERSAL_WALL_TOOL_STATE_RESTORED:", active_tool_success_restored and inactive_tool_failure_restored)
	print("DDAI_UNIVERSAL_NODE_ID_COLLISION_RECOVERED:", node_id_collision_recovered)
	print("DDAI_UNIVERSAL_PREPARED_RECOVERY:", recovery_ok)
	print("DDAI_UNIVERSAL_STRICT_FAILURES:", strict_failure_ok)
	print("DDAI_UNIVERSAL_CATALOG_MANIFEST_INTEGRITY:", catalog_manifest_integrity_ok)
	print("DDAI_UNIVERSAL_UNIQUE_ASSET_RESOLUTION:", repeated_preflight_ok)
	print("DDAI_UNIVERSAL_PREFLIGHT_CORRELATION:", preflight_correlation_ok)
	print("DDAI_UNIVERSAL_CATALOG_RACE_CORRELATION:", catalog_race_correlation_ok)
	print("DDAI_UNIVERSAL_JOURNAL_INVARIANTS:", journal_invariants_ok)
	print("DDAI_UNDO_EXACT_COMPLETED_JOB:", exact_undo_ok)
	print("DDAI_UNDO_INTERVENING_EDIT_REJECTED:", intervening_edit_rejected)
	print("DDAI_UNDO_DUPLICATE_IDEMPOTENT:", duplicate_undo_ok)
	print("DDAI_UNDO_MISSING_NODE_REJECTED:", missing_node_rejected)
	print("DDAI_UNDO_INTERRUPTED_BEFORE_REVERSAL_UNKNOWN:", interrupted_undo_unknown)
	print("DDAI_UNIVERSAL_DOUBLE_BITS:", PoolStringArray(number_texts).join("|"))
	bridge = null
	restarted = null
	Global.Editor = null
	Global.WorldUI = null
	get_tree().quit(0 if fingerprint_ok and validation_ok and preflight_ok and unnamed_inactive_tool_preflight_ok and null_inactive_tool_preflight_ok and observed_ok and completion_tamper_blocked and reversal_ok and observe_failure_reversed and duplicate_after_cleanup_ok and reversal_revision_stable and partial_registration_is_unknown and active_tool_success_restored and inactive_tool_failure_restored and node_id_collision_recovered and recovery_ok and strict_failure_ok and catalog_manifest_integrity_ok and repeated_preflight_ok and preflight_correlation_ok and catalog_race_correlation_ok and journal_invariants_ok and exact_undo_ok and intervening_edit_rejected and duplicate_undo_ok and missing_node_rejected and interrupted_undo_unknown else 1)


func _plan(bridge, request_id, revision):
	var map_id = bridge._current_map_id()
	var json = '{"schema_version":"2.0","request_id":"' + request_id + '","expected_map_id":"' + map_id + '","base_revision":' + str(revision) + ',"expected_catalog_revision":7,"mode":"add","coordinate_system":"grid","canvas":{"width":40,"height":30},"operations":[{"operation_type":"wall_polyline","operation_id":"wall-a","level_id":"0","asset_ref":"sha256:' + "a".repeat(64) + '","path":{"points":[{"x":1.23456789012345,"y":1.0000001},{"x":8.125,"y":1}]},"closed":false,"color_rgba":"#ffffffff"}]}'
	return JSON.parse(json).result


func _request(plan):
	return {
		"schema_version": "1.0",
		"request_id": plan.request_id,
		"command": "apply_plan",
		"timestamp": "2026-08-11T20:00:00Z",
		"payload": plan,
	}


func _run_job(bridge, plan):
	var request = _request(plan)
	var key = plan.request_id.sha256_text()
	_write_claim(bridge, request, key)
	var claim = {"file_name": key + ".json", "path": "user://ddai/processing/" + key + ".json"}
	var request_fingerprint = bridge._canonical_request_text(request).sha256_text()
	for _step in range(20):
		bridge._advance_universal_plan_claim(claim, request, request_fingerprint, "user://ddai/journal/" + key + ".json", "user://ddai/responses/" + key + ".json", key)
		yield(get_tree(), "idle_frame")
		var response = bridge._read_bounded_dictionary("user://ddai/responses/" + key + ".json")
		if response != null:
			return response
	return {"success": false}


func _complete_apply_job(bridge, plan):
	var request = _request(plan)
	var key = plan.request_id.sha256_text()
	var claim = {"file_name": key + ".json", "path": "user://ddai/processing/" + key + ".json"}
	var request_fingerprint = bridge._canonical_request_text(request).sha256_text()
	for _step in range(8):
		var transition = bridge._advance_universal_plan_claim(claim, request, request_fingerprint, "user://ddai/journal/" + key + ".json", "user://ddai/responses/" + key + ".json", key)
		if transition == "map_job_completion_recorded" or Directory.new().file_exists("user://ddai/map-completed/" + key + ".json"):
			return true
	return false


func _undo_request(request_id, target_request_id, expected_map_id, expected_revision):
	return {"schema_version": "1.0", "request_id": request_id, "command": "undo_last_job", "timestamp": "2026-08-12T20:00:00Z", "payload": {"target_request_id": target_request_id, "expected_map_id": expected_map_id, "expected_map_revision": expected_revision}}


func _run_undo(bridge, request, write_claim = true):
	var key = request.request_id.sha256_text()
	if write_claim:
		_write_claim(bridge, request, key)
	var claim = {"file_name": key + ".json", "path": "user://ddai/processing/" + key + ".json"}
	var request_fingerprint = bridge._canonical_request_text(request).sha256_text()
	for _step in range(40):
		bridge._advance_undo_last_job_claim(claim, request, request_fingerprint, "user://ddai/responses/" + key + ".json", key)
		yield(get_tree(), "idle_frame")
		var response = bridge._read_bounded_dictionary("user://ddai/responses/" + key + ".json")
		if response != null:
			return response
	return {"success": false}


func _write_claim(bridge, request, key):
	bridge._write_text_atomically("user://ddai/processing/" + key + ".json", to_json(request))


func _clear_request_state(request_id):
	var key = request_id.sha256_text()
	var directory = Directory.new()
	for path in [
		"user://ddai/processing/" + key + ".json",
		"user://ddai/responses/" + key + ".json",
		"user://ddai/journal/" + key + ".json",
		"user://ddai/map-jobs/" + key + ".json",
		"user://ddai/map-completed/" + key + ".json",
		"user://ddai/map-undoable/" + key + ".json",
		"user://ddai/map-undone/" + key + ".json",
		"user://ddai/undo-jobs/" + key + ".json",
	]:
		directory.remove(path)


func _publish_catalog(bridge):
	var root = "user://ddai/catalog"
	var snapshot = root + "/snapshots/executor-session-7-test"
	var directory = Directory.new()
	directory.make_dir_recursive(snapshot)
	var entry = _catalog_entry("sha256:" + "a".repeat(64))
	var chunk_text = to_json([entry])
	bridge._replace_json_recoverably(snapshot + "/assets-000.json", [entry])
	var counts = _empty_category_counts()
	counts.Walls = 1
	var manifest = _catalog_manifest(bridge, 7, counts, [{"file_name": "assets-000.json", "sha256": chunk_text.sha256_text(), "entry_count": 1, "byte_count": chunk_text.to_utf8().size()}])
	_accepted_catalog_fingerprint = manifest.catalog_fingerprint
	bridge._replace_json_recoverably(snapshot + "/manifest.json", manifest)
	bridge._replace_json_recoverably(root + "/current.json", {"session_id": bridge._session_id, "manifest": "executor-session-7-test/manifest.json", "catalog_revision": 7})
	bridge._replace_json_recoverably(root + "/current-slot-0.json", {"session_id": bridge._session_id, "manifest": "executor-session-7-test/manifest.json", "catalog_revision": 7})


func _publish_newer_catalog(bridge):
	var root = "user://ddai/catalog"
	var snapshot = root + "/snapshots/executor-session-8-test"
	Directory.new().make_dir_recursive(snapshot)
	bridge._replace_json_recoverably(snapshot + "/manifest.json", _catalog_manifest(bridge, 8, _empty_category_counts(), []))
	bridge._replace_json_recoverably(root + "/current.json", {"session_id": bridge._session_id, "manifest": "executor-session-8-test/manifest.json", "catalog_revision": 8})


func _publish_bad_catalog(bridge):
	var root = "user://ddai/catalog"
	var snapshot = root + "/snapshots/executor-session-7-bad"
	Directory.new().make_dir_recursive(snapshot)
	var entry = _catalog_entry("sha256:" + "a".repeat(64))
	entry.asset_ref += "junk"
	var entries = [entry]
	var chunk_text = to_json(entries)
	bridge._replace_json_recoverably(snapshot + "/assets-000.json", entries)
	var counts = _empty_category_counts()
	counts.Walls = 1
	bridge._replace_json_recoverably(snapshot + "/manifest.json", _catalog_manifest(bridge, 7, counts, [{"file_name": "assets-000.json", "sha256": chunk_text.sha256_text(), "entry_count": 1, "byte_count": chunk_text.to_utf8().size()}]))
	bridge._replace_json_recoverably(root + "/current.json", {"session_id": bridge._session_id, "manifest": "executor-session-7-bad/manifest.json", "catalog_revision": 7})


func _publish_tampered_catalog(bridge):
	var root = "user://ddai/catalog"
	var snapshot = root + "/snapshots/executor-session-7-tampered"
	Directory.new().make_dir_recursive(snapshot)
	var entries = [_catalog_entry("sha256:" + "b".repeat(64))]
	var chunk_text = to_json(entries)
	bridge._replace_json_recoverably(snapshot + "/assets-000.json", entries)
	var counts = _empty_category_counts()
	counts.Walls = 1
	var manifest = _catalog_manifest(bridge, 7, counts, [{"file_name": "assets-000.json", "sha256": chunk_text.sha256_text(), "entry_count": 1, "byte_count": chunk_text.to_utf8().size()}])
	manifest.catalog_fingerprint = _accepted_catalog_fingerprint
	bridge._replace_json_recoverably(snapshot + "/manifest.json", manifest)
	bridge._replace_json_recoverably(root + "/current.json", {"session_id": bridge._session_id, "manifest": "executor-session-7-tampered/manifest.json", "catalog_revision": 7})


func _catalog_manifest(bridge, revision, category_counts, chunks):
	var manifest = {
		"schema_version": "1.0",
		"session_id": bridge._session_id,
		"catalog_revision": revision,
		"catalog_fingerprint": "",
		"snapshot_at": "2026-08-11T20:00:00.0000000+00:00",
		"complete": true,
		"category_counts": category_counts,
		"chunks": chunks,
		"errors": [],
	}
	manifest.catalog_fingerprint = bridge._catalog_manifest_fingerprint(manifest)
	return manifest


func _empty_category_counts():
	var counts = {}
	for category in CATALOG_CATEGORIES:
		counts[category] = 0
	return counts


func _catalog_entry(asset_ref):
	return {
		"asset_ref": asset_ref,
		"category": "Walls",
		"display_name": "fixture wall",
		"resource_fingerprint": RESOURCE_IDENTITY.sha256_text(),
		"pack_id": null,
		"pack_name": null,
		"search_terms": ["fixture wall"],
		"tags": [],
		"preview_hash": null,
		"allow_third_party_use": false,
		"generated": false,
	}


func _asset_list(category):
	_asset_list_calls += 1
	return [RESOURCE_IDENTITY] if category == "Walls" else []


func _texture(_resource_identity):
	return ImageTexture.new()
