# DDAI Durable Map Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the session-derived `map_id` with a durable per-map UUID stored in `Global.ModMapData`, so map identity survives mod reload, application restart, and map close/reopen.

**Architecture:** The bridge resolves identity once per map load. It adopts a valid stored UUID, or mints one in memory without writing. The cached UUID is written into `Global.ModMapData` only during `apply_plan` preflight, so read-only commands never dirty the map and the identity value never changes mid-session. A new `map_identity_state` field reports `bound`, `pending`, or `unbound`.

**Tech Stack:** Godot 3.5.3 GDScript, Dungeondraft Modding API (1.2.0.1), .NET 9, xUnit, pinned Godot fixture harnesses.

**Design:** [2026-08-14-ddai-durable-map-identity-design.md](../specs/2026-08-14-ddai-durable-map-identity-design.md)

---

## Global Constraints

- Dungeondraft 1.2.0.1 only. The exact executable SHA-256 gate is unchanged.
- `Global.ModMapData` is undocumented. Task 1 is a read-only probe and Task 2 is a live gate. Do not implement binding before Task 2 passes.
- Never mutate the user's working map. Certification uses a newly created disposable map.
- Never write to `ModMapData` from `status`, `inspect_map`, or any catalog command.
- Never remove or rewrite another mod's key inside `ModMapData`.
- Preserve existing unrelated dirty files: `AGENTS.md`, `CLAUDE.md`, `.claude/`, `.gitignore`, and `.superpowers/sdd/2026-08-09-ddai-windows-connector/task-3-report.md`.
- The pinned Godot runtime is at `tools/godot-3.5.3/Godot_v3.5.3-stable_win64.exe` and is gitignored.

## File responsibilities

- `mods/DDAI/scripts/ddai_operation_certifier.gd` — add a read-only `probe_map_data` returning a closed-shape record. No mutation.
- `mods/DDAI/scripts/ddai_bridge.gd` — replace `_current_map_id`, add resolution, minting, binding, and state reporting.
- `src/DDAI.Core/Maps/MapSnapshotContracts.cs` — carry `map_identity_state`.
- `src/DDAI.App/DdaiStatusService.cs`, `src/DDAI.App/DdaiMapInspectionService.cs` — surface the field.
- `src/DDAI.App/Mcp/DdaiMapTools.cs` — expose it in tool output.
- `tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs` — static source and pinned-Godot behavior tests.
- `tests/DDAI.Core.Tests/Executors/GodotFixtures/MapIdentity/` — Godot fixture harness.
- `tests/DDAI.Core.Tests/Maps/MapSnapshotContractTests.cs` — contract round-trip for the new field.
- `docs/superpowers/reports/2026-08-14-ddai-durable-map-identity-report.md` — live evidence.

---

### Task 1: Read-only ModMapData probe

**Files:**
- Modify: `mods/DDAI/scripts/ddai_operation_certifier.gd`
- Create: `tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs`

- [ ] **Step 1: Write the failing static test**

Create `tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs`:

```csharp
using System.IO;

namespace DDAI.Core.Tests.Executors;

public sealed class DungeondraftMapIdentityTests
{
    [Fact]
    public void MapDataProbeIsReadOnlyAndReturnsAClosedShape()
    {
        var script = ReadCertifier();
        var probe = FunctionBody(script, "probe_map_data");

        Assert.Contains("ModMapData", probe, StringComparison.Ordinal);
        Assert.Contains("available", probe, StringComparison.Ordinal);
        Assert.Contains("is_dictionary", probe, StringComparison.Ordinal);
        Assert.Contains("ddai_key_present", probe, StringComparison.Ordinal);
        Assert.Contains("stored_uuid_valid", probe, StringComparison.Ordinal);
        Assert.Contains("foreign_key_count", probe, StringComparison.Ordinal);
        Assert.Contains("reason", probe, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "erase(", "clear(", ".Save(", "queue_free" })
        {
            Assert.DoesNotContain(forbidden, probe, StringComparison.Ordinal);
        }
    }

    private static string ReadCertifier()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "mods", "DDAI", "scripts", "ddai_operation_certifier.gd"));
    }

    private static string FunctionBody(string script, string name)
    {
        var marker = "\nfunc " + name + "(";
        var start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Function {name} not found.");
        var next = script.IndexOf("\nfunc ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? script[start..] : script[start..next];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DDAI.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~DungeondraftMapIdentityTests`
