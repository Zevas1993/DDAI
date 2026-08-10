# DDAI Rectangular Room Mutation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `ddai_validate_plan` and crash-safe, idempotent `ddai_apply_plan` tools that create one native rectangular Dungeondraft wall and prove normal Dungeondraft undo removes it.

**Architecture:** Extend the existing `MapPlan` contract with one grid-relative room, keep validation and MCP orchestration in `ddai.exe`, and send one canonical `apply_plan` request through the existing atomic mailbox. The GDScript bridge writes an immutable prepared mutation intent before calling documented `World`, `Level`, `WorldUI`, and `WallTool` APIs; a restart after the uncertain native boundary returns `mutation_outcome_unknown` instead of replaying the room.

**Tech Stack:** .NET 9, C# 13, xUnit 2.9.2, official `ModelContextProtocol` 1.4.1 SDK, Godot 3.x-compatible GDScript, Windows PowerShell, atomic JSON mailbox, Dungeondraft 1.2.0.1.

## Global Constraints

- Target Windows 10/11 x64 and Dungeondraft `1.2.0.1`; setup and runtime must not require administrator rights.
- Preserve the full connector objective: native editable maps from ChatGPT, Claude Desktop, Gemini, and other supported MCP clients; this room is the first mutation slice, not the final product.
- Never edit, unpack, patch, or redistribute Dungeondraft binaries, PCK data, maps, or purchased assets.
- Keep Dungeondraft free of inbound network listeners; all loaded-mod communication stays beneath `user://ddai` and the existing 1 MiB mailbox cap.
- Preserve the strict wire timestamp language, canonical request naming, correlation checks, non-overwriting atomic publication, conflict retention, and one-durable-transition-at-a-time recovery model.
- Preserve status behavior and the currently live-stable prohibition on zero-argument Dictionary built-ins in production GDScript.
- Use only the fixed documented runtime surface: `Global.World.Width`, `Height`, `GridSize`, `CurrentLevelId`, `GetLevelByID(id)`, `Level.Walls.get_children()`, `Global.Editor.Tools["WallTool"]`, `Global.WorldUI`, `Tool.Enable()`, `WorldUI.ClearPolyline()`, `WorldUI.AddPolyPoint()`, `WallTool.Confirm()`, and `Tool.Disable()`.
- Do not use runtime reflection, `get_property_list`, private scene construction, fixed pixel-size guesses, direct `.dungeondraft_map` editing, or Custom Snap as a dependency.
- `ddai_apply_plan` supports exactly one room, `mode: "add"`, `base_revision: 0`, and an explicit blank-map operator precondition in this slice.
- The same request ID and canonical-plan fingerprint is idempotent. The same request ID with different plan content fails closed.
- Never automatically replay a prepared mutation after process restart. Retain an ambiguous intent and publish `mutation_outcome_unknown`.
- Do not claim programmatic undo. A successful result says `undo_available: true` only after live proof that one normal Dungeondraft Undo removes the whole wall.
- Never update the installed mod while Dungeondraft is running. Ask the user to close it normally before setup and reopen it normally for live certification.
- Preserve unrelated dirty work: `.gitignore`, `.superpowers/sdd/2026-08-09-ddai-windows-connector/task-3-report.md`, `.claude/`, `AGENTS.md`, and `CLAUDE.md` remain outside every commit.
- Baseline at plan creation is 314/314 Release tests: 226 Core and 88 App.

---

### Task 1: Extend the canonical map-plan contract with one rectangular room

**Files:**
- Modify: `src/DDAI.Core/MapPlans/MapPlan.cs`
- Modify: `src/DDAI.Core/MapPlans/MapPlanJson.cs`
- Create: `src/DDAI.Core/MapPlans/RectangularRoomPlanValidation.cs`
- Modify: `src/DDAI.Core/MapPlans/MapPlanValidation.cs`
- Modify: `tests/DDAI.Core.Tests/MapPlanJsonTests.cs`
- Modify: `tests/DDAI.Core.Tests/MapPlanValidatorTests.cs`
- Create: `tests/DDAI.Core.Tests/RectangularRoomPlanValidatorTests.cs`

**Interfaces:**
- Produces: `MapRoom(string Id, int X, int Y, int Width, int Height)`.
- Produces: `MapPlan.Rooms : IReadOnlyList<MapRoom>` with a missing-wire default of an empty list.
- Produces: `MapPlanJson.SerializeToElement(MapPlan) -> JsonElement` and `MapPlanJson.Fingerprint(MapPlan) -> string`.
- Produces: `RectangularRoomPlanValidator.Validate(MapPlan) -> MapPlanValidationResult`.
- Stable issue order: envelope, rooms collection, mode, base revision, room count, room object/id/x/y/width/height, then bounds.

- [ ] **Step 1: Write failing JSON and room-validator tests**

Add direct tests with this valid plan factory:

```csharp
private static MapPlan ValidPlan() => new()
{
    SchemaVersion = MapPlan.CurrentSchemaVersion,
    RequestId = "room-job-001",
    BaseRevision = 0,
    Mode = MapOperationMode.Add,
    Canvas = new MapCanvas(40, 30),
    Rooms = [new MapRoom("room-entrance", 8, 7, 10, 8)],
};
```

Change the stable serialization expectation to exactly:

```json
{"schema_version":"1.0","request_id":"room-job-001","base_revision":0,"mode":"add","canvas":{"width":40,"height":30},"rooms":[{"id":"room-entrance","x":8,"y":7,"width":10,"height":8}]}
```

