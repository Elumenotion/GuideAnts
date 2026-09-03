#!/usr/bin/env python3
"""mssql_tool.py - query the stack's SQL Server (mssql-express) from the sandbox.

The connection string comes from the GA_DB_CONNECTION_STRING environment
variable (set on the Guide, marked secret; the same ADO.NET string the webapi
container carries as ConnectionStrings__DefaultConnection).

Endpoint: the in-compose server name (e.g. mssql-express) does not resolve
from the sandbox, so by default the tool connects to the host-published port
host.docker.internal:1434 (stock compose publishes 1434:1433). Override with
GA_DB_ENDPOINT or --endpoint.

Subcommands:
  probe                  endpoint + login check, server version, databases, top tables
  databases              list databases
  tables                 list tables with row counts (optionally --db)
  schema TABLE           columns, types, nullability, primary key
  sample TABLE           top N rows (default 10)
  query SELECT-SQL       read-only SQL (first keyword SELECT/WITH/DECLARE)
  execute SQL            DML/DDL - requires --allow-write, commits
"""

import argparse
import csv
import ipaddress
import json
import os
import re
import subprocess
import sys

NL = chr(10)
DEFAULT_ENDPOINT = "host.docker.internal:1434"
CS_ENV = "GA_DB_CONNECTION_STRING"
EP_ENV = "GA_DB_ENDPOINT"
DEFAULT_LIMIT = 500
SAMPLE_DEFAULT = 10
TABLE_RE = re.compile(r"^[A-Za-z0-9_]+([.][A-Za-z0-9_]+)?$")
MAX_CELL = 80


def die(msg):
    sys.stderr.write(msg + NL)
    sys.exit(1)


def ensure_pymssql():
    try:
        import pymssql
        return pymssql
    except ImportError:
        pass
    sys.stderr.write("pymssql not installed - running: pip install pymssql (one-time, ~30s)")
    try:
        subprocess.check_call([sys.executable, "-m", "pip", "install", "--quiet", "pymssql"])
    except Exception as exc:
        die(
            "ERROR: could not install pymssql automatically: " + str(exc) + NL +
            "The sandbox needs outbound network for pip. Install pymssql in the "
            "sandbox image or retry with network access."
        )
    import pymssql
    return pymssql


def parse_adonet_cs(cs):
    """Parse an ADO.NET connection string into a dict of lowercase keys.

    Quote-aware: single-quoted values may contain semicolons; doubled single
    quotes inside a value are an escaped quote.
    """
    segments = []
    cur = []
    in_quote = False
    for ch in cs:
        if ch == "'":
            in_quote = not in_quote
            cur.append(ch)
        elif ch == ";" and not in_quote:
            segments.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    if cur:
        segments.append("".join(cur))
    fields = {}
    for seg in segments:
        if "=" not in seg:
            continue
        k, v = seg.split("=", 1)
        k = k.strip().lower()
        v = v.strip()
        if len(v) >= 2 and v.startswith("'") and v.endswith("'"):
            v = v[1:-1].replace("''", "'")
        fields[k] = v
    return fields


def split_server(server_field):
    """Server field -> (host, port). Handles host, host:port, host,port,
    tcp:host,port, (local) / . / (localhost)."""
    s = (server_field or "").strip()
    if s.lower() in ("(local)", ".", "(localhost)"):
        return "localhost", 1433
    if s.lower().startswith("tcp:"):
        s = s[4:]
    host, port = s, 1433
    if "," in s:
        host, _, p = s.partition(",")
    elif ":" in s:
        host, _, p = s.partition(":")
    else:
        p = ""
    if p.strip():
        try:
            port = int(p.strip())
        except ValueError:
            pass
    host = host.strip()
    return (host or "localhost"), port


def resolve_endpoint(cli_ep, cs_fields):
    if cli_ep:
        return cli_ep.strip(), "--endpoint"
    env_ep = (os.environ.get(EP_ENV) or "").strip()
    if env_ep:
        return env_ep, EP_ENV
    server = cs_fields.get("server") or cs_fields.get("data source") or ""
    host, port = split_server(server)
    try:
        ipaddress.ip_address(host)
        local = True
    except ValueError:
        local = host.lower() in ("localhost", "127.0.0.1")
    if local:
        return "%s:%d" % (host, port), "connection string (local server)"
    return DEFAULT_ENDPOINT, "default (host-published port 1434)"


def parse_endpoint(ep):
    if ":" in ep:
        host, _, p = ep.rpartition(":")
        try:
            return host, int(p)
        except ValueError:
            die("ERROR: bad --endpoint " + repr(ep) + " (expected HOST:PORT)")
    return ep, 1433


