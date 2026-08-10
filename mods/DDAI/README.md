# DDAI Map Bridge

This Dungeondraft 1.2.0.1 mod services the local `user://ddai` mailbox. It has no network listener and supports `status` plus the deliberately narrow `apply_plan` command.

`apply_plan` accepts one validated rectangular room on a matching blank canvas and creates it as one native rectangular wall through Dungeondraft's wall tool. Blankness is an operator precondition; the bridge cannot inspect it yet. The interactive Wall tool must be idle, which the bridge checks before mutation. It does not paint terrain, place objects, modify arbitrary existing geometry, or expose a network service. A successful result remains editable in Dungeondraft and can be reversed with the application's normal manual Undo command. This route is live-certified only for Dungeondraft 1.2.0.1.

The controller writes atomic request envelopes to `user://ddai/requests`. The bridge claims one request at a time into `processing`, records a non-replayable mutation intent before touching the map, writes atomic correlated responses to `responses`, and leaves auditable structured records in `failed` when safe correlation or recovery is not possible. If Dungeondraft stops after a mutation begins, the bridge reports `mutation_outcome_unknown` on restart instead of risking a duplicate room.

Install this whole `DDAI` folder under a Dungeondraft mod folder selected by the user. Do not copy it into a Dungeondraft PCK, modify executables, or replace any existing mod.