Add `Deserialize_LegacyEnvelopeWithoutRoomsDefaultsToEmpty` using the existing envelope JSON and assert `Assert.Empty(plan.Rooms)`. Add `Fingerprint_IsLowercaseSha256OfLanguageNeutralCanonicalInput`. Assert the complete canonical input string literally, including `\n` separators and UTF-8 byte lengths, then compute the expected hash independently with `SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput))`. Include an identifier containing a non-ASCII character and a quote so the test proves the fingerprint is independent of JSON escaping.

Add room validation theories for:

```csharp
[Theory]
[InlineData("replace", 0, "unsupported_mode", "mode")]
[InlineData("add", 1, "unsupported_base_revision", "base_revision")]
public void Validate_RejectsUnsupportedCommandSemantics(
    string mode, long revision, string code, string path)
```

and individual tests for an unsafe plan request ID, empty/null room collection, two rooms, null room item, unsafe room ID, negative x/y, zero width/height, and each right/bottom out-of-bounds case. Include `int.MaxValue` coordinates and dimensions to prove bounds checks do not add values before range validation.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MapPlanJsonTests|FullyQualifiedName~MapPlanValidatorTests|FullyQualifiedName~RectangularRoomPlanValidatorTests"
```

Expected: compilation fails because `MapRoom`, `MapPlan.Rooms`, `SerializeToElement`, `Fingerprint`, and `RectangularRoomPlanValidator` do not exist.

- [ ] **Step 3: Add the room wire types and canonical helpers**

Use property-level JSON names and an explicit parameterless enum converter so the official MCP SDK and `MapPlanJson` expose the same snake-case object. Do not annotate the enum with the framework's default `JsonStringEnumConverter<MapOperationMode>` because an attribute-created default converter writes `Add` instead of the protocol value `add`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDAI.Core.MapPlans;

[JsonConverter(typeof(MapOperationModeJsonConverter))]
public enum MapOperationMode
{
    Add,
    Replace,
    Patch,
}

public sealed class MapOperationModeJsonConverter : JsonConverter<MapOperationMode>
{
    public override MapOperationMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "add" => MapOperationMode.Add,
                "replace" => MapOperationMode.Replace,
                "patch" => MapOperationMode.Patch,
                _ => throw new JsonException("Unsupported map operation mode."),
            }
            : throw new JsonException("Map operation mode must be a string.");

    public override void Write(Utf8JsonWriter writer, MapOperationMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            MapOperationMode.Add => "add",
            MapOperationMode.Replace => "replace",
            MapOperationMode.Patch => "patch",
            _ => throw new JsonException("Unsupported map operation mode."),
        });
}

public sealed record MapCanvas(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record MapRoom(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);
```

Annotate every `MapPlan` property with its current snake-case wire name and add:

```csharp
[JsonPropertyName("rooms")]
public IReadOnlyList<MapRoom> Rooms { get; init; } = [];
```

Remove the now-redundant options-level `JsonStringEnumConverter<MapOperationMode>` from `MapPlanJson`. Add round-trip assertions proving that both `MapPlanJson` and ordinary `JsonSerializer.Serialize(plan)` write exactly `"mode":"add"` and that unsupported casing such as `"Add"` is rejected.

Add to `MapPlanJson`:

```csharp
public static JsonElement SerializeToElement(MapPlan plan)
{
    ArgumentNullException.ThrowIfNull(plan);
    return JsonSerializer.SerializeToElement(plan, SerializerOptions);
}

public static string Fingerprint(MapPlan plan)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintInput(plan)));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}
```

`FingerprintInput` is a language-neutral scalar framing, not serialized JSON and not platform newlines. Append exact ASCII field tags, invariant-culture decimal integers, and a literal `\n`. Prefix every string with its UTF-8 byte count and `:`. The order is exactly:

```text
schema_version=<utf8-byte-count>:<value>\n
request_id=<utf8-byte-count>:<value>\n
base_revision=<decimal>\n
mode=<utf8-byte-count>:<lowercase-value>\n
canvas_width=<decimal>\n
canvas_height=<decimal>\n
rooms_count=<decimal>\n
room[0].id=<utf8-byte-count>:<value>\n
room[0].x=<decimal>\n
room[0].y=<decimal>\n
room[0].width=<decimal>\n
room[0].height=<decimal>\n
```

Repeat the five `room[i]` lines in array order for additional rooms. Use explicit `"\n"`, `Encoding.UTF8.GetByteCount`, and `CultureInfo.InvariantCulture`; never `AppendLine`, current culture, or JSON text. `FingerprintInput` may be `internal` with `InternalsVisibleTo` coverage, while `Fingerprint` remains the public contract.

- [ ] **Step 4: Implement deterministic command-specific validation**

