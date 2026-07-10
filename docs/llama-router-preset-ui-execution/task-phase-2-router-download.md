# Task — Phase 2: Exact download and complete router presets

> Subagent brief. Execute top to bottom and return the report contract verbatim.

## Mission

Upgrade llama-admin and its C# client from one-pattern/one-GGUF plus ctx/cache writes
to exact ordered artifact sets and complete per-alias presets. Make staging,
validation, INI commit, operation state, and reload behavior explicit and safe.

## Read first

- Proposal §§4.3, 4.7, 5.3–5.5, 5.8, 6.1–6.2, 6.5.
- `./DECISIONS.md` D1–D5, D8.
- Phase 0 shard/fixture evidence and Phase 1A grouping contracts.
- `docker/build/guideants-ai/llama-admin-service/llama_admin_service.py`
- `docker/build/guideants-ai/lib/guideants_hf/*`
- `docker/build/guideants-ai/start-llama.sh`, `entrypoint.sh`
- `LlamaRuntimeAdminClient.cs`, `HuggingFaceModelDownloadService.cs`
- Runtime inventory and INI sync services/tests.
- `./codeql-gate.md`

## Preconditions

- Phase 0 and 1A gates passed. D1 first-shard representation is physically proven.
- Phase 1A owns no unfinished llama-admin edits.

## Hard guardrails

- Inputs are exact ordered repository-relative files at one resolved commit.
- Every path is root-contained. No user/repository value is shell code.
- All files download to a staging tree; active artifacts/INI change only after the
  complete set validates.
- Curated `replace` and operator `merge` have distinct tested semantics.
- `model`, `mmproj`, and `version` are not preset extras.
- A failed download, integrity check, INI write, or reload is reported at its exact
  step. Do not return completed.
- Do not delete prior active artifacts in this phase.

## Tasks

1. Replace the llama-admin download request contract with exact fields from D2:
   repository, resolved revision, ordered model files, projector files, alias,
   target directory, complete preset, preset mode, artifact metadata, operation ID,
   and server-resolved HF token.
2. Download every exact file at the resolved commit. Preserve order; support resume
   only when temporary file metadata matches the requested artifact.
3. Validate final byte size/digest when available and the complete sharded group.
   Reject duplicate destination names and directory escapes.
4. Stage under an operation-specific directory and atomically activate the complete
   set. Record completed side effects in the durable operation bridge/journal.
5. Extend router parsing/serialization:
   - GET returns `preset`, with ctx/cache convenience projections retained;
   - PUT/POST accepts `preset` and `presetMode`;
   - `replace` removes prior extras then writes the supplied map;
   - `merge` changes supplied extras only;
   - normalize duplicate aliases/keys and enforce D5 scope/value rules.
6. Write model path (first shard per D1), projector path, and preset in one locked,
   atomic INI replacement. Trigger one reload after commit.
7. Ensure reload failure reports `runtimeApply` with INI revision/hash and
   remediation; it must not rewrite the request to another preset.
8. Make download/staging/INI side-effect state survive llama-admin restart using
   the subordinate journal frozen in D2. This journal never finalizes a catalog
   model; Phase 4 connects it to the authoritative SQL operation.
9. Extend `LlamaRuntimeAdminClient`, download service DTOs, inventory DTOs, and
   focused API proxies for complete router-entry reads/writes.
   Prove GuideAnts `PUT /api/settings/llama/router/entries/{alias}` maps to
   llama-admin `POST /router/entries` and round-trips the committed fixture.
10. Preserve read compatibility for ctx/cache convenience fields while removing
    them as the write authority.
11. Add Python tests for parser round-trip, atomic write, lock/concurrency, modes,
    invalid input, paths, staged download, resume metadata, integrity, shard order,
    restart recovery, and reload error.
12. Add C# wire-fixture/client/inventory tests.

## Files in scope

- llama-admin service, shared HF transport/download modules, new focused Python tests.
- Runtime shell scripts only if the frozen operation/reload contract requires it.
- C# llama admin client, download service/DTO, router inventory/proxy, DI, tests.

Out of scope: EF model changes, fleet settings behavior, catalog model creation,
runtime JSON migration, client UI, lifecycle replacement/repair.

## Self-verification

```text
python -m compileall docker/build/guideants-ai/lib/guideants_hf docker/build/guideants-ai/llama-admin-service
python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_*.py" -v
bash -n docker/build/guideants-ai/start-llama.sh
bash -n docker/build/guideants-ai/entrypoint.sh
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.sln
```

Run contract tests both directions, interrupted/restart tests, and Phase 2 CodeQL.

## Definition of Done

- [ ] Exact single and sharded artifact sets install from one commit.
- [ ] Staging/integrity/activation and subordinate journal recovery are tested.
- [ ] Full preset GET and replace/merge writes are atomic and scoped.
- [ ] C# and Python agree on all frozen payloads.
- [ ] No path/INI/token/process security issue remains.
- [ ] Builds and tests pass.

## Report-back contract

```text
PHASE 2 REPORT
- Download contract: <fields> exact-revision=<yes> ordered-files=<yes>
- Shards: D1 model-path=<value rule> complete-set-validation=<p/f>
- Staging/integrity: atomic=<p/f> size=<p/f> digest=<p/f/n-a> resume-metadata=<p/f>
- Router API: GET-preset=<yes> replace=<p/f> merge=<p/f> reload-once=<p/f>
- Scope/path/INI validation tests: <counts/results>
- Restart durability: subordinate-journal=<mechanism/result> SQL-authority-not-claimed=<confirmed>
- Proxy mapping: GuideAnts-PUT-to-admin-POST=<p/f> fixture-round-trip=<p/f>
- C#/Python fixture parity: <p/f>
- Verification: python=<counts> shell=<p/f> server-build=<p/f> server-tests=<counts>
- CODEQL REPORT: <required block from codeql-gate.md>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or none>
```