Expected: FAIL with "Function probe_map_data not found."

- [ ] **Step 3: Implement the probe**

Append to `mods/DDAI/scripts/ddai_operation_certifier.gd`:

```gdscript
const DDAI_MAP_DATA_KEY = "org.ddai.status_bridge"


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
		if not ("0123456789abcdef".find(value[index]) >= 0):
			return false
	return true
```

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~DungeondraftMapIdentityTests`
Expected: PASS, 1 test

- [ ] **Step 5: Add the pinned-Godot behavioral fixture**

Static substring tests cannot verify arithmetic, validator behavior, or read-only-ness. Create `tests/DDAI.Core.Tests/Executors/GodotFixtures/MapIdentity/` modeled on the existing `GodotFixtures/OperationCertifier` fixture, and drive the real `probe_map_data` with a `FakeGlobal` across these shapes:

| Shape | Required record |
|---|---|
| `ModMapData` absent | `available` false |
| present but a String | `available` true, `is_dictionary` false |
| Dictionary, no DDAI key, 2 foreign keys | `foreign_key_count` 2, `ddai_key_present` false |
| Dictionary, DDAI key + 2 foreign keys | `foreign_key_count` 2, `ddai_key_present` true |
| DDAI value is a String | `stored_uuid_valid` false |
| valid 64-char lowercase hex `map_uuid` | `stored_uuid_valid` true |
| uppercase or 63-char `map_uuid` | `stored_uuid_valid` false |

Prove read-only-ness structurally: snapshot with `.duplicate(true)` before the call and assert deep equality after. Grepping for forbidden call text cannot catch subscript assignment, which is the natural mutation vector for a GDScript Dictionary.

Add the C# driver to `DungeondraftMapIdentityTests.cs` following the existing pinned-Godot test shape. The runtime is at `tools/godot-3.5.3/Godot_v3.5.3-stable_win64.exe`.

- [ ] **Step 6: Guard against a false-negative probe**

`_has_readable_property` scans `get_property_list()`. If Dungeondraft exposes `ModMapData` as a plain C# field rather than a registered Godot property, it may not enumerate even though `Global.get("ModMapData")` succeeds. Because Task 2 gates the whole design, a false `available: false` would abandon a valid design for the wrong reason.

Report availability when EITHER `_has_readable_property` succeeds OR a direct `get()` returns non-null, and add a `discovery` field valued `property_list`, `direct_get`, or `none` so the live gate can distinguish the cases.

- [ ] **Step 7: Commit**

```bash
git add -- mods/DDAI/scripts/ddai_operation_certifier.gd tests/DDAI.Core.Tests/Executors
git commit -m "test: probe Dungeondraft map data storage read-only"
```

---

### Task 2: Live gate — prove ModMapData exists and persists

**This task is a hard gate. If it fails, stop and return to the design.**

**Files:**
- Modify: `mods/DDAI/scripts/ddai_bridge.gd` (temporary probe reporting only)
- Create: `docs/superpowers/reports/2026-08-14-ddai-map-data-probe-report.md`

- [ ] **Step 1: Report the probe through status**

In `ddai_bridge.gd`, inside `_certify_operation_routes()`, after the certifier is constructed, capture the probe and store it:

```gdscript
	if certifier.has_method("probe_map_data"):
		_map_data_probe = certifier.probe_map_data(Global)
```

Declare near the other state variables (around line 62, beside `var _operation_certifications = []`):

```gdscript
var _map_data_probe = {}
```

Add it to the status payload dictionary (around line 3633, beside `"operation_certifications"`):

```gdscript
		"map_data_probe": _map_data_probe.duplicate(true),