def get_cs_fields(args):
    cs = (args.connection_string or os.environ.get(CS_ENV) or "").strip()
    if not cs:
        die(
            "ERROR: " + CS_ENV + " is not set in the sandbox environment." + NL +
            "Set it on the Guide (guide editor - Environment variables, mark secret)." + NL +
            "Value = the webapi ADO.NET connection string; get it on the host:" + NL +
            "  docker exec guideants-webapi-ui printenv ConnectionStrings__DefaultConnection"
        )
    fields = parse_adonet_cs(cs)
    if not fields.get("server") and not fields.get("data source"):
        die("ERROR: connection string has no Server= / Data Source= field - is it an ADO.NET connection string?")
    return fields


def open_conn(pymssql, args, fields):
    ep, ep_source = resolve_endpoint(args.endpoint, fields)
    host, port = parse_endpoint(ep)
    user = fields.get("user id") or fields.get("uid") or "sa"
    password = fields.get("password") or fields.get("pwd") or ""
    db = (args.db or fields.get("database") or fields.get("initial catalog") or "master")
    try:
        conn = pymssql.connect(server=host, port=port, user=user,
                               password=password, database=db, login_timeout=10)
    except Exception as exc:
        msg = str(exc)
        hint = ""
        low = msg.lower()
        if "refused" in low or "timed out" in low or "unreachable" in low:
            hint = (NL + "Hints: is the mssql container up (docker ps)? Is the published port "
                    "still 1434 (docker port guideants-mssql-express-1)? Set GA_DB_ENDPOINT or "
                    "--endpoint if the mapping changed.")
        elif "login" in low or "password" in low or "access is denied" in low:
            hint = NL + "Hint: the user/password in the connection string do not match this server."
        die("ERROR: cannot connect to SQL Server at %s (endpoint from %s) as user %r in database %r:%s%s"
            % (ep, ep_source, user, db, NL, msg, hint))
    return conn, ep, ep_source, db


def qual(table):
    if not TABLE_RE.match(table):
        die("ERROR: invalid table name " + repr(table) + " (expected Name or Schema.Name, alphanumerics/underscore only)")
    if "." in table:
        sch, _, nm = table.partition(".")
        return "[%s].[%s]" % (sch, nm)
    return "[dbo].[%s]" % table


def run_sql(cur, sql):
    """Execute and translate DB exceptions into a clean error exit."""
    try:
        cur.execute(sql)
    except Exception as exc:
        die("ERROR: SQL error: " + str(exc).strip())


def fetch_all_capped(cur, limit):
    rows = []
    while True:
        batch = cur.fetchmany(500)
        if not batch:
            break
        rows.extend(batch)
        if limit and len(rows) >= limit:
            rows = rows[:limit]
            break
    return rows


def scrub(rows):
    """Render bytes/bytearray cells (e.g. RowVersion timestamps) as hex strings."""
    return [tuple(x.hex() if isinstance(x, (bytes, bytearray)) else x for x in r) for r in rows]


def print_table(cols, rows):
    def cell(v):
        if v is None:
            return ""
        s = str(v)
        return s if len(s) <= MAX_CELL else s[:MAX_CELL - 3] + "..."
    widths = [len(c) for c in cols]
    for r in rows:
        for i, v in enumerate(r):
            if i < len(widths):
                widths[i] = max(widths[i], min(len(cell(v)), MAX_CELL))
    def fmt(r):
        return "  ".join(cell(v).ljust(widths[i]) for i, v in enumerate(r)).rstrip()
    print(fmt(cols))
    print(fmt(["-" * w for w in widths]))
    for r in rows:
        print(fmt(r))


