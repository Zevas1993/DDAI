extends Node


func _ready():
	var bridge_script = load("res://ddai_bridge.gd")
	var parsed = bridge_script != null and bridge_script.can_instance()
	print("DDAI_PARSE_OK:" + str(parsed))
	get_tree().quit(0 if parsed else 1)
