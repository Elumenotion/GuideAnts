# Local AI lifecycle decisions

## D1 — GuideAntsApi owns policy

Only `GuideAntsApi` derives lifecycle intent from routing and selections.
Container services do not independently decide to start, load, warm, retain, or
restore a model.

## D2 — Commands are complete and structured

The API sends a complete JSON plan to `POST /warmup/apply`. Omitted services,
implicit booleans, and enabled services without execution references are
rejected. This prevents stale values from becoming accidental policy.

## D3 — No persisted desired plan

The executor keeps the latest accepted plan in process memory only. Restarting
`ga-admin` discards it and initializes empty/idle status. Persisted state is
observability data, not an instruction.

## D4 — ServiceModes never backfill from engines

Model folders, active markers, and engine inventory may be shown as diagnostics
but cannot mutate `ServiceModes`. A user/API selection may update ServiceModes;
engine discovery may not.

## D5 — Explicit API command is the only load edge

Engine load/unload endpoints are called only while executing an accepted API
plan or a direct API-owned user operation. Startup scripts and environment
variables cannot autoload auxiliary engines.

## D6 — Ordered execution remains centralized

`ga-admin` enforces unload/load ordering as a mechanical operation. Owning
execution order does not grant it policy authority.

## D7 — Notebook multi-alias llama is a bounded exception

Notebook chat may load multiple llama router aliases concurrently. After API
policy drains aux via lifecycle apply, `NotebookModelRuntimeService` may call
`ILlamaServerRuntimeClient` directly for the alias delta only. Aux load/unload
and default routed state still flow through API plan + `ga-admin` executor.
