# DDAI

DDAI is an open-source, Windows-first connector that lets MCP-compatible AI clients communicate with a locally running copy of Dungeondraft. The current prototype is functional with Claude Desktop, Gemini, and other local `stdio` MCP clients. It keeps Dungeondraft, maps, and purchased assets on the user's computer and opens no network listener.

## Current working slice

The connector currently exposes three MCP tools:

- `ddai_status` checks that Dungeondraft has loaded the DDAI mod.
- `ddai_validate_plan` validates the supported map-plan subset without changing the map.
- `ddai_apply_plan` creates one axis-aligned rectangular room as one closed native Dungeondraft wall operation.

The mutation slice is deliberately narrow: schema `1.0`, `mode: "add"`, `base_revision: 0`, one in-bounds room, and a canvas declaration that exactly matches the open map. A successful operation is editable, is removed by one normal Dungeondraft Undo action, and survives save/reopen.

This is not yet the complete natural-language map builder. Floors, doors, objects, lights, multiple rooms, map inspection, export, the ChatGPT relay, and release ZIP packaging remain future work.

## Compatibility and security

- Windows 10/11 x64
- Dungeondraft 1.2.0.1
- .NET 9 for source builds; the published Windows executable is self-contained
- Per-user, non-administrator installation
- Local atomic mailbox under Dungeondraft's `user://ddai`
- Local MCP `stdio`; no inbound port or firewall rule
- No Dungeondraft binaries or purchased assets are included or modified

## Build and verify

```powershell
dotnet build DDAI.slnx -c Release
dotnet test DDAI.slnx -c Release
dotnet publish src\DDAI.App\DDAI.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Local setup

Run setup from a directory containing the published `ddai.exe` and the repository's `mods\DDAI` folder, supplying explicit source paths when they are not adjacent:

```powershell
.\ddai.exe setup `
  --source-exe "C:\path\to\ddai.exe" `
  --source-mod-directory "C:\path\to\mods\DDAI" `
  --install-root "$env:LOCALAPPDATA\DDAI" `
  --mods-directory "$env:LOCALAPPDATA\DDAI\DungeondraftMods" `
  --dungeondraft-user-data "$env:APPDATA\Dungeondraft" `
  --json
```

Setup installs only DDAI-owned files and transactionally adds the `ddai` MCP entry to Claude Desktop and Gemini while preserving unrelated configuration. Dungeondraft must be closed for first activation or consolidation. In Dungeondraft, select the configured per-user Mods directory, enable `DDAI Status Bridge`, then open a map. After upgrades, use Dungeondraft's normal **Reload Mods** action.

Useful checks:

```powershell
& "$env:LOCALAPPDATA\DDAI\ddai.exe" diagnose --json
& "$env:LOCALAPPDATA\DDAI\ddai.exe" status --json
```

The local MCP command is:

```text
C:\Users\<user>\AppData\Local\DDAI\ddai.exe serve --stdio
```

## Example supported plan

```json
{
  "schema_version": "1.0",
  "request_id": "room-example-001",
  "base_revision": 0,
  "mode": "add",
  "canvas": { "width": 40, "height": 30 },
  "rooms": [
    { "id": "room-entrance", "x": 8, "y": 7, "width": 10, "height": 8 }
  ]
}
```

## Evidence and design

- [Rectangular-room design](docs/superpowers/specs/2026-08-10-ddai-rectangular-room-mutation-design.md)
- [Rectangular-room implementation plan](docs/superpowers/plans/2026-08-10-ddai-rectangular-room-mutation.md)
- [Rectangular-room live report](docs/superpowers/reports/2026-08-10-ddai-rectangular-room-mutation-report.md)
- [Long-term Windows connector plan](docs/plans/2026-08-09-ddai-windows-connector.md)

## Licensing and ownership

DDAI is licensed under Apache License 2.0. Dungeondraft is separate commercial software and is not included. Users must obtain their own licensed copy from Megasploot.
