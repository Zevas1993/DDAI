# DDAI rectangular-room mutation report

Date: 2026-08-10

Target: Windows x64, Dungeondraft 1.2.0.1

Result: live rectangular-room acceptance passed with DDAI mod 0.2.1

## Delivered behavior

- The official C# MCP SDK initializes the published `ddai.exe`, lists the plan tools, validates a plan, and invokes `ddai_apply_plan` over protocol-clean `stdio`.
- The controller and Dungeondraft mod exchange bounded, correlated JSON through the per-user atomic mailbox.
- The supported plan subset creates exactly one closed native wall for one rectangular room on a matching open canvas.
- Dungeondraft records the mutation as one normal Undo operation.
- A saved map reopens with the same rectangle.

## Live acceptance evidence

The acceptance run used one non-administrator Windows user, the licensed local Dungeondraft installation, the per-user DDAI installation, and a 40 by 30 open map.

1. `diagnose --json` returned `running` / `runtime_heartbeat_fresh`.
2. `status --json` returned success, mod version `0.2.1`, `map_loaded: true`, and supported commands `status` and `apply_plan`.
3. A deliberately mismatched 41 by 30 plan returned `canvas_mismatch`, left the blank map unchanged, and did not crash Dungeondraft.
4. The official SDK submitted a fresh 40 by 30 plan with room `(8,7,10,8)`.
5. The response returned success with `created_walls: 1`, room ID `room-entrance`, and plan fingerprint `1b8e86cd697c7220ecebb710591fb84afdae278946b74558c8c85a0bc1e4cb23`.
6. The resulting map showed one clean axis-aligned rectangle and no diagonal or extra segments.
7. One Dungeondraft Undo removed the entire earlier wall operation and enabled Redo, proving the operation is grouped as one native history entry.
8. The corrected room was saved as `test.dungeondraft_map` (316,606 bytes; SHA-256 `4465C4E97C52096B075A7177B861D4F8BD9FBAE65D8BC68B90DC0C9302F49031`).
9. Dungeondraft was closed normally and launched directly with that file. It reopened as `test - Dungeondraft`, remained responsive, loaded mod 0.2.1, and displayed the same clean rectangle.
10. No new Dungeondraft Application Error event was recorded during the corrected apply, save, close, or reopen sequence. The latest observed event remained the earlier 2026-08-10 01:04:24 crash from pre-fix testing.

Screenshots, the disposable parser harness, plan inputs, and locally extracted runtime metadata remain under the untracked `artifacts/rectangular-room` evidence directory. They are intentionally excluded from source control because they are machine-specific and the runtime metadata came from the user's purchased local installation.

## Diagonal-wall defect and correction

The initial live attempt supplied five points (four corners plus the first corner again) and called `WallTool.Confirm()`. Offline tests proved only that the calls existed and one wall node was created; the live map exposed diagonal segments.

Inspection of the installed Dungeondraft 1.2.0.1 managed runtime established the exact cause: `Confirm()` decides whether to loop by comparing the live mouse cursor with the first polyline point. When the cursor is elsewhere, it appends the cursor as an additional point and ends an open wall. The connector now supplies exactly four corners and calls `EndWall(true)`, which consumes those points as one closed wall and records one Undo entry. `EndWall(bool)` appears in the current official mod API reference, but this route is nevertheless treated as an exact-version dependency and requires fresh live certification for any Dungeondraft version beyond 1.2.0.1. A regression test rejects any return to the cursor-dependent `Confirm()` route.

The bridge also fails with `wall_tool_busy` before writing a mutation intent when the interactive Wall tool is drawing, editing an arc, or retaining polyline points. It does not clear the user's unfinished polyline in that failure path. Blank-map state remains an explicit operator precondition because this slice cannot inspect all map content.

The DDAI version was advanced from 0.2.0 to 0.2.1 so setup and diagnostics can distinguish the corrected runtime.

## Safety boundaries retained

- No Dungeondraft executable, PCK, or purchased asset was changed or committed.
- The connector opens no network listener.
- Setup writes only DDAI-owned installation files and ownership-proven MCP configuration entries.
- The mutation state machine records a durable prepared intent before map mutation and will not blindly replay an interrupted operation.
- Malformed, unsupported, out-of-bounds, or canvas-mismatched plans fail before mutation.
- The current certified slice remains one rectangular room only; broader map generation is not claimed.

## Evidence appendix

### Installed artifact and configuration

