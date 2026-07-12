# Phase 1 — INI contract + warmup_desired_ini.py

## Goal

Define `warmup-desired.ini` and `.warmup-state.json` schemas; implement atomic
INI I/O with revision bump and state sync helpers.

## Deliverables

| File | Purpose |
|------|---------|
| `admin-service/warmup_desired_ini.py` | Parse/serialize/validate; atomic write under lock |
| `admin-service/warmup_state.py` | `.warmup-state.json` document builder + atomic write |
| `admin-service/tests/test_warmup_desired_ini.py` | Round-trip, revision bump, state sync |

## Gate commands

```bash
python -m unittest discover -s docker/build/guideants-ai/admin-service/tests -p "test_*.py" -v
```

## Gate checklist

- [x] INI round-trip for plan fixture shape
- [x] Validation rejects `desired=warm` without model/router ref
- [x] Atomic write bumps revision on content change
- [x] Identical content write is idempotent (no revision bump)
- [x] State JSON tracks `desiredRevision` / `appliedRevision` / per-service desired+applied
