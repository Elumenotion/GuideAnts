# Curated Local Llama — CodeQL Security Gate

This feature crosses untrusted repository metadata, authenticated HF credentials,
filesystem paths, downloads, INI generation, process restart, JSON request shaping,
and destructive model lifecycle operations. Security scanning is a phase gate.

Full local reference: [`../codeql-local-solution-runbook.md`](../codeql-local-solution-runbook.md).

## 1. Scan policy

- Scan C#, Python, and JavaScript/TypeScript.
- C# uses `--build-mode=none --source-root=.`.
- Use only the language code-scanning suites.
- Establish a local Phase 0 baseline from the exact accepted worktree.
- Pass condition: zero new findings relative to that baseline, plus remediation of
  any pre-existing finding in code materially changed by this feature.
- Fix findings in code. Do not suppress, model away, or exclude changed source.
- Do not claim GitHub parity when the branch/commit has no matching remote scan.

## 2. Commands

Preferred repository wrapper:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-codeql-sln-triage.ps1 -CleanCodeqlOutputs -SkipGitHubParityCheck
```

If the wrapper cannot produce a local-only result, use explicit databases:

```powershell
$codeql = if ($env:CODEQL_PATH) { $env:CODEQL_PATH } else { (Get-Command codeql -ErrorAction Stop).Source }

& $codeql database create .codeql/db-csharp --language=csharp --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-csharp codeql/csharp-queries:codeql-suites/csharp-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-csharp.sarif

& $codeql database create .codeql/db-python --language=python --build-mode=none --source-root=. --overwrite
& $codeql database analyze .codeql/db-python codeql/python-queries:codeql-suites/python-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-python.sarif

& $codeql database create .codeql/db-javascript --language=javascript --build-mode=none --source-root=src/client --overwrite
& $codeql database analyze .codeql/db-javascript codeql/javascript-queries:codeql-suites/javascript-code-scanning.qls `
  --download --format=sarifv2.1.0 --output=.codeql/results-javascript.sarif
```

Save Phase 0 SARIF files under `.codeql/baseline/` and record per-language/rule
counts in `STATUS.md`. Later scans compare normalized `(rule, file, region)` rows.

## 3. Required scan points

| Point | Primary risk |
|---|---|
| Phase 0 | accepted local baseline |
| Phase 1A | repository/revision input, HF token handling, remote metadata |
| Phase 1B | persisted JSON contracts, operation/provenance data |
| Phase 2 | path traversal, arbitrary file writes, INI injection, unsafe logs |
| Phase 3 | settings serialization, projection file, process control |
| Phase 4 | operation input, durable state, partial completion |
| Phase 5 | repair/replacement/deletion paths, alias concurrency |
| Phase 6 | repository metadata, documentation URLs, operation rendering |
| Phase 7 | advanced operator input and preset key/value transport |
| Phase 8A/final | full feature closeout |

## 4. Manual security checks in addition to CodeQL

CodeQL does not prove the complete domain contract. At the relevant gate verify:

- HF tokens are server-resolved, never accepted from a browser payload, never
  returned by an endpoint, and never logged.
- Repository identifiers and revisions are passed to the HF client as data, not
  concatenated into commands or arbitrary URLs.
- Every remote artifact path is normalized, remains under its staging/target root,
  and cannot escape via absolute paths, separators, drive prefixes, or `..`.
- Downloaded filenames cannot overwrite the INI, projection, operation journal, or
  files belonging to another alias.
- Manifest documentation URLs are presentation-only and never become download hosts.
- INI aliases, keys, and values cannot add sections/newlines or process arguments.
- Runtime projection writes are atomic and revisioned.
- Lifecycle endpoints are Admin-protected by the existing settings policy.
- Error responses and logs sanitize user/repository/alias values.
- Operation/status DTOs contain no token, local host path, or secret settings.
- Custom preset values remain data; `start-llama.sh` must construct argument arrays
  without `eval`.

## 5. Report addition

Every sensitive phase includes:

```text
CODEQL REPORT
- C# build-mode=none and code-scanning suites: <yes/no>
- Baseline commit/worktree identity: <value>
- C# findings: <total> new=<count>
- Python findings: <total> new=<count>
- JavaScript findings: <total> new=<count>
- New findings: <rule @ file:line, or none>
- Changed-code pre-existing findings remediated: <list or none>
- Suppressions/exclusions added: <must be none>
- Manual checks: token=<pass/fail> paths=<pass/fail> INI=<pass/fail> process=<pass/fail>
```