In `MapPlanValidator.Validate`, keep `invalid_request_id` in its current ordered slot but extend it to reject `.`, `..`, `/`, and `\`, matching `AtomicMailbox.ValidateRequestId`. Add one general issue when `plan.Rooms is null`:

```csharp
issues.Add(new MapPlanValidationIssue("invalid_rooms", "rooms", "Rooms cannot be null."));
```

Create `RectangularRoomPlanValidator` and start from `MapPlanValidator.Validate(plan).Issues`. Add command-specific issues in the interface order. Treat a JSON `null` element in `rooms` as `room_required` before dereferencing it. Validate bounds only after canvas and scalar dimensions are valid, using subtraction rather than `X + Width`:

```csharp
if (room.X >= 0 && room.Width > 0 && plan.Canvas is { Width: > 0 } &&
    (room.X > plan.Canvas.Width || room.Width > plan.Canvas.Width - room.X))
{
    issues.Add(new MapPlanValidationIssue(
        "room_out_of_bounds", "rooms[0]", "Room exceeds the declared canvas width."));
}
```

Use the same form for y/height. Safe room IDs must use the same helper and therefore be nonblank, not `.` or `..`, and contain neither `/` nor `\`.

Assert these stable command-specific code/path pairs in order where applicable: `unsupported_mode`/`mode`, `unsupported_base_revision`/`base_revision`, `invalid_room_count`/`rooms`, `room_required`/`rooms[0]`, `invalid_room_id`/`rooms[0].id`, `invalid_room_x`/`rooms[0].x`, `invalid_room_y`/`rooms[0].y`, `invalid_room_width`/`rooms[0].width`, `invalid_room_height`/`rooms[0].height`, and `room_out_of_bounds`/`rooms[0]`.

- [ ] **Step 5: Run focused and full Core tests**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MapPlanJsonTests|FullyQualifiedName~MapPlanValidatorTests|FullyQualifiedName~RectangularRoomPlanValidatorTests"
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore
```

Expected: all focused tests pass; the complete Core suite passes with zero failures.

- [ ] **Step 6: Run the pre-commit impact gate and commit**

Run GitNexus impact for `MapPlan`, `MapPlanValidator`, and `MapPlanJson`, then:

```powershell
git add -- src/DDAI.Core/MapPlans tests/DDAI.Core.Tests/MapPlanJsonTests.cs tests/DDAI.Core.Tests/MapPlanValidatorTests.cs tests/DDAI.Core.Tests/RectangularRoomPlanValidatorTests.cs
git diff --cached --check
git commit -m "feat: define rectangular room map plans"
```

---

### Task 2: Add validated, fingerprinted apply-plan orchestration over the real mailbox

**Files:**
- Modify: `src/DDAI.Core/Mailbox/MailboxContracts.cs`
- Modify: `src/DDAI.Core/Mailbox/FakeModHarness.cs`
- Create: `src/DDAI.App/DdaiPlanService.cs`
- Create: `tests/DDAI.App.Tests/DdaiPlanServiceTests.cs`
- Modify: `tests/DDAI.Core.Tests/AtomicMailboxTests.cs`

**Interfaces:**
- Produces: `MailboxRequest.CreateApplyPlan(MapPlan plan, DateTimeOffset timestamp) -> MailboxRequest`.
- Produces: `DdaiPlanValidationResult(bool Valid, MapPlan? CanonicalPlan, IReadOnlyList<MapPlanValidationIssue> Issues, IReadOnlyList<string> RuntimeChecksRequired)`.
- Produces: `DdaiPlanApplyResult(bool Success, string Command, JsonElement Payload, MailboxErrorDetails? Error)`.
- Produces: `DdaiPlanService.Validate(MapPlan) -> DdaiPlanValidationResult`.
- Produces: `DdaiPlanService.ApplyAsync(MapPlan, TimeSpan, CancellationToken) -> Task<DdaiPlanApplyResult>`.
- Every bridge/mailbox `apply_plan` response payload contains the 64-character lowercase `plan_fingerprint`, including failures after a valid plan was published. Control-plane `invalid_plan` results contain ordered validation issues and never publish.

- [ ] **Step 1: Write failing request-factory and service tests**

Add `CreateApplyPlan_UsesPlanIdentityAndCanonicalPayload`:

```csharp
var plan = ValidPlan();
var request = MailboxRequest.CreateApplyPlan(plan, Timestamp);
Assert.Equal(plan.RequestId, request.RequestId);
Assert.Equal("apply_plan", request.Command);
Assert.Equal(MapPlanJson.Serialize(plan), request.Payload.GetRawText());
```

Create service tests that prove:

1. `Validate` returns the canonical plan and these exact runtime checks in order: `canvas`, `grid_scale`, `active_level`, `wall_tool`, `wall_count`.
2. Invalid plans return `invalid_plan`, include every validation issue, and leave `requests` empty.
3. A valid plan is published through the real `AtomicMailbox`, the fake bridge responds, and the result contains `applied: true`, `created_walls: 1`, room ID, undo fields, and the matching fingerprint.
4. Calling `ApplyAsync` twice with the same ID and plan returns the existing matching response without publishing a second request.
5. Reusing an ID with different geometry waits for the existing response, detects a different fingerprint, and returns `request_conflict`.
6. Missing response returns `apply_timeout`, explicitly marks the outcome unknown, warns against retrying with a new request ID, and does not delete request/processing evidence.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DdaiPlanServiceTests"
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CreateApplyPlan"
```

Expected: compilation fails on the missing factory, result records, and service.

- [ ] **Step 3: Implement the apply request and fake bridge response**

Add to `MailboxRequest`:

```csharp
public static MailboxRequest CreateApplyPlan(MapPlan plan, DateTimeOffset timestamp)
{
    ArgumentNullException.ThrowIfNull(plan);
    return new MailboxRequest
    {
        SchemaVersion = CurrentSchemaVersion,
        RequestId = plan.RequestId,
        Command = "apply_plan",
        Timestamp = timestamp,
        Payload = MapPlanJson.SerializeToElement(plan),
    };
}
```

Extend `FakeModHarness` only for `apply_plan`. Deserialize the payload with `MapPlanJson.Deserialize`, compute `MapPlanJson.Fingerprint`, and return:

```csharp
Payload = JsonSerializer.SerializeToElement(new
{
    applied = true,
    created_walls = 1,
    room_id = plan.Rooms[0].Id,
    undo_available = true,
    undo_instruction = "Use Dungeondraft Undo once",
    plan_fingerprint = MapPlanJson.Fingerprint(plan),
});
```

Keep unsupported commands failing exactly as before.

- [ ] **Step 4: Implement `DdaiPlanService` without weakening conflicts**

Use a single private `WaitForResponseAsync` wrapper around `AtomicMailbox.WaitForResponse`. `Validate` canonicalizes by `MapPlanJson.Deserialize(MapPlanJson.Serialize(plan))` after `RectangularRoomPlanValidator` succeeds.

`ApplyAsync` must:

```csharp
var validation = Validate(plan);
if (!validation.Valid)
    return InvalidPlan(validation.Issues);

