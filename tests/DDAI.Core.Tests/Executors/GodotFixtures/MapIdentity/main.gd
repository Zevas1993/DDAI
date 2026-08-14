extends Node

const CertifierScript = preload("res://ddai_operation_certifier.gd")

class FakeGlobalAbsent:
	extends Reference
	# Declared but unset. The probe reads the member directly, so "unavailable"
	# is expressed as a null value rather than a missing property -- reaching for
	# a missing property is exactly the reflection that crashed the host.
	var ModMapData = null

class FakeGlobalWithData:
	extends Reference
	var ModMapData

	func _init(value):
		ModMapData = value

# Mirrors the host that crashed Dungeondraft 1.2.0.1: a reflective property
# lookup by name reaches a handler that never terminates. Here it merely records
# that it was reached, so a regression fails the test instead of running the
# stack out. ModMapData is a normal declared member, so a probe using direct
# access never trips it.
class FakeGlobalHostileReflection:
	extends Reference
	var ModMapData
	var reflection_used = false

	func _init(payload):
		ModMapData = payload

	func _get(_property):
		reflection_used = true
		return null

func _ready():
	var certifier = CertifierScript.new()
	var all_ok = true

	# Case: ModMapData entirely absent (no property, no direct get either).
	var absent_record = certifier.probe_map_data(FakeGlobalAbsent.new())
	var absent_ok = (
		not absent_record.available
		and absent_record.discovery == "none"
		and not absent_record.is_dictionary
		and absent_record.foreign_key_count == 0
		and absent_record.reason == "map_data_unavailable")
	all_ok = all_ok and absent_ok

	# Case: present but a String, not a Dictionary.
	var string_record = certifier.probe_map_data(FakeGlobalWithData.new("not-a-dictionary"))
	var string_ok = (
		string_record.available
		and string_record.discovery == "direct_member"
		and not string_record.is_dictionary
		and string_record.reason == "map_data_not_dictionary")
	all_ok = all_ok and string_ok

	# Case: Dictionary, no DDAI key, two foreign keys.
	var foreign_only_source = {"alpha": 1, "beta": 2}
	var foreign_only_snapshot = foreign_only_source.duplicate(true)
	var foreign_only_record = certifier.probe_map_data(FakeGlobalWithData.new(foreign_only_source))
	var foreign_only_ok = (
		foreign_only_record.available
		and foreign_only_record.is_dictionary
		and not foreign_only_record.ddai_key_present
		and foreign_only_record.foreign_key_count == 2
		and _deep_equal(foreign_only_source, foreign_only_snapshot))
	all_ok = all_ok and foreign_only_ok

	# Case: Dictionary with the DDAI key plus two foreign keys, and a valid
	# 64-char lowercase hex map_uuid stored under it. Built by repetition
	# (rather than a hand-typed literal) so the length is exactly 64 by
	# construction.
	var hex_chars = "0123456789abcdef"
	var valid_uuid = ""
	for _repeat in range(4):
		valid_uuid += hex_chars
	var with_key_source = {
		"alpha": 1,
		"beta": 2,
		CertifierScript.DDAI_MAP_DATA_KEY: {"map_uuid": valid_uuid},
	}
	var with_key_snapshot = with_key_source.duplicate(true)
	var with_key_record = certifier.probe_map_data(FakeGlobalWithData.new(with_key_source))
	var with_key_ok = (
		with_key_record.available
		and with_key_record.ddai_key_present
		and with_key_record.foreign_key_count == 2
		and with_key_record.stored_uuid_valid
		and _deep_equal(with_key_source, with_key_snapshot))
	all_ok = all_ok and with_key_ok

	# Case: DDAI key present but its value is a String, not a Dictionary.
	var string_owned_source = {CertifierScript.DDAI_MAP_DATA_KEY: "not-a-dictionary"}
	var string_owned_snapshot = string_owned_source.duplicate(true)
	var string_owned_record = certifier.probe_map_data(FakeGlobalWithData.new(string_owned_source))
	var string_owned_ok = (
		string_owned_record.ddai_key_present
		and not string_owned_record.stored_uuid_valid
		and _deep_equal(string_owned_source, string_owned_snapshot))
	all_ok = all_ok and string_owned_ok

	# Case: uppercase-hex or 63-char map_uuid values are both rejected.
	var uppercase_uuid = valid_uuid.to_upper()
	var short_uuid = valid_uuid.substr(0, 63)
	var uppercase_source = {CertifierScript.DDAI_MAP_DATA_KEY: {"map_uuid": uppercase_uuid}}
	var short_source = {CertifierScript.DDAI_MAP_DATA_KEY: {"map_uuid": short_uuid}}
	var uppercase_snapshot = uppercase_source.duplicate(true)
	var short_snapshot = short_source.duplicate(true)
	var uppercase_record = certifier.probe_map_data(FakeGlobalWithData.new(uppercase_source))
	var short_record = certifier.probe_map_data(FakeGlobalWithData.new(short_source))
	var malformed_ok = (
		not uppercase_record.stored_uuid_valid
		and not short_record.stored_uuid_valid
		and _deep_equal(uppercase_source, uppercase_snapshot)
		and _deep_equal(short_source, short_snapshot))
	all_ok = all_ok and malformed_ok

	# Crash regression guard. Probing this host reflectively is what took
	# Dungeondraft down; the probe must read the declared member directly and
	# never reach the reflection handler at all.
	var direct_only_source = {
		"alpha": 1,
		CertifierScript.DDAI_MAP_DATA_KEY: {"map_uuid": valid_uuid},
	}
	var direct_only_snapshot = direct_only_source.duplicate(true)
	var direct_only_global = FakeGlobalHostileReflection.new(direct_only_source)
	var direct_only_record = certifier.probe_map_data(direct_only_global)
	var direct_only_ok = (
		not direct_only_global.reflection_used
		and direct_only_record.available
		and direct_only_record.discovery == "direct_member"
		and direct_only_record.ddai_key_present
		and direct_only_record.foreign_key_count == 1
		and _deep_equal(direct_only_source, direct_only_snapshot))
	all_ok = all_ok and direct_only_ok

	var read_only = (
		_deep_equal(foreign_only_source, foreign_only_snapshot)
		and _deep_equal(with_key_source, with_key_snapshot)
		and _deep_equal(string_owned_source, string_owned_snapshot)
		and _deep_equal(uppercase_source, uppercase_snapshot)
		and _deep_equal(short_source, short_snapshot)
		and _deep_equal(direct_only_source, direct_only_snapshot))

	print("DDAI_MAP_IDENTITY_ABSENT_CLOSED:" + str(absent_ok))
	print("DDAI_MAP_IDENTITY_NON_DICTIONARY_CLOSED:" + str(string_ok))
	print("DDAI_MAP_IDENTITY_FOREIGN_ONLY_COUNTED:" + str(foreign_only_ok))
	print("DDAI_MAP_IDENTITY_DDAI_KEY_COUNTED:" + str(with_key_ok))
	print("DDAI_MAP_IDENTITY_STRING_OWNED_INVALID:" + str(string_owned_ok))
	print("DDAI_MAP_IDENTITY_MALFORMED_UUID_REJECTED:" + str(malformed_ok))
	print("DDAI_MAP_IDENTITY_NO_HOST_REFLECTION:" + str(direct_only_ok))
	print("DDAI_MAP_IDENTITY_READ_ONLY:" + str(read_only))
	get_tree().quit(0 if all_ok and read_only else 1)


# Godot 3.5's GDScript Dictionary "==" compares by reference identity, not
# content, so a real deep comparison is needed to prove probe_map_data left
# the fetched dictionaries byte-for-byte unchanged.
func _deep_equal(a, b):
	if typeof(a) != typeof(b):
		return false
	if typeof(a) == TYPE_DICTIONARY:
		if a.size() != b.size():
			return false
		for key in a.keys():
			if not b.has(key) or not _deep_equal(a[key], b[key]):
				return false
		return true
	if typeof(a) == TYPE_ARRAY:
		if a.size() != b.size():
			return false
		for index in range(a.size()):
			if not _deep_equal(a[index], b[index]):
				return false
		return true
	return a == b