```

- [ ] **Step 2: Install the mod and reload Dungeondraft normally**

Ask the user to close Dungeondraft normally. Then:

```powershell
Copy-Item -Recurse -Force mods\DDAI "$env:LOCALAPPDATA\DDAI\DungeondraftMods\"
```

Ask the user to reopen Dungeondraft and create a **new disposable map**, not the working map.

- [ ] **Step 3: Read the probe**

Call `ddai_status` through the connector and record `map_data_probe`.

Required to continue: `available == true` and `is_dictionary == true`.

If `available` is false, **stop**. The design is void. Record the failure in the report and return to the spec.

- [ ] **Step 4: Prove persistence manually**

This step proves the round trip before any binding code exists. In Dungeondraft's console-free environment the bridge cannot be driven ad hoc, so prove it through the first real binding instead: proceed to Task 3, then return here and complete Task 6's live proof. Mark this step complete only once Task 6 passes.

- [ ] **Step 5: Write the probe report**

Create `docs/superpowers/reports/2026-08-14-ddai-map-data-probe-report.md` recording the exact probe record, Dungeondraft version, session id, and whether the gate passed.

- [ ] **Step 6: Commit**

```bash
git add -- mods/DDAI/scripts/ddai_bridge.gd docs/superpowers/reports/2026-08-14-ddai-map-data-probe-report.md
git commit -m "test: report live Dungeondraft map data probe"
```

---

### Task 3: Resolve and mint identity in the bridge

**Files:**
- Modify: `mods/DDAI/scripts/ddai_bridge.gd`
- Modify: `tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs`

- [ ] **Step 1: Write the failing static tests**

Add to `DungeondraftMapIdentityTests.cs`:

```csharp
    [Fact]
    public void CurrentMapIdUsesStoredUuidAndNeverSessionState()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_current_map_id");

        Assert.DoesNotContain("_session_id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("get_instance_id", body, StringComparison.Ordinal);
        Assert.Contains("_resolved_map_uuid", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveMapIdentityAdoptsValidStoredUuidAndMintsWithoutWriting()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_resolve_map_identity");

        Assert.Contains("ModMapData", body, StringComparison.Ordinal);
        Assert.Contains("map_uuid", body, StringComparison.Ordinal);
        Assert.Contains("_is_valid_map_uuid", body, StringComparison.Ordinal);
        Assert.Contains("bound", body, StringComparison.Ordinal);
        Assert.Contains("pending", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ModMapData[", body, StringComparison.Ordinal);
    }

    private static string ReadBridge()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "mods", "DDAI", "scripts", "ddai_bridge.gd"));
    }
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~DungeondraftMapIdentityTests`
Expected: FAIL, "Function _resolve_map_identity not found."

- [ ] **Step 3: Implement resolution and minting**

Add state variables in `ddai_bridge.gd` beside `var _map_data_probe = {}`:

```gdscript
var _resolved_map_uuid = ""
var _map_identity_state = "unbound"
var _resolved_map_world_id = 0
```

Add the constant beside `MAILBOX_ROOT`:

```gdscript
const MAP_DATA_KEY = "org.ddai.status_bridge"
```

Replace `_current_map_id()` entirely:

```gdscript
func _current_map_id():
	_resolve_map_identity()
	return _resolved_map_uuid


func _resolve_map_identity():
	if Global.World == null:
		_resolved_map_uuid = ""
		_map_identity_state = "unbound"
		_resolved_map_world_id = 0
		return
	var world_id = Global.World.get_instance_id()
	if _resolved_map_uuid != "" and _resolved_map_world_id == world_id:
		return
	_resolved_map_world_id = world_id
	var stored = _read_stored_map_uuid()
	if stored != "":
		_resolved_map_uuid = stored
		_map_identity_state = "bound"
		return
	_resolved_map_uuid = _mint_map_uuid()
	_map_identity_state = "pending"


func _read_stored_map_uuid():
	if not _has_property(Global, "ModMapData"):
		return ""
	var data = Global.get("ModMapData")
	if typeof(data) != TYPE_DICTIONARY or not data.has(MAP_DATA_KEY):
		return ""
	var owned = data[MAP_DATA_KEY]
	if typeof(owned) != TYPE_DICTIONARY:
		return ""
	var value = owned.get("map_uuid", null)
	return value if _is_valid_map_uuid(value) else ""


func _mint_map_uuid():
	return (
		_framed_string("session_id=", _session_id)
		+ "world_instance_id=" + str(_resolved_map_world_id)
		+ "ticks=" + str(OS.get_ticks_usec())
		+ "\n").sha256_text()


func _is_valid_map_uuid(value):
	if typeof(value) != TYPE_STRING or value.length() != 64:
		return false
	for index in range(64):
		if "0123456789abcdef".find(value[index]) < 0:
			return false
	return true


func _has_property(value, property_name):
	if value == null or typeof(property_name) != TYPE_STRING:
		return false
	for property in value.get_property_list():
		if typeof(property) == TYPE_DICTIONARY and property.get("name", "") == property_name:
			return true
	return false