var canonicalPlan = validation.CanonicalPlan!;
var expectedFingerprint = MapPlanJson.Fingerprint(canonicalPlan);
var request = MailboxRequest.CreateApplyPlan(canonicalPlan, timeProvider.GetUtcNow());
var published = mailbox.PublishRequest(request);
var response = await WaitForResponseAsync(request.RequestId, timeout, cancellationToken);
if (response is null)
    return Failure(
        "apply_timeout",
        "Dungeondraft did not answer before the deadline. The outcome is unknown; inspect the map and retry only with the same request_id.");
if (!StringComparer.Ordinal.Equals(response.Command, "apply_plan") ||
    response.Payload.ValueKind != JsonValueKind.Object ||
    !response.Payload.TryGetProperty("plan_fingerprint", out var actual) ||
    actual.ValueKind != JsonValueKind.String ||
    !StringComparer.Ordinal.Equals(actual.GetString(), expectedFingerprint))
    return Failure("request_conflict", "The request identifier belongs to a different map plan.");
return FromResponse(response);
```

Do not treat `published == false` as an immediate conflict; an identical in-flight request must be allowed to finish and be fingerprint-checked. Do not create a new request ID after timeout.

- [ ] **Step 5: Run focused, Core, and App tests**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AtomicMailboxTests"
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DdaiPlanServiceTests"
dotnet test DDAI.slnx -c Release --no-restore
```

Expected: every command passes; existing status and mailbox recovery tests remain green.

- [ ] **Step 6: Run the pre-commit impact gate and commit**

Run GitNexus impact for `MailboxRequest`, `FakeModHarness`, and `DdaiStatusService` to confirm the new service did not alter status callers, then:

```powershell
git add -- src/DDAI.Core/Mailbox/MailboxContracts.cs src/DDAI.Core/Mailbox/FakeModHarness.cs src/DDAI.App/DdaiPlanService.cs tests/DDAI.App.Tests/DdaiPlanServiceTests.cs tests/DDAI.Core.Tests/AtomicMailboxTests.cs
git diff --cached --check
git commit -m "feat: orchestrate validated map plan application"
```

---

### Task 3: Expose validate/apply through MCP and extend the official-SDK probe

**Files:**
- Modify: `src/DDAI.App/McpStdioServer.cs`
- Create: `tools/DDAI.McpProbe/McpToolProbe.cs`
- Modify: `tools/DDAI.McpProbe/McpStatusProbe.cs`
- Modify: `tools/DDAI.McpProbe/McpProbeOptions.cs`
- Modify: `tools/DDAI.McpProbe/Program.cs`
- Modify: `tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs`
- Modify: `tests/DDAI.App.Tests/McpProbeTests.cs`
- Create: `tests/DDAI.App.Tests/McpPlanToolTests.cs`

**Interfaces:**
- MCP: `ddai_validate_plan(plan: MapPlan) -> JSON text`.
- MCP: `ddai_apply_plan(plan: MapPlan) -> JSON text`.
- Produces: `McpToolProbe.RunAsync(McpProbeOptions, CancellationToken) -> Task<string>`.
- `McpProbeOptions` gains `ToolName` and optional normalized `PlanFilePath`; omitted `--tool` remains `ddai_status` for compatibility.

- [ ] **Step 1: Write failing MCP metadata, published-call, and probe-option tests**

Add source-level attribute tests requiring:

```csharp
[McpServerTool(Name = "ddai_validate_plan", ReadOnly = true,
    Destructive = false, Idempotent = true, OpenWorld = false)]
[McpServerTool(Name = "ddai_apply_plan", ReadOnly = false,
    Destructive = true, Idempotent = true, OpenWorld = false)]
```

Extend the published SDK integration worker with the real mailbox and fake bridge, require the listed names `ddai_status`, `ddai_validate_plan`, and `ddai_apply_plan`, then call both plan tools using:

```csharp
new Dictionary<string, object?> { ["plan"] = ValidPlan() }
```

Assert validation is true and apply returns the exact room ID, one wall, undo text, and fingerprint.

Add probe parser cases:

```text
--tool ddai_validate_plan --plan-file <absolute-json>
--tool ddai_apply_plan --plan-file <absolute-json>
```

Reject unknown tools, relative/missing plan files, a plan file with status, and a plan tool without a plan file.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~McpPlanToolTests|FullyQualifiedName~McpProbeTests|FullyQualifiedName~McpPublishedIntegrationTests"
```

Expected: tests fail because the tools, generic probe, and options do not exist.

- [ ] **Step 3: Register the service and add both MCP tools**

Rename `DdaiMcpRuntimeOptions.StatusTimeout` to `RequestTimeout`, update status, and register `DdaiPlanService` in `RunAsync`.

Add to `DdaiTools`:

```csharp
[McpServerTool(
    Name = "ddai_validate_plan",
    ReadOnly = true,
    Destructive = false,
    Idempotent = true,
    OpenWorld = false)]
