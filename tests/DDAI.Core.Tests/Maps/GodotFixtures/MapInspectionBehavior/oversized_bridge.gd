extends "res://ddai_bridge.gd"


func _inspect_map_payload(_payload):
	var oversized = "x"
	for _index in range(20):
		oversized += oversized
	return {"ok": true, "payload": {"oversized": oversized}}
