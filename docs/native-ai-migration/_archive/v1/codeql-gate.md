# CodeQL Gate — Native AI Migration

Companion to [`00-overview.md`](./00-overview.md). Local baseline-vs-current only (no GitHub
parity). **Runs once, at the end of the migration** — not per phase.

Unlike the skills-support plan (which re-ran CodeQL after each security-sensitive phase),
this migration is a rolling refactor of the same data/control planes. Running the scanner
incrementally after every phase would produce noise against a moving tree and re-flag the
same in-flight code repeatedly. So this gate is deliberately **end-only + changed-languages-
only**, per explicit instruction.

---

## 1. Gate intent

The final gate before the migration is considered complete. Pass when the **NEW** findings
versus the captured pre-migration baseline are **zero** (or explicitly triaged) for **each
language that actually changed**, with specific attention to the new surfaces this migration
introduces:

- **C++ (`ga-audio-server` / the `engine_runtime` adapter):** the highest-risk new surface —
  a native HTTP server handling **multipart uploads** and shelling out to `ffmpeg`. Focus:
  buffer/bounds handling on upload parsing, command-injection in the ffmpeg invocation
  (build argv arrays, never a shell string from user-influenced input), temp-file handling,
  and path handling for `model_path`/`model_id` download targets.
- **Python (rewritten `emb_service.py` facade + consolidated `ga-admin`):** subprocess
  spawning of `llama-server`/`sd-server`, download/operation subsystem (HF + single-file
  GGUF), and router-INI CRUD. Focus: command construction for child processes, path
  traversal in `model_dir`/download targets, SSRF/unvalidated URLs in downloads, and
  clear-text logging of `hf_token` (stamped in by .NET).
- **C# (only if any .NET contract tweak lands):** e.g. a Phase 3 Option-B voice-preset
  change in `ServiceEditorMetadataProvider.cs`. Focus: nothing new expected; scan only if
  the diff touches C#.
- **No new silent `catch`/`except` masking** of parse/IO/subprocess failures (user rule +
  the plan's "no runtime fallback" invariant).

---

## 2. Language matrix is derived from the diff (do not run all languages)

CodeQL is run **only for the languages the cumulative migration diff actually changed.**
Derive the matrix from the merged diff of all phases against the pre-migration baseline:

| CodeQL language | Include when… | This migration |
|---|---|---|
| `cpp` | any `.c/.cc/.cpp/.h/.hpp` under the new `audio-server/` (and any vendored source built into it) changed | **include** (`ga-audio-server`, Phases 2–3) |
| `python` | any `.py` under `docker/build/guideants-ai/**` changed | **include** (emb facade Phase 1; `ga-admin` Phase 4; retired services) |
| `csharp` | any `.cs` changed | **include only if** a phase landed a real .NET change (e.g. Phase 3 D1=Option B `ServiceEditorMetadataProvider.cs` + a `VoiceName` migration); otherwise **omit** |
| `java-kotlin`, `javascript`, `go`, `ruby`, `swift` | corresponding sources changed | **omit** (no such code changed) |

> **Explicitly not analyzable by CodeQL.** `Dockerfile.*`, `nginx.conf`, `entrypoint.sh`,
> `start-*.sh`, `*-requirements.txt`, and `docker-compose.*.yml` are **not** CodeQL-supported
> languages. They carry real risk (route splits, process supervision, shelling) but are
> **out of scope for this scanner** and are covered instead by the
> [`contract-preservation-gate.md`](./contract-preservation-gate.md) (route/behaviour parity)
> and [`flavor-build-gate.md`](./flavor-build-gate.md) (build + `HEALTHCHECK`). Do **not**
> claim shell/Dockerfile/nginx coverage from CodeQL.

If a phase is deferred (e.g. Phase 4), re-derive the matrix from whatever actually merged —
if no Python `ga-admin` landed, the `python` findings are limited to the Phase 1 facade, and
so on. The matrix is a function of the diff, never a fixed "run everything" list.

---

## 3. Procedure

1. **Baseline (pre-flight, before Phase 1):** create CodeQL databases for the languages the
   plan is expected to touch (`cpp` — note the adapter does not exist yet, so its baseline is
   empty; `python`; `csharp`) at the **pre-migration** commit and save SARIFs under
   `.codeql/baseline/`. Record counts in [`STATUS.md`](./STATUS.md).
2. **End-only run:** after **all phase code changes are merged** (the last of Phases 1–4 that
   is in scope), compute the cumulative diff, derive the language matrix (§2), build a fresh
   database per included language, and analyze.
3. **Diff** by `(ruleId, file)` against the baseline for each included language.
4. **Pass** when the NEW set is empty for every changed language, **or** each new finding is
   explicitly triaged with a written rationale (no blanket suppressions). Any new
   command-injection, path-traversal, SSRF/exposure, or clear-text-secret finding on the new
   C++/Python surfaces is an automatic FAIL — fix in code.

---

## 4. Entry / exit criteria

- **Entry:** all phase code changes that will ship are **merged** (this is the final gate;
  it does not run against a partial tree). DECISIONS D1–D6 resolved for the phases that
  shipped. Baseline SARIFs exist under `.codeql/baseline/`.
- **Exit:** CodeQL is **clean (or triaged) for each changed language**, the language matrix
  provably matches the diff (i.e. no language was skipped that had changes, and none was run
  that had none), and [`STATUS.md`](./STATUS.md)'s CodeQL ledger records the final counts.

---

## 5. Focused manual review (in addition to the scan)

- [ ] `ga-audio-server` multipart parser bounds-checks sizes/counts; the `ffmpeg`
      normalization call passes an **argv array**, never a shell-interpolated string built
      from the uploaded filename or headers.
- [ ] Python download/op subsystem validates `model_id`/`model_path`/URL targets; no write
      escapes `MODEL_DIR`; `hf_token` never appears in logs or operation records.
- [ ] Child-process spawns (`llama-server --embeddings`, `sd-server`, `ga-audio-server`)
      build argument lists explicitly; no `shell=True` on attacker-influenced strings.
- [ ] No new `except: pass` / empty `catch` swallowing a subprocess or parse failure —
      failures surface as explicit errors (`/ready` false, `/health` degraded), never a
      silent fallback.

---

## 6. Report-back addition (final gate only)

```text
CODEQL GATE (end-only, changed-languages-only):
- Languages run (derived from diff): <e.g. cpp, python[, csharp]>
- Languages omitted + why (no diff): <e.g. csharp omitted — no .cs change>
- Non-analyzable artifacts noted (Dockerfile/nginx/shell → other gates): <ack>
- New vs baseline per language (ruleId+file): <count → ids/files or none>
- ga-audio-server upload/ffmpeg-argv review: <pass/fail>
- Python download/subprocess/secret-log review: <pass/fail>
- No new silent catch/except: <pass/fail>
```