def write_csv(path, cols, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(cols)
        w.writerows(rows)
    print("wrote " + path + " (%d rows)" % len(rows))


def emit_json(obj):
    print(json.dumps(obj, indent=2, default=str))


# ---------------------------------------------------------------- commands

def cmd_probe(args):
    pymssql = ensure_pymssql()
    fields = get_cs_fields(args)
    conn, ep, ep_source, db = open_conn(pymssql, args, fields)
    cur = conn.cursor()
    run_sql(cur, "SELECT @@VERSION")
    version = cur.fetchone()[0].split(NL)[0].strip()
    run_sql(cur, "SELECT SUSER_SNAME()")
    user = cur.fetchone()[0]
    run_sql(cur, "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name")
    databases = [r[0] for r in cur.fetchall()]
    run_sql(cur,
        "SELECT TOP 10 t.name, p.rows FROM sys.tables t "
        "JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) "
        "ORDER BY p.rows DESC")
    top = [{"table": r[0], "rows": r[1]} for r in cur.fetchall()]
    conn.close()
    if args.json:
        emit_json({"endpoint": ep, "endpoint_source": ep_source, "database": db,
                   "user": user, "server": version, "databases": databases, "top_tables": top})
        return
    print("endpoint: " + ep + "  (from " + ep_source + ")")
    print("server:   " + version)
    print("user:     " + user)
    print("database: " + db)
    print()
    print("databases: " + ", ".join(databases))
    print()
    print("top tables in " + db + ":")
    for t in top:
        print("  " + t["table"] + ": " + format(t["rows"], ","))


def cmd_databases(args):
    pymssql = ensure_pymssql()
    fields = get_cs_fields(args)
    conn, ep, _, _ = open_conn(pymssql, args, fields)
    cur = conn.cursor()
    run_sql(cur, "SELECT name, state_desc FROM sys.databases WHERE state_desc = 'ONLINE' ORDER BY name")
    rows = [(r[0], r[1]) for r in cur.fetchall()]
    conn.close()
    if args.json:
        emit_json([{"name": n, "state": s} for n, s in rows])
        return
    print_table(["name", "state"], rows)


def cmd_tables(args):
    pymssql = ensure_pymssql()
    fields = get_cs_fields(args)
    conn, ep, _, db = open_conn(pymssql, args, fields)
    cur = conn.cursor()
    run_sql(cur,
        "SELECT t.name, p.rows FROM sys.tables t "
        "JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) "
        "ORDER BY p.rows DESC")
    rows = [(r[0], r[1]) for r in cur.fetchall()]
    conn.close()
    if args.json:
        emit_json({"database": db, "tables": [{"table": n, "rows": r} for n, r in rows]})
        return
    print("database: " + db)
    print_table(["table", "rows"], rows)


SCHEMA_SQL = (
    "SELECT c.name, ty.name,"
    " CASE WHEN ty.name IN ('varchar','nvarchar','char','nchar','varbinary','binary')"
    "      THEN CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END"
    "      ELSE CAST(c.precision AS varchar(10)) + (CASE WHEN c.scale > 0 THEN '(' + CAST(c.scale AS varchar(10)) + ')' ELSE '' END)"
    "      END,"
    " c.is_nullable,"
    " CASE WHEN pk.object_id IS NOT NULL THEN 1 ELSE 0 END"
    " FROM sys.columns c"
    " JOIN sys.types ty ON ty.user_type_id = c.user_type_id"
    " LEFT JOIN ("
    "   SELECT ic.object_id, ic.column_id FROM sys.index_columns ic"
    "   JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id"
    "   WHERE i.is_primary_key = 1"
    " ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id"
    " WHERE c.object_id = OBJECT_ID(N" + chr(39) + "{ident}" + chr(39) + ")"
    " ORDER BY c.column_id"
)


def cmd_schema(args):
    pymssql = ensure_pymssql()
    fields = get_cs_fields(args)
    q = qual(args.table)
    conn, ep, _, db = open_conn(pymssql, args, fields)
    cur = conn.cursor()
    run_sql(cur, SCHEMA_SQL.format(ident=q))
    rows = [(n, t, l, "yes" if nul else "no", "PK" if pk else "")
            for n, t, l, nul, pk in cur.fetchall()]
    conn.close()
    if not rows:
        die("ERROR: table not found: " + args.table + " (database " + db + ") - check tables --db")
    if args.json:
        emit_json({"table": args.table, "database": db,
                   "columns": [{"name": n, "type": t, "length": l, "nullable": nu, "primary_key": bool(p)}
                               for n, t, l, nu, p in rows]})
        return
    print("table: " + args.table + "  (database " + db + ")")
    print_table(["column", "type", "len", "null", "key"], rows)


def cmd_sample(args):
    pymssql = ensure_pymssql()
    fields = get_cs_fields(args)
    q = qual(args.table)
    conn, ep, _, db = open_conn(pymssql, args, fields)
    cur = conn.cursor()
    n = max(1, int(args.n))
    run_sql(cur, "SELECT TOP " + str(n) + " * FROM " + q)
    cols = [d[0] for d in cur.description] if cur.description else []
    rows = scrub([tuple(r) for r in cur.fetchall()])
    conn.close()
    if args.csv:
        write_csv(args.csv, cols, rows)
        return
    if args.json:
        emit_json({"table": args.table, "database": db, "columns": cols,
                   "rows": [dict(zip(cols, r)) for r in rows]})
        return
    print("table: " + args.table + "  (database " + db + ") - top " + str(n) + " rows")
    print_table(cols, rows)


def cmd_query(args):
    pymssql = ensure_pymssql()
    fields = get_cs_fields(args)
    sql = args.sql.strip()
    while sql.endswith(";"):
        sql = sql[:-1].rstrip()
    if not sql:
        die("ERROR: empty SQL")
    if ";" in sql:
        die("ERROR: single statement only (remove extra semicolons).")
    first = sql.split(None, 1)[0].upper()
    if first not in ("SELECT", "WITH", "DECLARE"):
        die(
            "ERROR: query is read-only. The first keyword must be SELECT, WITH, or DECLARE (got "
            + repr(first) + "). For DML/DDL use: mssql_tool.py execute <sql> --allow-write"
        )
    limit = args.limit
    conn, ep, _, db = open_conn(pymssql, args, fields)
    cur = conn.cursor()
    try:
        run_sql(cur, sql)
        cols = [d[0] for d in cur.description] if cur.description else []
        rows = scrub(fetch_all_capped(cur, limit))
    finally:
        conn.close()
    if limit and len(rows) >= limit:
        sys.stderr.write("(row cap reached: %d - raise with --limit or use 0 for none)" % limit)
    if args.csv:
        write_csv(args.csv, cols, rows)
        return
    if args.json:
        emit_json({"columns": cols, "rows": [dict(zip(cols, r)) for r in rows],
                   "row_count": len(rows)})
        return
    print_table(cols, rows)
    sys.stderr.write("(%d rows, database %s, endpoint %s)" % (len(rows), db, ep))


def cmd_execute(args):
    if not args.allow_write:
        die("ERROR: execute changes data. Re-run with --allow-write (only when the user asked for a change).")
    pymssql = ensure_pymssql()
    fields = get_cs_fields(args)
    conn, ep, _, db = open_conn(pymssql, args, fields)
    cur = conn.cursor()
    try:
        try:
            cur.execute(args.sql)
        except Exception as exc:
            try:
                conn.rollback()
            except Exception:
                pass
            die("ERROR: statement failed (rolled back): " + str(exc).strip())
        conn.commit()
        affected = cur.rowcount
    finally:
        conn.close()
    if args.json:
        emit_json({"ok": True, "rows_affected": affected, "database": db, "endpoint": ep})
        return
    print("committed on " + db + " (" + ep + "): " + str(affected) + " row(s) affected")


def main():
    ap = argparse.ArgumentParser(
        prog="mssql_tool",
        description="Query the stack's SQL Server from the sandbox (GA_DB_CONNECTION_STRING).")
    sub = ap.add_subparsers(dest="cmd", required=True)

    def add_common(sp):
        sp.add_argument("--endpoint", help="HOST:PORT (overrides " + EP_ENV + " / default " + DEFAULT_ENDPOINT + ")")
        sp.add_argument("--db", help="database (overrides Initial Catalog)")
        sp.add_argument("--connection-string", help="override " + CS_ENV + " (prefer the env var)")
        sp.add_argument("--json", action="store_true", help="machine-readable output")

    sp = sub.add_parser("probe", help="connect + summary")
    add_common(sp); sp.set_defaults(fn=cmd_probe)
    sp = sub.add_parser("databases", help="list databases")
    add_common(sp); sp.set_defaults(fn=cmd_databases)
    sp = sub.add_parser("tables", help="list tables with row counts")
    add_common(sp); sp.set_defaults(fn=cmd_tables)
    sp = sub.add_parser("schema", help="column schema of a table")
    add_common(sp); sp.add_argument("table"); sp.set_defaults(fn=cmd_schema)
    sp = sub.add_parser("sample", help="top N rows of a table")
    add_common(sp); sp.add_argument("table")
    sp.add_argument("-n", default=SAMPLE_DEFAULT, help="rows (default 10)")
    sp.add_argument("--csv", help="write rows to this CWD file as CSV")
    sp.set_defaults(fn=cmd_sample)
    sp = sub.add_parser("query", help="read-only SQL (SELECT/WITH/DECLARE)")
    add_common(sp); sp.add_argument("sql")
    sp.add_argument("--limit", type=int, default=DEFAULT_LIMIT, help="max rows (default 500, 0 = none)")
    sp.add_argument("--csv", help="write rows to this CWD file as CSV")
    sp.set_defaults(fn=cmd_query)
    sp = sub.add_parser("execute", help="DML/DDL, requires --allow-write")
    add_common(sp); sp.add_argument("sql")
    sp.add_argument("--allow-write", action="store_true", help="required for writes")
    sp.set_defaults(fn=cmd_execute)

    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