[Description("Validate one grid-relative rectangular-room plan without changing Dungeondraft.")]
public static string ValidatePlan(MapPlan plan, DdaiPlanService planService) =>
    JsonSerializer.Serialize(planService.Validate(plan), JsonOptions);

[McpServerTool(
    Name = "ddai_apply_plan",
    ReadOnly = false,
    Destructive = true,
    Idempotent = true,
    OpenWorld = false)]
[Description("Create one native rectangular wall in the open blank Dungeondraft map. The same request_id is idempotent.")]
public static async Task<string> ApplyPlanAsync(
    MapPlan plan,
    DdaiPlanService planService,
    DdaiMcpRuntimeOptions runtimeOptions,
    CancellationToken cancellationToken) =>
    JsonSerializer.Serialize(
        await planService.ApplyAsync(plan, runtimeOptions.RequestTimeout, cancellationToken),
        JsonOptions);
```

- [ ] **Step 4: Generalize the official-SDK probe while preserving status behavior**

`McpToolProbe.RunAsync` must initialize the published server, verify `options.ToolName` appears in `ListToolsAsync`, and build arguments as follows:

```csharp
IReadOnlyDictionary<string, object?> arguments = options.ToolName == "ddai_status"
    ? new Dictionary<string, object?>()
    : new Dictionary<string, object?>
    {
        ["plan"] = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(options.PlanFilePath!, cancellationToken)),
    };
```

Call the chosen tool and require exactly one `TextContentBlock`. Keep `McpStatusProbe.RunAsync` as a compatibility wrapper that rejects non-status options and delegates to `McpToolProbe`. Change `Program.Main` to call `McpToolProbe`.

- [ ] **Step 5: Run focused, full, build, and published protocol tests**

Run:

```powershell
dotnet test tests/DDAI.App.Tests/DDAI.App.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~McpPlanToolTests|FullyQualifiedName~McpProbeTests|FullyQualifiedName~McpPublishedIntegrationTests"
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
```

Expected: all tests pass; build reports zero warnings and zero errors; the published SDK test proves initialize/list/validate/apply with protocol-clean stdout.

- [ ] **Step 6: Run the pre-commit impact gate and commit**

Run GitNexus impact for `DdaiTools` and `McpProbeOptions`, then:

```powershell
git add -- src/DDAI.App/McpStdioServer.cs tools/DDAI.McpProbe tests/DDAI.App.Tests/McpPublishedIntegrationTests.cs tests/DDAI.App.Tests/McpProbeTests.cs tests/DDAI.App.Tests/McpPlanToolTests.cs
git diff --cached --check
git commit -m "feat: expose rectangular room MCP tools"
```

---

### Task 4: Model the non-replayable native mutation durability protocol in .NET

**Files:**
- Modify: `src/DDAI.Core/Mailbox/DungeondraftBridgeStateMachine.cs`
- Modify: `tests/DDAI.Core.Tests/DungeondraftBridgeStateMachineTests.cs`
- Modify: `tests/DDAI.Core.Tests/MailboxBridgeConformanceTests.cs`

**Interfaces:**
- Extend `BridgeTransition` with `MutationIntentCreated`, `MutationConfirmed`, `MutationAmbiguityRecorded`, and `MutationIntentDeleted`.
- Extend the constructor with optional `Func<MailboxRequest, MailboxResponse>? executeMutation = null` while preserving all existing status callers.
- Create immutable intent files beneath `mutation-intents`: `<key>.prepared.json`, `<key>.confirmed.json`, and `<key>.ambiguous.json`.
- Prepared intent fields: schema version, request ID, canonical request fingerprint, canonical plan fingerprint, command, state `prepared`.
- Confirmed/ambiguous fields add exact `response_text`; only confirmed intents are deleted after normal response completion.

- [ ] **Step 1: Write the failing mutation-boundary and restart tests**

Add a mutation sandbox that places one `apply_plan` request in `processing` and records executor calls. Tests must prove:

```csharp
Assert.Equal(BridgeTransition.MutationIntentCreated, bridge.AdvanceClaim(fileName));
Assert.Equal(0, executeCount);
Assert.Equal(BridgeTransition.MutationConfirmed, bridge.AdvanceClaim(fileName));
Assert.Equal(1, executeCount);
```

Add a restart test after only `MutationIntentCreated`: construct a new state machine with an executor that throws if called, run recovery, and assert a correlated failure response with code `mutation_outcome_unknown`, matching plan fingerprint, zero executor calls, deleted processing claim, retained prepared/ambiguous intents, and a retained failed diagnostic.

Add a theory for restart after each confirmed-side boundary: confirmed intent, response journal, response publication, claim deletion, and response-journal deletion. Every case must converge to the exact first response without invoking the executor again and remove both confirmed and prepared intent files.

Add corruption/locking tests for prepared, confirmed, and ambiguous intent files. A failed durable write or removal must retain the source state and return `Blocked`.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DungeondraftBridgeStateMachineTests"
```

Expected: compilation fails because the mutation transitions, executor, and intent protocol do not exist.

- [ ] **Step 3: Route only `apply_plan` through immutable mutation intents**

Create `mutation-intents` in the constructor and maintain a private in-memory `HashSet<string> _preparedMutationKeys`. After normal request validation and duplicate reconciliation, route `apply_plan` to `AdvanceMutationClaim`.

The first call creates `<key>.prepared.json`, adds `key` to `_preparedMutationKeys`, and returns. The second call in the same process may invoke `_executeMutation` exactly once. A new state-machine instance has an empty set; seeing only a prepared intent must write the ambiguous intent and failure response without calling the executor.

