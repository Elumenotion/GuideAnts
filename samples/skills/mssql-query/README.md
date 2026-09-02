# mssql-query skill

Query the stack's **SQL Server** (`mssql-express`) from the sandbox — probe,
databases, tables, schema, sample rows, read-only SQL, and opt-in DML/DDL.

The skill runs **inside the `guideants-ai` container**, i.e. inside the running
compose stack. `mssql-express` is a sibling service. The connection string
comes from the **Guide's Environment variables** (`GA_DB_CONNECTION_STRING`,
secret); no SSH, no host tooling, nothing outside the sandbox at runtime.

## Skill

| Skill | What it does |
|-------|--------------|
| `mssql-query` | `scripts/mssql_tool.py` — pymssql client: probe / databases / tables / schema / sample / query / execute |

## Required Environment (guide → sandbox)

Set on the **Guide** (guide editor → **Environment variables**).

| Variable | Value | Secret |
|----------|-------|--------|
| `GA_DB_CONNECTION_STRING` | the webapi's ADO.NET connection string | **Yes** |

Optional:

| Variable | Default | Purpose |
|----------|---------|---------|
| `GA_DB_ENDPOINT` | `host.docker.internal:1434` | host:port the tool connects to, when the DB is reachable on a different port |

### Where to get the connection string (one-time setup)

The webapi container carries it. On the host, once:

```powershell
docker exec guideants-webapi-ui printenv ConnectionStrings__DefaultConnection
```

Paste that value in as `GA_DB_CONNECTION_STRING`. The tool only needs its
`Server`, `Initial Catalog`, `User ID`, and `Password` fields; it ignores the
rest.

### Endpoint

The `Server=mssql-express,1433` in the string is the compose-network service
name; it does not resolve from the `ai` container. The tool therefore defaults
to the **host-published port** — stock compose publishes mssql `1434:1433`,
so the default is `host.docker.internal:1434`. If your deployment publishes a
different port (check `docker port mssql-express-1`), set `GA_DB_ENDPOINT`.

## Dependencies

None beyond the sandbox Python. The tool self-installs `pymssql` on first run
(one-time `pip install`, ~30 s).

## Test from the sandbox

```bash
python3 Skills/mssql-query/scripts/mssql_tool.py probe
```

Expect `server: Microsoft SQL Server …`, the endpoint used, the database list,
and the largest tables. Or run the packaged test:

```bash
python3 Skills/mssql-query/scripts/test_mssql_query.sh
```
(needs `GA_DB_CONNECTION_STRING` in the environment)

## Security notes

- Mark `GA_DB_CONNECTION_STRING` **secret** in the guide editor.
- The tool never prints the password or the full connection string.
- `query` is read-only by construction; writes require `execute --allow-write`
  and should only be used on explicit request.

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| `GA_DB_CONNECTION_STRING not set` | Guide Environment variables not set |
| connection refused | DB down, or different published port — `docker ps`, `docker port mssql-express-1`, then set `GA_DB_ENDPOINT` |
| login failed | user/password in the string does not match this server |
| table not found | wrong database — `tables --db <name>` |
| `pymssql` install fails | sandbox has no outbound network for pip |
