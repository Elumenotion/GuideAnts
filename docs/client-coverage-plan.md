# Client coverage effort — tracking

Baseline (2026-06-09): **43.97%** lines, 917 tests, 119 test files.

## Policy

- Tests and test infrastructure only — **no production source edits**
- Done when `npm run test:coverage` reports **≥85%** global lines
- No commits until user opens PR

## Checkpoints

| Checkpoint | Lines | Notes |
|------------|-------|-------|
| Baseline | 43.97% | Before pragmatic excludes |
| After Phase 1 infra | ~45% | Excludes + test infra refactor |
| After Phase 2 lanes | ~65% | Agents 1–8 |
| After gap fill | **85.05%** | 240 test files, 2430+ tests |
| Thresholds locked | 85 / 85 / 83 / 80 | lines / statements / functions / branches |
| Gap-close pass | **90.93%** | 259 test files, 2857 tests; sidebars + LexicalEditor + managers |
| Outlier pass | **91.18%** | 261 test files; all per-file lines ≥85% |

## Pragmatic excludes (vitest.config.ts)

- `**/*.css`, `**/*.json`, `**/index.ts`
- Stub provider forms (Anthropic, Azure OpenAI, OpenAI, Gemini, HF, OpenRouter)

## Agent lanes

See plan: Agent 0 infra → Agents 1–8 parallel tests → gap fill → lock 85% threshold.