Use immutable creation only. Never overwrite or delete the prepared intent before the exact response is confirmed. Serialize every intent with `BridgeWireJson.Options` and enforce the existing 1 MiB cap.

- [ ] **Step 4: Reuse the exact response-journal path after confirmation**

When a confirmed or ambiguous intent contains a validated response, create the existing `ResponseJournal` from that exact `response_text`, then reuse the current response publication, equivalence, and claim deletion code.

After confirmed completion, cleanup order is response journal, confirmed intent, then prepared intent, one transition per call. Retain both prepared and ambiguous intents for an ambiguous outcome. Extend ordinary and startup recovery loops to eight transitions and enumerate canonical mutation-intent keys so confirmed cleanup cannot strand.

- [ ] **Step 5: Run focused conformance and full Core tests**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DungeondraftBridgeStateMachineTests|FullyQualifiedName~MailboxBridgeConformanceTests"
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore
```

Expected: the new mutation truth table and all prior status/timestamp/fault-injection tests pass.

- [ ] **Step 6: Run the high-risk impact gate and commit**

Run GitNexus impact for `DungeondraftBridgeStateMachine` with tests included and inspect every direct caller. Then:

```powershell
git add -- src/DDAI.Core/Mailbox/DungeondraftBridgeStateMachine.cs tests/DDAI.Core.Tests/DungeondraftBridgeStateMachineTests.cs tests/DDAI.Core.Tests/MailboxBridgeConformanceTests.cs
git diff --cached --check
git commit -m "feat: model crash-safe map mutations"
```

---

### Task 5: Implement the documented GDScript rectangular-wall executor

**Files:**
- Modify: `mods/DDAI/scripts/ddai_bridge.gd`
- Modify: `mods/DDAI/ddai_bridge.ddmod`
- Modify: `mods/DDAI/README.md`
- Modify: `tests/DDAI.Core.Tests/DungeondraftModPackageTests.cs`
- Modify: `tests/DDAI.Core.Tests/MailboxBridgeConformanceTests.cs`

**Interfaces:**
- Bridge version becomes `0.2.0` and supported commands become `status`, `apply_plan` in that order.
- Produces: `_validate_rectangular_room_plan(plan, request_id) -> Dictionary`, empty on success or stable error on failure.
- Produces: `_advance_apply_plan_claim(claim, request, canonical_request_text, request_fingerprint) -> String`.
- Produces: `_execute_rectangular_room(plan, plan_fingerprint) -> Dictionary` containing a correlated response body.
- Uses immutable files under `user://ddai/mutation-intents` with the Task 4 schema.

- [ ] **Step 1: Write failing static package and durability-parity tests**

Require the manifest/script versions and exact supported commands. Add static assertions that the apply executor contains every documented token and in this order:

```text
Global.World.Width
Global.World.Height
Global.World.GridSize
Global.World.CurrentLevelId
Global.World.GetLevelByID
level.Walls.get_children()
Global.Editor.Tools["WallTool"]
wall_tool.Enable()
Global.WorldUI.ClearPolyline()
Global.WorldUI.AddPolyPoint
wall_tool.Confirm()
level.Walls.get_children()
Global.WorldUI.ClearPolyline()
wall_tool.Disable()
```

Require five explicit `AddPolyPoint` call sites in rectangle order and the fifth point to equal the first. Require canvas comparison and pre-wall count before any intent or `Enable()` call. Require prepared intent publication before `Confirm()`. Require confirmed intent publication only after wall count increases by exactly one and cleanup completes.

Require restart recovery to route prepared-only intents to `mutation_outcome_unknown` without any call to `_execute_rectangular_room`. Require the `_mutation_active` guard and a `mutation_busy` route before `wall_tool.Enable()`. Preserve the existing no-zero-argument-Dictionary-method assertions.

- [ ] **Step 2: Run the package tests and verify RED**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DungeondraftModPackageTests|FullyQualifiedName~MailboxBridgeConformanceTests"
```

Expected: failures for version, command list, missing plan validator, missing intent routes, and missing documented wall sequence.

- [ ] **Step 3: Add strict GDScript plan validation**

Validate the direct request payload as the plan. Require exact types (`TYPE_STRING`, `TYPE_INT`, `TYPE_DICTIONARY`, `TYPE_ARRAY`), `schema_version == "1.0"`, matching request IDs, `mode == "add"`, `base_revision == 0`, positive canvas, one room, safe room ID, nonnegative x/y, positive width/height, and subtraction-based bounds.

Reject JSON integers outside signed 32-bit range before conversion or multiplication:

```gdscript
func _is_nonnegative_int32(value):
	return typeof(value) == TYPE_INT and value >= 0 and value <= 2147483647

func _is_positive_int32(value):
	return typeof(value) == TYPE_INT and value > 0 and value <= 2147483647
