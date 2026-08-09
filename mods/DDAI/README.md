# DDAI Status Bridge

This Dungeondraft 1.2.0.1 mod services the local `user://ddai` mailbox. It has no network listener and currently supports only `status`.

The controller writes atomic request envelopes to `user://ddai/requests`. The bridge claims one request at a time into `processing`, writes atomic correlated responses to `responses`, and leaves an auditable structured failure record in `failed` when a malformed envelope cannot be safely correlated. On load, it writes `user://ddai/runtime-receipt.json`.

Install this whole `DDAI` folder under a Dungeondraft mod folder selected by the user. Do not copy it into a Dungeondraft PCK, modify executables, or replace any existing mod.
