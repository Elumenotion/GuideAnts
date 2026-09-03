---
name: mssql-query
description: "Query the stack's SQL Server (mssql-express) from the sandbox: probe, databases, tables, schema, sample rows, read-only SQL, and opt-in DML/DDL. Connection string comes from GA_DB_CONNECTION_STRING in the Guide's Environment variables. Use when the user wants to inspect or query the GuideAnts database."
metadata:
  guideants:
    enabled: true
    display_order: 40
    requires_toolsets: [sandbox]
---
Queries the stack's **SQL Server** (`mssql-express` container) **from the
sandbox**, using the connection string supplied through the environment
(`GA_DB_CONNECTION_STRING` — set on the Guide, marked secret). The tool
self-bootstraps `pymssql` on first use (one `pip install`), then everything is
local Python.

## Database at a glance

**Read `references/database.md` first** — it is the persistent understanding of
this database (table names, column gotchas, feature areas, how the tables
join, the enum dictionary, a *reading a conversation* recipe, *usage,
performance, and undone turns*, *guides, projects, and environments*, and a
verified core-query library), so you do not re-derive the schema each session.

The short version: GuideAnts is a **Projects > Notebooks** workspace. Each
notebook has **files** (`NotebookFiles`, versioned `ContentFiles`) and
**conversations** (`NotebookConversations` -> `NotebookConversationMessages`
+ `ConversationTurns`). **Guides/Assistants** (`Assistants`, `Kind` 1=Guide)
package the way of working; **execution** is `AgentInvocations`
(-> `AgentInvocationMessages` for detail) + `JobQueue`; **cost/telemetry** is
`UsageEvents`; **file provenance** is `FileLineageEvents`. `UsageEvents` has
a nullable FK to nearly every other table, so it is the key for
"what/who/cost" questions. **SQL table names are plural** (the doc uses
singular entity names); verify column details with `schema <Table>`.

## How the instance is wired

The skill runs **inside the `guideants-ai` container**, inside the running
compose stack; `mssql-express` is a sibling service (stock compose: service
`mssql-express`, image `mssql2025-express-fts`, publishes `1434:1433` on the
host). The connection string is the ADO.NET one the webapi carries as
`ConnectionStrings__DefaultConnection` — here it arrives as
`GA_DB_CONNECTION_STRING` (Guide Environment variable, secret).

Endpoint resolution in the tool: `--endpoint` → `GA_DB_ENDPOINT` → the
`Server=` field of the connection string (only when that host is an IP
literal or `localhost`; the compose service name `mssql-express` does not
resolve from the `ai` container) → default `host.docker.internal:1434`.

## Dependencies

Python 3 + `pymssql`. The tool installs it automatically on first run
(`pip install pymssql`, ~30 s, one-time). Prerequisite: the stack is up and
the mssql container is running.

## What to run

```bash
TOOL="Skills/mssql-query/scripts/mssql_tool.py"
python3 $TOOL probe                                  # connect + summary (run first)
python3 $TOOL tables                                 # all tables + row counts
python3 $TOOL schema <Table>                         # columns, types, PK
python3 $TOOL sample <Table> -n 5                    # top rows
python3 $TOOL query "SELECT ..."                     # read-only SQL, --json / --csv / --limit
python3 $TOOL execute "UPDATE ..." --allow-write     # DML/DDL, only on explicit request
```

Common flags (all subcommands): `--db <database>` (override Initial Catalog),
`--endpoint HOST:PORT`, `--json`. `query` adds `--limit N` (default 500, 0 =
none) and `--csv FILE` (bare CWD filename); `sample` adds `--csv FILE`.

## Behavior notes

- `query` is a hard read-only gate: the first token must be `SELECT`, `WITH`,
  or `DECLARE`. Anything else is rejected with a pointer to `execute`.
- Table names in `schema` / `sample` are validated against
  `[A-Za-z0-9_](.[A-Za-z0-9_])?` before use — no raw identifier interpolation.
- Result sets are capped (`query`: `--limit`, default 500) to keep the
  conversation context small; a cap notice goes to stderr so `--json` stdout
  stays clean.
- `RowVersion`/timestamp columns render as hex strings (not raw bytes).
- Tinyint status columns are app enums — check the enum in
  `src/server/GuideAntsApi/DataModel` before interpreting values.

## Failure modes

- **`GA_DB_CONNECTION_STRING` not set** — the tool exits with a setup hint.
  Set it on the Guide (guide editor → Environment variables, mark secret);
  the value is the webapi's `ConnectionStrings__DefaultConnection` (get it on
  the host: `docker exec <webapi-container> printenv
  ConnectionStrings__DefaultConnection`).
- **connection refused to `host.docker.internal:1434`** — mssql container down
  or a different port mapping. On the host: `docker ps` (container up?) and
  `docker port guideants-mssql-express-1` (published port). Then set
  `GA_DB_ENDPOINT` or `--endpoint` accordingly.
- **login failed** — the password/user in the string does not match this
  server (wrong instance or stale password).
- **table not found** — wrong database; list with `tables --db <name>`.
- **"Invalid object name"** — the SQL tables are plural (`Notebooks`, not
  `Notebook`); the reference doc uses singular entity names. Also check the
  column-gotcha list in `references/database.md` (`Notebooks.Title` not
  `Name`, `MessageAttachments.Type` not `AttachmentType`, ...).
- **single-container MSSQL builds** (`docker/docker-compose.mssql.yml`): no
  host DB port is published; use `docker exec` into that container with
  `sqlcmd` instead of this skill.
- **`pymssql` install fails** — sandbox has no outbound network for pip.

## Security rules

- **Never** print the connection string or its password (the tool masks
  both). Do not write the connection string to any file or notebook output.
- Prefer read-only `query`; use `execute --allow-write` only when the user
  explicitly asked for a change, and report what changed (rows affected).
- Results may contain user data — summarize rather than dumping large result
  sets into the conversation; use `--csv` for big exports.

## Reporting

State the endpoint + database used, the query (or command), the row count, and
the CWD filename of any CSV written (the UI displays it under `Output/`). For
writes: report rows affected.