```

After strict field/type checks, build `_plan_fingerprint_input(plan)` using the exact Task 1 scalar framing. Use `value.to_utf8().size()` for string byte counts, literal `"\n"`, decimal integer strings, fixed tags, and room array order; do not hash `to_json(plan)` or depend on Dictionary iteration/escaping. Compute `plan_fingerprint` from `_plan_fingerprint_input(plan).sha256_text()`. Add a static source contract for the exact field/tag order and pair it with the .NET golden fixture containing a non-ASCII/quoted identifier. The live mismatch and valid responses are the executable cross-language proof and must equal `MapPlanJson.Fingerprint`.

- [ ] **Step 4: Add immutable mutation-intent routing**

Create `mutation-intents` during startup and add `_prepared_mutation_keys = {}` at script scope. When a prepared file is created in the current session, assign `_prepared_mutation_keys[key] = true`; only `_prepared_mutation_keys.has(key)` authorizes the subsequent native call.

Add `_mutation_active = false`. Set it immediately before entering the documented tool sequence and clear it on every normal/error cleanup exit. A second mutation path while true returns `mutation_busy`; it must not call `Enable`, `AddPolyPoint`, or `Confirm`. Status and duplicate reconciliation remain outside the native executor and cannot re-enter it.

On startup or recovery, prepared without confirmed/ambiguous creates an immutable ambiguous intent and exact failure response with:

```gdscript
"error": _error(
	"mutation_outcome_unknown",
	"Dungeondraft may have applied this room before interruption; inspect the map and use Undo once if it appeared.",
	"request_id")
```

Every apply success and failure payload includes `plan_fingerprint`. Never erase a prepared/ambiguous intent after an unknown outcome.

- [ ] **Step 5: Implement the exact native wall sequence and cleanup**

Preflight before intent creation:

```gdscript
var world = Global.World
if world == null:
	return _apply_failure(request, plan_fingerprint, "map_not_available", "No open map is available.", "")
