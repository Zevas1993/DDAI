# DDAI in-flight map job deadlock

Recorded: 2026-08-16

## Outcome

A map job that does not finish inside the connector's apply deadline can never be
resumed. The operation stays applied, the created node is never observed, no reversal
runs, and the documented recovery instruction cannot be satisfied. This is a defect in
the connector and bridge contract, not in any one executor.

`wall_polyline` does not expose it because it completes apply, observe and commit
inside a single processing window. Any slower operation does.

## Sequence

1. `apply_plan` arrives. The bridge prepares the job, executes the native operation and
   records the created node ids.
2. The job moves to `operation_applied` and the internal map revision advances.
3. The connector's deadline expires first. It returns `apply_timeout` with
   `outcome_unknown: true` and `retry_with_same_request_id: true`.
4. The caller retries the identical request, exactly as instructed.
5. Plan validation rejects it with `map_revision_mismatch` on `base_revision`, because
   the revision advanced at step 2. The job-resume path is never reached.
6. The job remains at `operation_applied` permanently.

## Evidence

Live, on a disposable map, mod `0.3.0`, Dungeondraft 1.2.0.1.

Job record after the timeout:

```
request_id                 cert-object-20260816-010
state                      operation_applied
current_operation_node_ids {0}
observed_native_node_ids   {}
```

The job was then watched for 90 seconds with no further requests sent:

```
t=0    state: operation_applied
t=15s  state: operation_applied   observe_ran: False
t=30s  state: operation_applied   observe_ran: False
t=45s  state: operation_applied   observe_ran: False
t=60s  state: operation_applied   observe_ran: False
t=75s  state: operation_applied   observe_ran: False
t=90s  state: operation_applied   observe_ran: False
runtime heartbeat age: 3s
```

The bridge was alive throughout. A temporary probe inside
`_observe_addressable_surface` wrote nothing, confirming observation was never invoked
rather than invoked and failing.

Retrying the identical request returned:

```
map_revision_mismatch  path: base_revision
```

## Why it deadlocks

Observation is scheduled on one tick and its result read on the next, through the
in-memory `_pending_observation_results`. The state machine advances only while a
correlated request is being processed; it does not progress an in-flight job on its own
update tick.

Resuming therefore requires re-sending the same request. But plan validation runs
`base_revision` before the request is correlated to the existing job, and by then the
revision has moved. The two requirements are mutually exclusive.

## Impact

- An operation slower than the deadline leaves a node on the map with no observation
  record, so `ddai_undo_last_job` has no evidence and cannot reverse it.
- The connector reports `outcome_unknown`, which is accurate but unrecoverable.
- The retry contract published in the error message is impossible to satisfy in this
  state, which will mislead any agent that follows it.

## Suggested direction

Correlate an incoming request to an existing durable job before revalidating
`base_revision`, so a retry resumes rather than being refused. The resume validation at
`_read_correlated_map_job` already accepts `operation_applied`, so the ordering is the
issue rather than the acceptance rule.

Separately, consider whether the bridge should advance an in-flight job on its own
update tick instead of depending on caller polling. That would also close the case where
the caller disconnects between apply and observe.

## Status of object placement

Unaffected by this report's conclusion: the executor itself works. The Prop is created
through `Objects.CreateObject`, configured, registered in the search index, assigned a
node id, given valid bounds, and appears in `ddai_inspect_map` at the exact requested
grid position with the correct asset reference. It is blocked from certification only by
the deadlock above.
