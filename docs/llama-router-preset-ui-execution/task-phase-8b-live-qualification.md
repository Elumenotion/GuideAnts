# Task — Phase 8B: Live catalog and hardware qualification

> Qualification-only subagent brief. May run in parallel with 8A. Return the report contract verbatim.

## Mission

Prove the shipped manifest against live Hugging Face repositories and qualify the
six representative models required by the proposal through real download, router,
chat, tool, reasoning, vision/MTP, restart, repair, and quant-change behavior.
Record evidence; do not edit product source.

## Read first

- Proposal §§4.10–4.16, 6, 8 Phase 5, 9.
- `./DECISIONS.md` D1, D3–D6, D10.
- `STATUS.md` live and representative qualification tables.
- Final Phase 1A live-suite command and exact image/test commands frozen by Phase 0.
- Runtime/container operator docs produced by Phase 8A if already available.

## Preconditions

- Phase 7 gate passed.
- Required HF token, storage, bandwidth, and claimed accelerator environments exist.
- Phase 8A may run concurrently; do not modify its files.

## Hard guardrails

- Qualification is read/test only. Do not patch code, manifests, DB migrations, or
  presets to obtain a pass.
- Record exact image digest, accelerator/driver, definition version, repository
  commit, quant, artifact list, preset, profile, and timestamps.
- Use an explicit quant each time. Recommendation labels are not selections.
- A repository drift, gated-access error, insufficient hardware, or unavailable
  release lane is recorded as failure/blocked with evidence.
- Do not publish HF tokens, private paths, or model-license credentials.
- Clean up test artifacts only through supported lifecycle operations.

## Tasks

1. Run the live manifest-drift suite for all 14 definitions. For each, record:
   resolved commit, discovered quant labels, complete shard groups, sizes,
   projector resolution, recommendation-label presence, and result.
2. Confirm every live response is deterministic when repeated at its resolved commit.
3. Confirm an intentionally incomplete shard fixture/repository response is rejected.
4. Qualify the six proposal representatives:
   - `qwen3.6-35b-a3b`: vision, reasoning, tools;
   - `qwen3.6-35b-a3b-mtp`: text/reasoning/tools and MTP arguments, no projector;
   - `gemma4-31b`: vision, reasoning, tools;
   - `deepseek-r1-14b`: reasoning and `parallel_tool_calls=false`;
   - `qwen3-coder-30b`: coding/tools and `parallel_tool_calls=true`;
   - `gpt-oss-20b`: reasoning/tools.
5. For each representative run applicable steps:
   catalog resolve, explicit quant, review, download all artifacts, INI/preset
   inspection, load, basic chat, profile sampling/thinking, tool request capture,
   vision input or MTP argv proof, restart and reload, repair, and status/inventory.
6. Run change quant on at least one single-file and one sharded representative.
   Confirm staged activation, provenance update, loaded-state behavior, and old-file
   deletion only after success.
7. Corrupt/remove one disposable test artifact, run Repair, and confirm exact
   recorded-commit restoration and integrity result.
8. Restart API and llama-admin during separate disposable operations and confirm
   durable status/finalization.
9. Qualify every claimed CPU/CUDA/ROCm/Vulkan image at least for startup, catalog,
   router preset, fleet projection, and the proposal's minimum smoke model
   `qwen3.5-9b`. Run the full six-model matrix on the primary release hardware;
   record any additional lane limitations explicitly.
10. Populate the two qualification tables in the report for the orchestrator to
    copy into `STATUS.md`.

## Files in scope

- No product or documentation edits.
- Temporary runtime data, downloaded test models, logs, screenshots, request
  captures, and an external qualification report location approved by the orchestrator.

## Self-verification

Repeat one live resolution at the recorded commit, one repaired load, and one
restart/load test. Ensure logs contain no HF token. Confirm the installed provenance,
INI, inventory, and captured request agree for each representative.

## Definition of Done

- [ ] All 14 live definitions resolve and group correctly at recorded commits.
- [ ] Six representatives pass every applicable capability.
- [ ] Single and sharded quant changes plus corruption repair pass.
- [ ] Restart durability is demonstrated.
- [ ] Every claimed runtime image/hardware lane has evidence or is explicitly blocked.
- [ ] No product file changed and no credential appears in evidence.

## Report-back contract

```text
PHASE 8B REPORT
- Environment: images=<digests> hardware=<accelerator/driver/RAM/VRAM> storage=<available>
- Live 14 suite command/result: <command> pass=<n> fail=<n> blocked=<n>
- Live table: <definition -> commit, quant labels/groups, projector, result for all 14>
- Repeat-at-commit determinism: <p/f>
- Representative table: <model -> quant/commit, install, load, chat, tools, reasoning, vision/MTP, restart, repair, result>
- Quant change: single=<model/result> sharded=<model/result>
- Corruption repair: <model/artifact/result/integrity evidence>
- Restart durability: API=<p/f> llama-admin=<p/f>
- Runtime lanes: CPU=<result> CUDA=<result> ROCm=<result> Vulkan=<result>
- Token/log inspection: <clean?>
- Product files touched: <must be none>
- Blockers / failures: <exact evidence or none>
- Deviations / surprises: <list or none>
```
