# Task 2: Atomic local mailbox and fake-mod harness

## Status

Implemented and verified on .NET 9 Release. The connector uses an explicitly supplied mailbox root and creates `requests`, `processing`, `responses`, `failed`, and `journal` directories. Request publication is same-directory temporary-file serialization followed by a non-overwriting rename. Claims use a non-overwriting move from `requests` to `processing`.

The protocol has versioned request and response envelopes, correlation by request ID and command, explicit success/error state, and structured error details. Request IDs are SHA-256 file keys; path segments are rejected before publication. Duplicate IDs are idempotent, including a concurrent publication race. Requests and inbound files are capped at 1 MiB, and processing recovery requeues interrupted claims.

## Commit

`feat: add atomic local mailbox harness` (the commit containing this report)

## Red-green evidence

Observed red then green cycles:

1. The initial fake-mod status round-trip test failed to compile because `DDAI.Core.Mailbox` did not exist; the minimal mailbox contracts, filesystem mailbox, and fake harness made it pass.
2. Traversal request ID test failed because `../escape` was accepted; request-ID path-segment validation made it pass.
3. Concurrent duplicate-ID test failed with `IOException` from the losing atomic rename; duplicate-aware publication handling made it pass with exactly one winner.
4. Oversize publisher test failed because a >1 MiB serialized request was accepted; pre-publication byte enforcement made it pass.

Coverage also exercises temporary partial-write invisibility, ordinary duplicate publication, malformed input movement to `failed`, oversize inbound-file movement to `failed`, response timeout, and processing crash recovery through the real filesystem mailbox.

## Tests

`dotnet test DDAI.slnx -c Release --no-restore`

- Passed: 23
- Failed: 0
- Skipped: 0

`dotnet build DDAI.slnx -c Release --no-restore`

- Succeeded with 0 warnings and 0 errors.

## Concerns

- This task validates the protocol against the fake mod only; no Dungeondraft binary, asset, or runtime interaction was introduced or exercised.
- The timeout wait uses short polling because the mailbox must remain portable for the target mod environment; a later runtime integration can replace or supplement this with a platform-aware change signal if required.
