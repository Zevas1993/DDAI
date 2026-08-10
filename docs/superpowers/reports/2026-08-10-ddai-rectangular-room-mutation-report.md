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

Inspection of the installed Dungeondraft 1.2.0.1 managed runtime established the exact cause: `Confirm()` decides whether to loop by comparing the live mouse cursor with the first polyline point. When the cursor is elsewhere, it appends the cursor as an additional point and ends an open wall. The connector now supplies exactly four corners and calls `EndWall(true)`, which consumes those points as one closed wall and records one Undo entry. A regression test rejects any return to the cursor-dependent `Confirm()` route.

The DDAI version was advanced from 0.2.0 to 0.2.1 so setup and diagnostics can distinguish the corrected runtime.

## Safety boundaries retained

- No Dungeondraft executable, PCK, or purchased asset was changed or committed.
- The connector opens no network listener.
- Setup writes only DDAI-owned installation files and ownership-proven MCP configuration entries.
- The mutation state machine records a durable prepared intent before map mutation and will not blindly replay an interrupted operation.
- Malformed, unsupported, out-of-bounds, or canvas-mismatched plans fail before mutation.
- The current certified slice remains one rectangular room only; broader map generation is not claimed.

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

- Dungeondraft package tests: 12/12 passed.
- Version-sensitive diagnostic tests: 8/8 core and 19/19 application passed.
- Complete Release suite: 288/288 core plus 104/104 application, 392/392 total.
- Release solution build: 0 warnings, 0 errors.
- Exact packaged GDScript under Godot 3.5.3: exit 0, `DDAI_PARSE_OK:True`, empty stderr.
- PowerShell AST errors: 0.
- Executable listener-pattern hits in `src` and `mods`: 0.
- `git diff --check`: clean.