var runtime_width = Global.World.Width
var runtime_height = Global.World.Height
var grid_size = Global.World.GridSize
var level = Global.World.GetLevelByID(Global.World.CurrentLevelId)
var wall_tool = Global.Editor.Tools["WallTool"]
var walls_before = level.Walls.get_children().size()
```

Reject null/nonpositive values and canvas mismatch before writing the prepared intent. Verify `Global.Editor`, its `Tools` dictionary, the `WallTool` key/value, `Global.WorldUI`, the current level, and `level.Walls` before dereferencing or enabling anything. Use stable preflight codes `map_not_available`, `canvas_unavailable`, `grid_scale_unavailable`, `active_level_unavailable`, `wall_tool_unavailable`, `wall_count_unavailable`, and `canvas_mismatch`; each pre-intent failure must leave `mutation-intents` empty. For the five points `(x,y)`, `(x+w,y)`, `(x+w,y+h)`, `(x,y+h)`, `(x,y)`, multiply validated grid coordinates by `grid_size` and issue five explicit `AddPolyPoint` calls in that order. Call `Confirm()` exactly once, read `walls_after`, then call the cleanup helper (`ClearPolyline` and `Disable`) before evaluating or returning the result. A cleanup failure returns `cleanup_failed`; a post-count other than exactly `walls_before + 1` returns `wall_creation_unverified` and retains intent evidence. Only the exact count plus successful cleanup creates the confirmed success response:

```gdscript
{
	"applied": true,
	"created_walls": 1,
	"room_id": room.id,
	"undo_available": true,
	"undo_instruction": "Use Dungeondraft Undo once",
	"plan_fingerprint": plan_fingerprint,
}
```

Any control-flow exit after `Enable()` must call the cleanup helper first. A native/script interruption that prevents cleanup leaves the prepared intent and becomes ambiguous on restart.

- [ ] **Step 6: Run static, conformance, full test, build, and parser gates**

Run:

```powershell
dotnet test tests/DDAI.Core.Tests/DDAI.Core.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DungeondraftModPackageTests|FullyQualifiedName~MailboxBridgeConformanceTests"
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
$tokens = $null; $errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
  (Resolve-Path tools/Install-DDAIMod.ps1), [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
```

Expected: all tests pass, build has zero warnings/errors, and the installer PowerShell AST has zero parse errors.

- [ ] **Step 7: Run staged change-impact and commit**

Run `gitnexus detect_changes` for staged changes and verify only mailbox/mod/tool flows are affected. Then:

```powershell
git add -- mods/DDAI tests/DDAI.Core.Tests/DungeondraftModPackageTests.cs tests/DDAI.Core.Tests/MailboxBridgeConformanceTests.cs
git diff --cached --check
git commit -m "feat: create native rectangular walls"
```

---

### Task 6: Publish, install, certify read-only preflight, and prove native room/undo/save

**Files:**
- Modify after live proof: `README.md`
- Create: `docs/superpowers/reports/2026-08-10-ddai-rectangular-room-mutation-report.md`
- Produce untracked artifact: `artifacts/rectangular-room/win-x64/ddai.exe`
- Produce untracked live plan files: `artifacts/rectangular-room/plans/*.json`
- Mutate only after normal Dungeondraft close: `%LOCALAPPDATA%\DDAI\ddai.exe`, `%LOCALAPPDATA%\DDAI\DungeondraftMods\DDAI`, and owned configuration entries.

**Interfaces:**
- Official SDK probe CLI defaults to status and supports `--tool ddai_validate_plan|ddai_apply_plan --plan-file <absolute-json>`.
- Live acceptance map is 40 by 30 grid cells.
- Live room is top-left `(8,7)`, width `10`, height `8`.

- [ ] **Step 1: Run the final offline gate and publish one-file Windows x64**

Run while leaving the current Dungeondraft process untouched:

```powershell
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
dotnet publish src/DDAI.App/DDAI.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false --no-restore -o artifacts/rectangular-room/win-x64
Get-ChildItem artifacts/rectangular-room/win-x64 -File
Get-FileHash -Algorithm SHA256 artifacts/rectangular-room/win-x64/ddai.exe
```

Expected: all tests pass, zero warnings/errors, exactly one `ddai.exe`, and a recorded SHA-256.

- [ ] **Step 2: Ask the user to close Dungeondraft normally, then run owned non-administrative setup**

Wait until this returns no process:

```powershell
Get-Process -Name Dungeondraft -ErrorAction SilentlyContinue
```

Resolve the source paths first so setup receives stable absolute paths:

```powershell
$artifact = (Resolve-Path artifacts/rectangular-room/win-x64/ddai.exe).Path
$modSource = (Resolve-Path mods/DDAI).Path
& $artifact setup `
  --source-exe $artifact `
  --source-mod-directory $modSource `
  --install-root "$env:LOCALAPPDATA\DDAI" `
  --mods-directory "$env:LOCALAPPDATA\DDAI\DungeondraftMods" `
  --dungeondraft-user-data "$env:APPDATA\Dungeondraft"
```

Run under the user's original non-admin permissions. Require exit `0`, installed/source executable and GDScript hashes equal, config activates exactly `org.ddai.status_bridge`, and unrelated Claude/Gemini entries remain semantically unchanged.

- [ ] **Step 3: Ask the user to open a new blank 40-by-30 map and record the baseline**

Do not terminate or restart Dungeondraft from the connector. For this certification run, require DDAI to be the sole active mod and ask the user to leave Custom Snap disabled; do not uninstall or alter the other mod. After the user opens the map, record process ID/start time, fresh heartbeat, direct status, official-SDK status, latest crash dump, and Application Error event timestamp.

- [ ] **Step 4: Create three exact live plan files with `apply_patch`**

Create:

`preflight-mismatch.json` with request ID `live-room-preflight-20260810-001`, declared canvas 41 by 30, and the room geometry `(8,7,10,8)`.

`room-apply.json` with request ID `live-room-apply-20260810-001`, declared canvas 40 by 30, and the same geometry.

`room-save.json` with request ID `live-room-save-20260810-002`, declared canvas 40 by 30, and the same geometry.

Every file contains the complete schema shown in the approved design and `mode: "add"`, `base_revision: 0`.

- [ ] **Step 5: Certify the fixed runtime API path without mutation**

First call validation on the mismatch plan and require offline validity:

```powershell
dotnet run --project tools/DDAI.McpProbe -c Release --no-build -- `
  --exe "$env:LOCALAPPDATA\DDAI\ddai.exe" `
  --mailbox-root "$env:APPDATA\Dungeondraft\ddai" `
  --timeout-ms 5000 `
  --tool ddai_validate_plan `
  --plan-file (Resolve-Path artifacts/rectangular-room/plans/preflight-mismatch.json).Path
```

Then call `ddai_apply_plan` with the same file. Require `success: false`, error code `canvas_mismatch`, matching plan fingerprint, zero new wall, continuing heartbeat, live process, and no new crash dump/event. This is the required read-only live certification of `World`, current level, wall tool resolution, and pre-wall count before the correct canvas is allowed to mutate.

- [ ] **Step 6: Apply the correct room through the official SDK and prove one native wall**

Run the probe with `--tool ddai_apply_plan` and `room-apply.json`. Require success, one created wall, room ID `room-entrance`, undo instruction, matching fingerprint, and a continuing heartbeat.

Use the Windows UI inspection capability to capture the visible map and verify one closed rectangular native wall with no extra segment. Do not infer this from the response alone.

- [ ] **Step 7: Prove built-in undo removes the whole job**

With Dungeondraft focused, invoke one normal Undo through the UI. Capture the map again and verify the full rectangle is gone. Confirm Dungeondraft remains responsive and no new crash event/dump appears.

- [ ] **Step 8: Prove native save/reopen persistence with a fresh request ID**

Apply `room-save.json`, visually confirm the wall, save the map through normal Dungeondraft UI to a user-approved local path, close and reopen it normally, and verify the same native wall remains editable. Do not reuse the first request ID and do not edit the map file directly.

- [ ] **Step 9: Write the evidence report and correct the README status**

The report must record exact test counts, build warnings/errors, GitNexus change risk, artifact/installed hashes, config state, Dungeondraft PID/start time, heartbeat/receipt timestamps, three MCP probe JSON results and exits, intent/journal cleanup state, screenshots or their local paths, undo observation, save/reopen observation, and crash-dump/event deltas.

Change the README warning from “not yet an installable or functional connector” to an evidence-scoped statement: status, plan validation, and one rectangular native wall are functional on the certified Windows/Dungeondraft target; complete map generation and ChatGPT remote transport remain in development.

- [ ] **Step 10: Run final verification and commit only tracked acceptance files**

Run:

```powershell
dotnet test DDAI.slnx -c Release --no-restore
dotnet build DDAI.slnx -c Release --no-restore
git diff --check
git status --short
```

Run `gitnexus detect_changes` for the complete branch diff and inspect every affected flow. Then:

```powershell
git add -- README.md docs/superpowers/reports/2026-08-10-ddai-rectangular-room-mutation-report.md
git diff --cached --check
git commit -m "docs: record native room acceptance"
```

Do not stage artifacts, installed files, crash dumps, screenshots containing unrelated user content, or the preserved unrelated dirty files.

## Completion boundary

This plan is complete only when the official SDK call produces one visible native rectangular wall, one normal Dungeondraft Undo removes it, a fresh-ID wall survives save/reopen, Dungeondraft has no new crash, and every offline gate passes. The active connector goal remains open afterward: implement floors, multiple rooms/corridors, doors, installed assets, lights, labels, inspection/revision enforcement, grouped job undo, export, supported ChatGPT remote MCP transport, packaging, and the haunted five-room crypt acceptance scenario.