```

- [ ] **Step 4: Run GREEN and the parser test**

Run: `dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter "FullyQualifiedName~DungeondraftMapIdentityTests|FullyQualifiedName~GdscriptBridge_ParsesWithPinnedGodot353"`
Expected: PASS, 4 tests

- [ ] **Step 5: Commit**

```bash
git add -- mods/DDAI/scripts/ddai_bridge.gd tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs
git commit -m "feat: resolve durable map identity without session state"
```

---

### Task 4: Bind identity during apply preflight

**Files:**
- Modify: `mods/DDAI/scripts/ddai_bridge.gd`
- Modify: `tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs`

- [ ] **Step 1: Write the failing static test**

Add to `DungeondraftMapIdentityTests.cs`:

```csharp
    [Fact]
    public void BindMapIdentityWritesOnlyTheOwnedKeyAndOnlyWhenPending()
    {
        var script = ReadBridge();
        var body = FunctionBody(script, "_bind_map_identity");

        Assert.Contains("_map_identity_state != \"pending\"", body, StringComparison.Ordinal);
        Assert.Contains("MAP_DATA_KEY", body, StringComparison.Ordinal);
        Assert.Contains("map_uuid", body, StringComparison.Ordinal);
        Assert.Contains("\"bound\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("clear()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("erase(", body, StringComparison.Ordinal);

        var preflight = FunctionBody(script, "_preflight_universal_plan");
        Assert.Contains("_bind_map_identity()", preflight, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~DungeondraftMapIdentityTests`
Expected: FAIL, "Function _bind_map_identity not found."

- [ ] **Step 3: Implement binding**

Add to `ddai_bridge.gd`:

```gdscript
func _bind_map_identity():
	_resolve_map_identity()
	if _map_identity_state != "pending" or _resolved_map_uuid == "":
		return _map_identity_state == "bound"
	if not _has_property(Global, "ModMapData"):
		return false
	var data = Global.get("ModMapData")
	if typeof(data) != TYPE_DICTIONARY:
		return false
	var owned = {}
	if data.has(MAP_DATA_KEY) and typeof(data[MAP_DATA_KEY]) == TYPE_DICTIONARY:
		owned = data[MAP_DATA_KEY]
	owned["map_uuid"] = _resolved_map_uuid
	data[MAP_DATA_KEY] = owned
	_map_identity_state = "bound"
	return true
```

In `_preflight_universal_plan`, call `_bind_map_identity()` immediately before the existing map identity comparison, so the plan's `expected_map_id` is checked against the same value that will be persisted.

- [ ] **Step 4: Run GREEN**

Run: `dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~DungeondraftMapIdentityTests`
Expected: PASS, 5 tests

- [ ] **Step 5: Commit**

```bash
git add -- mods/DDAI/scripts/ddai_bridge.gd tests/DDAI.Core.Tests/Executors/DungeondraftMapIdentityTests.cs
git commit -m "feat: bind durable map identity on first mutation"
```

---

### Task 5: Expose map_identity_state through the connector

**Files:**
- Modify: `mods/DDAI/scripts/ddai_bridge.gd`
- Modify: `src/DDAI.Core/Maps/MapSnapshotContracts.cs`
- Modify: `src/DDAI.App/DdaiStatusService.cs`
- Modify: `src/DDAI.App/DdaiMapInspectionService.cs`
- Modify: `src/DDAI.App/Mcp/DdaiMapTools.cs`
- Modify: `tests/DDAI.Core.Tests/Maps/MapSnapshotContractTests.cs`

- [ ] **Step 1: Write the failing contract test**

Add to `MapSnapshotContractTests.cs`:

```csharp
    [Theory]
    [InlineData("bound")]
    [InlineData("pending")]
    [InlineData("unbound")]
    public void MapIdentityStateRoundTrips(string state)
    {
        var json = $$"""{"map_identity_state":"{{state}}"}""";
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(state, document.RootElement.GetProperty("map_identity_state").GetString());
    }

    [Fact]
    public void UnknownMapIdentityStateIsRejected()
    {
        Assert.False(MapSnapshotContracts.IsKnownMapIdentityState("bogus"));
        Assert.False(MapSnapshotContracts.IsKnownMapIdentityState(null));
        Assert.True(MapSnapshotContracts.IsKnownMapIdentityState("bound"));
    }
```

- [ ] **Step 2: Run RED**

Run: `dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --filter FullyQualifiedName~MapSnapshotContractTests`
Expected: FAIL, `IsKnownMapIdentityState` does not exist.

- [ ] **Step 3: Add the contract helper**

In `src/DDAI.Core/Maps/MapSnapshotContracts.cs`:

```csharp
    public static readonly string[] MapIdentityStates = ["bound", "pending", "unbound"];

    public static bool IsKnownMapIdentityState(string? value) =>
        value is not null && Array.IndexOf(MapIdentityStates, value) >= 0;
```

- [ ] **Step 4: Emit the field from the bridge**

In `ddai_bridge.gd`, add to the status payload dictionary beside `"map_id"`:

```gdscript
		"map_identity_state": _map_identity_state,
```

Add the same key to the inspection response dictionary beside its `"map_id"` entry.

- [ ] **Step 5: Surface it in the .NET services**

In `DdaiStatusService.cs` and `DdaiMapInspectionService.cs`, read `map_identity_state` from the bridge payload and include it in the response. Where the value is missing or unknown per `MapSnapshotContracts.IsKnownMapIdentityState`, emit `"unbound"` rather than defaulting to `"bound"`.

- [ ] **Step 6: Run GREEN and full Core suite**

Run: `dotnet test DDAI.slnx -c Release`
Expected: PASS, all tests

- [ ] **Step 7: Commit**

```bash
git add -- mods/DDAI src/DDAI.Core/Maps/MapSnapshotContracts.cs src/DDAI.App tests/DDAI.Core.Tests
git commit -m "feat: report map identity durability state"
```

---

### Task 6: Live certification

**Files:**
- Create: `docs/superpowers/reports/2026-08-14-ddai-durable-map-identity-report.md`
- Modify: `mods/DDAI/scripts/ddai_bridge.gd` (bump `MOD_VERSION` to `0.3.0`)

- [ ] **Step 1: Run the full gate**

```powershell
dotnet test DDAI.slnx -c Release
dotnet build DDAI.slnx -c Release --no-restore
git diff --check
```

- [ ] **Step 2: Bump the mod version**

Set `const MOD_VERSION = "0.3.0"` in `ddai_bridge.gd`. Update `DdaiCapabilityService.ExpectedModVersion` to match and re-run the suites.

- [ ] **Step 3: Install and reload**

Ask the user to close Dungeondraft normally, then publish and run setup so the installed mod and executable hashes match source. Confirm exactly one `DDAI` directory under the active Mods root.

- [ ] **Step 4: Prove the durable round trip**

On a newly created disposable map:

1. Call `ddai_status`. Record `map_id` and require `map_identity_state == "pending"`.
2. Apply one `wall_polyline` plan.
3. Call `ddai_status`. Require the identical `map_id` and `map_identity_state == "bound"`.
4. Save the map, close Dungeondraft normally, reopen it, and open the same map.
5. Call `ddai_status`. Require the identical `map_id` and `map_identity_state == "bound"`.

Success criterion 1 is met only when step 5 returns the exact value recorded in step 1.

- [ ] **Step 5: Prove reload stability and map distinctness**

1. Use Dungeondraft's normal **Reload Mods** action. Require the identical `map_id`.
2. Open a different disposable map. Require a different `map_id`.

- [ ] **Step 6: Prove read-only isolation**

On a freshly opened, never-mutated map, call `ddai_status` and `ddai_inspect_map`, then close the map. Dungeondraft must not prompt to save changes.

- [ ] **Step 7: Write the report and commit**

Record every observed `map_id`, state transition, Dungeondraft version, and the installed/source hashes. Record honestly if any step failed.

```bash
git add -- mods/DDAI src/DDAI.App/DdaiCapabilityService.cs docs/superpowers/reports/2026-08-14-ddai-durable-map-identity-report.md
git commit -m "test: certify durable map identity"
```

---

## Success criteria

1. Reopening the same saved map yields the identical `map_id`.
2. A mod reload without reopening the map yields the identical `map_id`.
3. Two different maps never share an identity, except by file copy.
4. No read-only command modifies `ModMapData` or marks the map dirty.
5. Full Release Core and App suites pass with zero warnings and zero errors.
