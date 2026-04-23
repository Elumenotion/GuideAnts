# Testing Guide for GuideAnts Electron Client

> This guide distills everything we learned while stabilising Vitest + React-Testing-Library in the GuideAnts Electron client. Follow these patterns when adding **any new unit, integration, or component tests** so we don't re-introduce hangs, auth loops, or memory leaks.

---

## 1 – Tech-Stack Recap

| Layer                           | Library / Tool                       |
|---------------------------------|--------------------------------------|
| Test-Runner                     | **Vitest ≥ 1.3**                     |
| Assertion / RTL helpers         | **@testing-library/react**           |
| DOM emulation                   | **jsdom** (auto by Vitest)           |
| Coverage                        | **@vitest/coverage-v8** (optional)   |
| Build / Bundler                 | **Vite**                             |
| Framework                       | **React 19 + TypeScript strict**     |

All commands assume you are inside the `client/` directory.

```bash
# Run all tests (watch mode)
npm test
# CI / coverage
npm run test:coverage
```

---

## 2 – File & Folder Conventions

```
client/
  src/
    components/
      Spinner.tsx
      __tests__/Spinner.test.tsx   # ← colocated tests
    pages/
      Login.tsx
      __tests__/Login.test.tsx
  test/
    test-utils.tsx                # ← global custom render & mocks
    setup.ts                      # ← runs before each suite (see vitest.config.ts)
  vitest.config.ts
```

*   **One component ⇒ one test file** in a sibling `__tests__` directory.
*   Snapshot testing is allowed but prefer explicit queries (text, roles, labels).
*   Keep test line-length ≤ 120 chars (same as code).

---

## 3 – The Global Test Harness (`test-utils.tsx`)

`test/test-utils.tsx` wraps every render call with:

1.  A **stable** `MemoryRouter` so routing works without hitting the real electron main process.
2.  A **stubbed MSAL provider** returning one *singleton* `PublicClientApplication`-like object.
3.  Default `useIsAuthenticated` → `false` (override per test).

```tsx
// usage in tests
import { render, screen } from "../../test/test-utils";

render(<MyComponent />);
```

### Why "stable"?

When the mock returns a *new* object on every render, React thinks the `instance` prop changed; any effect that depends on it re-fires → **infinite loop → OOM**. Always reuse the same object reference (e.g. store it in a module-level const).
 
---

## 4 – Mocking & Isolation Guidelines  _(NEW)_

> The most frequent cause of "one test passes, full suite fails" bugs is a **leaking mock**. Because Vitest hoists `vi.mock()` calls to the top of the file, a mock declared at module level lives for the entire lifetime of that module graph. If test–file isolation is disabled a mock from one spec silently mutates the behaviour of every spec that executes after it.

### 4.1  Always run with per-file isolation

`vitest.config.ts` is now configured with:

```ts
  test: {
    /* …other flags… */
    isolate: true,
  }
```

This launches a fresh module graph for **each** spec file, guaranteeing that mocks, singletons and global state cannot bleed across files. Do **not** set `isolate: false` unless you have a very specific, documented reason (e.g. performance debugging).

### 4.2  Mock only what you own, as locally as possible

1. **Prefer function stubs over module mocks**. If a component depends on a prop callback, pass a `vi.fn()` instead of mocking the whole module.
2. If you truly need to mock a module:
   * Use `vi.mock()` **inside** the test (or `beforeEach`) when practical so it is restored automatically when the test ends.
   * Or pair a top-level `vi.mock()` with `vi.unmock()` / `vi.restoreAllMocks()` in an `afterEach`.
3. Avoid wildcard mocks like `vi.mock('*')` – they mask real integration problems and complicate refactors.

### 4.3  Beware of hoisting side-effects

Because `vi.mock()` is hoisted:

```ts
// ❌  Bad – this variable is undefined at mock-execution time
const mySpy = vi.fn();
vi.mock('../api', () => ({ api: { fetch: mySpy } }));
```

Instead:

```ts
// ✅  Good – declare inside the factory
vi.mock('../api', () => {
  const fetch = vi.fn();
  return { api: { fetch } };
});
// Access it later via import:  const { api } = await import('../api');
```

### 4.4  Reset between tests

Even with isolation it's good practice to clean state:

```ts
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});
```

### 4.5  Use `jest-dom` queries that fail loudly

The `LoadingSpinner` incident only surfaced because the tests asserted on
user-visible text. Keep doing that – it reveals mock leaks quickly.

---

*(The remainder of the guide is unchanged)*
