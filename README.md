# DDAI

DDAI is an open-source Windows connector intended to let ChatGPT, Claude Desktop, Gemini, and other MCP-compatible AI clients build maps in Dungeondraft from natural-language instructions.

> [!IMPORTANT]
> DDAI is in early development. It is not yet an installable or functional connector.

## Design principles

- Use Dungeondraft's documented GDScript mod API.
- Keep Dungeondraft, purchased asset packs, maps, and generated private assets on the user's computer.
- Never redistribute or modify Dungeondraft binaries.
- Prefer local MCP `stdio` connections for Claude, Gemini, and compatible clients.
- Use an outbound-only paired relay for ChatGPT, with no inbound firewall rule.
- Validate a complete map job before applying it, create a backup, and preserve undo/recovery.

## Initial compatibility target

- Windows 10/11 x64
- Dungeondraft 1.2.0.1
- Complete dungeon and building maps using an already-open map

The approved implementation design is recorded in [`docs/plans/2026-08-09-ddai-windows-connector.md`](docs/plans/2026-08-09-ddai-windows-connector.md).

## Licensing and ownership

DDAI is licensed under Apache License 2.0. Dungeondraft is separate commercial software and is not included. Users must obtain their own licensed copy from Megasploot.

