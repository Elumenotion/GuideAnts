# Runtime Parity Gate

Last updated: 2026-07-12

Run after Phases 2, 3, 5, and final Phase 7.

## Purpose

Prove limits are enforced identically on every `ThreadRun` entry path — private notebook,
published conversation, nested `Agent.Invoke`, and evaluator reopen.

## Pass criteria

### 1. Entry path coverage

| Path | Verified |
|------|----------|
| Private notebook `SendMessageStreamAsync` | [ ] |
| Published conversation stream | [ ] |
| `Agent.Invoke` nested `ThreadRun` (child budget) | [ ] |
| Parent budget decrements on `Agent.Invoke` (= 1) | [ ] |
| Evaluator reopen inherits `ToolLimitState` (T14) | [ ] |
| Client-handled tool emission counts toward budget (T8) | [ ] |

### 2. Counting semantics

- [ ] Parallel batch of N tools in one round increments counter by N.
- [ ] Blocked-at-limit tools still increment counter (proposal §5: "executed or blocked = 1").
- [ ] `ReadWeb` / local function tools count in parent run.
- [ ] `skills.list` / `skills.read` count when skills execution is present.
- [ ] MCP tools count when MCP execution is present.

### 3. Nested crew budget (required — Phase 6)

- [ ] Child budget = `min(remaining parent, GuideMember.MaxToolCallsPerInvocation ?? child.MaxToolCallsPerTurn)`.

### 4. Null = unlimited (T2)

- [ ] Assistant with all limit fields null behaves identically to pre-feature baseline
      (regression test on a simple turn).

## Fail modes

- `path-skipped` — limit check missing on any stream entry path
- `budget-reset` — evaluator reopen resets counters
- `nested-budget-wrong` — child ignores parent or member override incorrectly
- `client-tools-free` — client-handled tools not counted when T8 requires it
