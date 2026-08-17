# Browser Session Capture Requirements

Requirement traceability for the capture integrity rebuild. Each `R-*` ID maps to automated tests and/or acceptance gates.

## Capture contract

| ID | Requirement | Test / Gate |
|----|-------------|-------------|
| R-CAP-01 | Continuous monitor + mic recording | `test_screen_recorder.py`, Capture gate |
| R-CAP-02 | Single muxed A/V media clock | `ffmpeg_av.py` integration, Capture gate |
| R-CAP-03 | Preflight before `recording` | `cmd_preflight`, Capture gate |
| R-CAP-04 | Required tracks; stall → exit recording | `integrity.py`, `test_capture.py`, Capture gate |
| R-CAP-05 | Failure seal + modal + same-input restart | `integrity.py`, Live failure-injection matrix |
| R-CAP-06 | `recovered_with_gap` never `complete` | `integrity.py`, Recovery gate |
| R-CAP-07 | Two-second durability bound | `ffmpeg_av.py` segments, Durability gate |
| R-CAP-08 | Monotonic orchestration timestamps | `journal.py`, Durability gate |
| R-CAP-09 | Persist exact device endpoint IDs | Capture gate |
| R-CAP-10 | Drops/overruns are integrity failures | `integrity.py`, Capture gate |

## Browser and application evidence

| ID | Requirement | Test / Gate |
|----|-------------|-------------|
| R-META-01 | Persistent multi-tab Playwright | Evidence gate |
| R-META-02 | Initial checkpoint before recording | Evidence gate |
| R-META-03 | Page-bound callbacks, not URL | Evidence gate |
| R-META-04 | Durable navigation/DOM events | Evidence gate |
| R-META-05 | Checkpoint failures are required-track failures | Evidence gate |
| R-META-06 | Foreground app crop at transitions | Evidence gate |
| R-META-07 | Event-hook intervals with typed gaps | Evidence gate |
| R-META-08 | Lookup never clamps; returns `unavailable` | `test_time_map_clamp.py`, `test_time_map.py` |

## Storage, rolling, recovery, resume

| ID | Requirement | Test / Gate |
|----|-------------|-------------|
| R-STO-01 | Two-second fsynced hashed segments | `ffmpeg_av.py`, Durability gate |
| R-STO-02 | Durability vs rolling parts distinct | Recovery gate |
| R-STO-03 | Disk budgeting hard-stop | Storage gate |
| R-STO-04 | No automatic source deletion | `test_audit.py` |
| R-STO-05 | Journal is source of truth | `journal.py`, Recovery gate |
| R-STO-06 | Resume appends, never moves source | `test_resume.py`, Recovery gate |
| R-STO-07 | Salvage preserves originals | `test_recovery.py`, Recovery gate |
| R-STO-08 | `complete` requires validated coverage | `audit.py`, `test_audit.py` |

## Analysis and compaction

| ID | Requirement | Test / Gate |
|----|-------------|-------------|
| R-CMP-01 | `analyze-idle` read-only on complete source | `audit.py` gate in `compact.py` |
| R-CMP-02 | Removable = static + silent + fully covered | Analysis gate |
| R-CMP-03 | Every-frame conservative visual proof | `visual_salvage.py`, Analysis gate |
| R-CMP-04 | Audio gaps are errors, not silence | `test_audit.py`, Analysis gate |
| R-CMP-05 | Boundary protection shrinks removal | `test_compact.py` |
| R-CMP-06 | `idle.json` with source hashes | `compact.py` stale check |
| R-CMP-07 | Empty removal → `no_changes` | `compact.py`, Compaction gate |
| R-CMP-08 | No `apad`/`tpad`/invented samples | `test_compact.py` |
| R-CMP-09 | Temp build, verify, atomic publish | `compact.py`, Compaction gate |
| R-CMP-10 | `verified` requires content proof | `compact.py` `_verify_compact` |
| R-CMP-11 | Transcription WAV from verified mux | Editorial gate |
| R-CMP-12 | Compact lookup uses compact PTS | `lookup.py`, `test_lookup.py` |
| R-CMP-13 | Prune requires two-phase confirmation | Deletion gate |
| R-CMP-14 | `visual-salvage` video-only degraded | `visual_salvage.py`, Visual-salvage gate |
| R-CMP-15 | Salvage on 20260817 session, unchanged source | `test_audit.py`, Visual-salvage gate |

## Truthful operation

| ID | Requirement | Test / Gate |
|----|-------------|-------------|
| R-OPS-01 | Nonzero exit on incomplete state | `browser_session_capture.py` commands |
| R-OPS-02 | External watchdog | `watchdog.py`, Live failure-injection |
| R-OPS-03 | `/health` reports integrity state | `control_server.py` |
| R-OPS-04 | Docs distinguish status vocabulary | Documentation gate |

## Mandatory negative regressions

| Scenario | Expected | Test |
|----------|----------|------|
| 840533 ms video + 43520 ms audio + interrupted | `AUDIO_COVERAGE_GAP`, audit fail | `test_audit.py::test_failed_session_reports_expected_codes` |
| Zero checkpoints | `PLAYWRIGHT_EVIDENCE_EMPTY` | `test_audit.py` |
| Deceptive compact with apad | `COMPACT_SYNTHETIC_AUDIO` | `test_audit.py` |
| `compact` on failed session | Blocked | `test_audit.py::test_compact_rejects_failed_session` |
| `idle_ranges=[]` | `no_changes`, no re-encode | Compaction gate |
| `apad` in compaction | `ERROR_SYNTHETIC_MEDIA_FILTER` | `test_compact.py` |
| Out-of-range source time | `unavailable`, not clamped | `test_time_map_clamp.py` |
| Visual salvage on 20260817 | `visual_only_degraded`, no audio, source unchanged | Visual-salvage gate |

## Regression fixture

Session `20260817_170050_session` (redacted descriptor):

- Video: 840,533 ms
- Audio: 43,520 ms
- Checkpoints: 0
- Status: `interrupted`
- Compact: deceptive (840,533 ms padded audio, zero removal)

Preserved at `recordings/sessions/20260817_170050_session/`.
