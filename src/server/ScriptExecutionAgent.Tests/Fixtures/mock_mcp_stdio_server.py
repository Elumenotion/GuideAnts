#!/usr/bin/env python3
"""Minimal MCP stdio mock server for ScriptExecutionAgent tests."""
import json
import os
import sys


def write_message(message):
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


def handle_request(request):
    request_id = request.get("id")
    method = request.get("method")

    if method == "initialize":
        write_message(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "mock-mcp", "version": "1.0.0"},
                },
            }
        )
        return

    if method == "notifications/initialized":
        return

    if method == "tools/list":
        write_message(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "tools": [
                        {
                            "name": "env_probe",
                            "description": "Reports whether agent tokens leaked into MCP child env.",
                            "inputSchema": {"type": "object", "properties": {}},
                        },
                        {
                            "name": "env_dump",
                            "description": "Returns sorted process environment keys as JSON.",
                            "inputSchema": {"type": "object", "properties": {}},
                        }
                    ]
                },
            }
        )
        return

    if method == "tools/call":
        params = request.get("params") or {}
        tool_name = params.get("name")
        if tool_name == "env_probe":
            leaked = bool(os.environ.get("SCRIPT_EXECUTION_AGENT_TOKEN"))
            text = "leaked" if leaked else "missing"
        elif tool_name == "env_dump":
            text = json.dumps(dict(sorted(os.environ.items())))
        else:
            text = f"mock-result:{tool_name}"
        write_message(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "result": {
                    "content": [
                        {
                            "type": "text",
                            "text": text,
                        }
                    ]
                },
            }
        )
        return

    if request_id is not None:
        write_message(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "error": {"code": -32601, "message": f"Method not found: {method}"},
            }
        )


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except json.JSONDecodeError:
            continue
        handle_request(request)


if __name__ == "__main__":
    main()