- Published/installed executable: 73,843,605 bytes; SHA-256 `12A91D020063BBC040288034AA3D31ED9E7FBF493662379F9B59B2C586919508` on both copies.
- Installed mod directory: `C:\Users\ChrisBoyd\AppData\Local\DDAI\DungeondraftMods\DDAI`.
- Final source/installed GDScript SHA-256: `114D1C24696CE5686E5A6F262D9F44591FF5487FC8BFF7A8E9EB67AD9D793A7C` on both copies.
- Dungeondraft configuration: `mods_directory="C:\\Users\\ChrisBoyd\\AppData\\Local\\DDAI\\DungeondraftMods"`; active mods exactly `[ "org.ddai.status_bridge" ]` during certification.
- Claude and Gemini `ddai` entries were already current; setup reported `changed: false` for both and preserved their unrelated configured servers.
- Setup ran under the user's original non-administrator permissions.

### Runtime identity

- Corrected live apply/Undo/save process: PID `55280`, started `2026-08-10 08:12:44 -04:00`.
- Save/reopen process: PID `47120`, started `2026-08-10 09:31:50 -04:00`, title `test - Dungeondraft`, responsive.
- Hardened-script reopen process: PID `22892`, started `2026-08-10 10:45:22 -04:00`, title `test - Dungeondraft`, responsive.
- Hardened-script receipt: `runtime-receipts/1786373125-2849.json`, timestamp `2026-08-10T14:45:25Z`, mod `0.2.1`, supported commands `[status,apply_plan]`.
- Observed fresh heartbeat: `runtime-heartbeats/heartbeat-slot-1.json`, session `1786373125-2849`, timestamp `2026-08-10T14:45:25Z`.
- Dungeondraft's Reload Mods action did not rotate the existing bridge session during the final hardening check, so it was not accepted as activation evidence. A normal close and direct reopen of the saved map produced the new PID, receipt, and session above.

### MCP probes

- `ddai_validate_plan` accepted each complete plan input; each probe exited `0`.
- Mismatched live apply (`live-room-preflight-20260810-001`, declared 41 by 30): probe exit `0`, `success:false`, code `canvas_mismatch`, fingerprint `2aacfbe6090fc00a735e5f3ec3bee5a032e8d1039f3d511d8e7bdf64171a28aa`.
- Corrected live apply (`live-room-save-20260810-002`, declared 40 by 30): probe exit `0`, `success:true`, `created_walls:1`, room `room-entrance`, fingerprint `1b8e86cd697c7220ecebb710591fb84afdae278946b74558c8c85a0bc1e4cb23`.
- Direct `status --json` and official-SDK `ddai_status` each exited `0` and returned mod `0.2.1` with `map_loaded:true`.

### Durable state and visual evidence

- After response completion: `requests=0`, `processing=0`, `journal=0`, and `mutation-intents=0`. Published response files are retained by design.
- Blank baseline: `artifacts/rectangular-room/dungeondraft-after-reload.png`.
- Canvas mismatch: `artifacts/rectangular-room/dungeondraft-after-mismatch.png`.
- Single-Undo proof: `artifacts/rectangular-room/dungeondraft-after-undo.png`.
- Corrected rectangle: `artifacts/rectangular-room/dungeondraft-room-021.png`.
- Save/reopen persistence: `artifacts/rectangular-room/dungeondraft-room-reopened-021.png`.
- Hardened-script normal-reopen persistence: `artifacts/rectangular-room/dungeondraft-room-hardened-reopen.png`.
- GitNexus reported low risk and zero affected processes for the final staged diff, but GDScript function bodies are not fully represented in its graph. The final review therefore conservatively classifies the runtime change as medium risk and requires the independent live evidence above.

## Verification gates

The final commit is gated on:

- focused Dungeondraft package tests;
- complete Release tests for core and application projects;
- warning-free Release solution build;
- Godot 3.5.3 parse/instantiation of the exact packaged GDScript;
- PowerShell AST checks for repository scripts;
- static executable-listener scan;
- GitNexus changed-symbol and affected-flow review;
- `git diff --check` and a scoped commit that excludes unrelated dirty files and local artifacts.

Final results:

- Dungeondraft package tests: 13/13 passed.
- Version-sensitive diagnostic tests: 8/8 core and 19/19 application passed.
- Complete Release suite: 289/289 core plus 104/104 application, 393/393 total.
- Release solution build: 0 warnings, 0 errors.
- Exact packaged GDScript under Godot 3.5.3: exit 0, `DDAI_PARSE_OK:True`, empty stderr.
- PowerShell AST errors: 0.
- Executable listener-pattern hits in `src` and `mods`: 0.
- `git diff --check`: clean.
