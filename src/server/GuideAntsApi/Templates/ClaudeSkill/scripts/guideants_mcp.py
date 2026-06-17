#!/usr/bin/env python3
"""GuideAnts published-guide MCP client (standard library only, cross-platform).

A single entry point for discovering, invoking, and collecting results from a
GuideAnts published guide's MCP endpoint. No bash, no curl - it behaves
identically on Windows, macOS, and Linux and needs nothing beyond Python 3.8+.

Commands:
    doctor            Validate .env and endpoint connectivity
    list-tools        List assistant tools (tools/list)
    invoke            Call an assistant, save images + deliverables
    get-conversation  Fetch conversation history (conversation_get)
    recover           Download files created in a conversation's latest turn
    download          Download a published artifact URL

Every command prints exactly one JSON object to stdout. Human-readable
diagnostics go to stderr. Errors exit non-zero with {"error": "<code>", ...}.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

# stdout/stderr must be UTF-8 so emoji and non-ASCII markdown never crash on
# Windows consoles (cp1252 by default).
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

SKILL_ROOT = Path(__file__).resolve().parent.parent
ENV_FILE = SKILL_ROOT / ".env"
API_KEY_PLACEHOLDER = "gak_REPLACE_ME"

MIME_EXTENSIONS = {
    "image/png": "png",
    "image/jpeg": "jpg",
    "image/webp": "webp",
    "image/gif": "gif",
}

ARTIFACT_URL_PATTERNS = (
    re.compile(r"https?://[^)\s\"']+/api/published/[^)\s\"']+"),
    re.compile(r"/api/published/[^)\s\"']+"),
)


def log(message: str) -> None:
    print(message, file=sys.stderr)


def emit(payload: dict[str, Any]) -> None:
    print(json.dumps(payload, ensure_ascii=False, indent=2))


def fail(code: str, message: str, **extra: Any) -> "None":
    payload: dict[str, Any] = {"error": code, "message": message}
    payload.update(extra)
    print(json.dumps(payload, ensure_ascii=False), flush=True)
    raise SystemExit(1)


def load_env() -> dict[str, str]:
    env: dict[str, str] = {}
    if ENV_FILE.exists():
        for line in ENV_FILE.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            env[key.strip()] = value.strip()

    # A real process environment variable overrides the .env file.
    for key in (
        "GUIDEANTS_API_BASE",
        "GUIDEANTS_PUB_ID",
        "GUIDEANTS_API_KEY",
        "GUIDEANTS_SAVE_DIR",
    ):
        value = os.environ.get(key)
        if value:
            env[key] = value
    return env


def require_config(env: dict[str, str]) -> tuple[str, str, str]:
    base = env.get("GUIDEANTS_API_BASE", "").rstrip("/")
    pub = env.get("GUIDEANTS_PUB_ID", "")
    key = env.get("GUIDEANTS_API_KEY", "")

    missing = [
        name
        for name, value in (
            ("GUIDEANTS_API_BASE", base),
            ("GUIDEANTS_PUB_ID", pub),
            ("GUIDEANTS_API_KEY", key),
        )
        if not value
    ]
    if missing:
        fail(
            "missing_config",
            "Missing required .env values: " + ", ".join(missing),
            missing=missing,
        )
    if key == API_KEY_PLACEHOLDER:
        fail(
            "api_key_placeholder",
            "GUIDEANTS_API_KEY is still the placeholder. Set your gak_ key in .env.",
        )
    return base, pub, key


def resolve_save_dir(args: argparse.Namespace, env: dict[str, str]) -> Path:
    explicit = getattr(args, "save_dir", "") or env.get("GUIDEANTS_SAVE_DIR", "")
    return Path(explicit).expanduser() if explicit else Path.cwd()


def unwrap_sse(raw: str) -> str:
    """Streamable HTTP transports may return Server-Sent Events; take the last
    ``data:`` payload, which carries the final JSON-RPC message."""
    if "data:" not in raw:
        return raw
    data_lines = [line[6:] for line in raw.splitlines() if line.startswith("data: ")]
    return data_lines[-1] if data_lines else raw


def _request(
    url: str,
    *,
    method: str = "GET",
    headers: dict[str, str] | None = None,
    data: bytes | None = None,
    timeout: int = 300,
) -> tuple[int, bytes]:
    """Perform an HTTP request. Returns (status, body). Raises urllib.error.URLError
    only when no HTTP response is received (connection failure)."""
    request = urllib.request.Request(url, method=method, data=data, headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()


def mcp_call(
    base: str,
    pub: str,
    key: str,
    method: str,
    params: dict[str, Any],
    request_id: int = 1,
    timeout: int = 300,
) -> dict[str, Any]:
    url = f"{base}/published/mcp?pubId={pub}"
    body = json.dumps(
        {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
    ).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "x-guideants-apikey": key,
    }

    try:
        status, raw = _request(
            url, method="POST", headers=headers, data=body, timeout=timeout
        )
    except urllib.error.URLError as exc:
        fail("network_error", f"Could not reach MCP endpoint: {exc.reason}")

    text = raw.decode("utf-8", errors="replace")
    if status == 401:
        fail("unauthorized", "MCP rejected the API key (401). Check GUIDEANTS_API_KEY in .env.")
    if status >= 500:
        fail("mcp_error", f"MCP endpoint returned HTTP {status}.", status=status)

    try:
        rpc = json.loads(unwrap_sse(text))
    except json.JSONDecodeError:
        fail("mcp_error", "MCP endpoint returned a non-JSON response.", status=status)

    if rpc.get("error"):
        detail = rpc["error"]
        fail("mcp_error", detail.get("message", "MCP returned an error."), detail=detail)
    return rpc.get("result") or {}


def fetch_guide_info(base: str, pub: str) -> dict[str, Any]:
    """Anonymous metadata lookup used to resolve projectId/notebookId for file URLs."""
    url = f"{base}/published/guides/{pub}"
    try:
        status, raw = _request(url, method="GET", timeout=30)
    except urllib.error.URLError:
        return {}
    if status != 200:
        return {}
    try:
        return json.loads(raw.decode("utf-8", errors="replace"))
    except json.JSONDecodeError:
        return {}


def parse_inner(content: list[dict[str, Any]]) -> dict[str, Any]:
    """The first text block of an MCP tool result is a JSON string the guide returns."""
    for block in content:
        if block.get("type") == "text" and block.get("text"):
            try:
                return json.loads(block["text"])
            except json.JSONDecodeError:
                return {}
    return {}


def save_inline_images(content: list[dict[str, Any]]) -> list[str]:
    """Persist base64 image blocks under the skill's cache for the agent to Read
    and render. These are previews, not the user's deliverable."""
    images = [b for b in content if b.get("type") == "image" and b.get("data")]
    if not images:
        return []

    turn_dir = SKILL_ROOT / "artifacts" / f"turn-{int(time.time())}"
    turn_dir.mkdir(parents=True, exist_ok=True)

    saved: list[str] = []
    for index, block in enumerate(images):
        mime = block.get("mimeType") or "image/png"
        ext = MIME_EXTENSIONS.get(mime, "png")
        out_path = turn_dir / f"image-{index}.{ext}"
        out_path.write_bytes(base64.b64decode(block["data"]))
        saved.append(str(out_path.resolve()))
    return saved


def latest_new_files(conversation: dict[str, Any]) -> list[str]:
    """Collect NewFiles reported by tool messages in the most recent turn."""
    messages = conversation.get("messages") or []
    if not messages:
        return []

    max_turn = max((m.get("turnIndex", 0) for m in messages), default=0)
    files: list[str] = []
    for message in messages:
        if message.get("turnIndex") != max_turn or message.get("role") != "tool":
            continue
        try:
            payload = json.loads(message.get("content") or "{}")
        except json.JSONDecodeError:
            continue
        for name in payload.get("NewFiles") or []:
            if name and name not in files:
                files.append(name)
    return files


def build_file_url(
    base: str, pub: str, project_id: str, notebook_id: str, conversation_id: str, name: str
) -> str:
    path_q = urllib.parse.quote(name, safe="")
    return (
        f"{base}/published/projects/{project_id}/notebooks/{notebook_id}"
        f"/conversations/{conversation_id}/files/content?path={path_q}&pubId={pub}"
    )


def download_to(url: str, out_path: Path) -> dict[str, Any] | None:
    try:
        status, raw = _request(url, method="GET", timeout=120)
    except urllib.error.URLError:
        return None
    if status != 200:
        return None
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(raw)
    return {"localPath": str(out_path.resolve()), "url": url, "bytes": len(raw)}


def download_new_files(
    base: str,
    pub: str,
    project_id: str,
    notebook_id: str,
    conversation_id: str,
    names: list[str],
    save_dir: Path,
) -> list[dict[str, Any]]:
    deliverables: list[dict[str, Any]] = []
    for name in names:
        url = build_file_url(base, pub, project_id, notebook_id, conversation_id, name)
        result = download_to(url, save_dir / Path(name).name)
        if result:
            result["name"] = name
            deliverables.append(result)
    return deliverables


# --------------------------------------------------------------------------- #
# Commands
# --------------------------------------------------------------------------- #


def cmd_list_tools(args: argparse.Namespace, env: dict[str, str]) -> int:
    base, pub, key = require_config(env)
    result = mcp_call(base, pub, key, "tools/list", {}, request_id=1, timeout=60)
    tools = [
        {"name": tool.get("name"), "description": tool.get("description", "")}
        for tool in result.get("tools", [])
    ]
    emit({"tools": tools})
    return 0


def cmd_invoke(args: argparse.Namespace, env: dict[str, str]) -> int:
    base, pub, key = require_config(env)

    arguments: dict[str, Any] = {"instructions": args.instructions}
    if args.conversation_id:
        arguments["conversationId"] = args.conversation_id
    if args.title:
        arguments["title"] = args.title

    result = mcp_call(
        base,
        pub,
        key,
        "tools/call",
        {"name": args.tool, "arguments": arguments},
        request_id=2,
        timeout=args.timeout,
    )
    content = result.get("content") or []
    inner = parse_inner(content)

    conversation_id = inner.get("conversationId") or args.conversation_id or ""
    response_md = inner.get("response") or ""
    display_images = save_inline_images(content)

    artifact_urls: list[str] = []
    for pattern in ARTIFACT_URL_PATTERNS:
        artifact_urls.extend(pattern.findall(response_md))
    artifact_urls = sorted(set(artifact_urls))

    deliverables: list[dict[str, Any]] = []
    if conversation_id and not args.no_download:
        info = fetch_guide_info(base, pub)
        project_id = info.get("projectId")
        notebook_id = info.get("notebookId")
        if project_id and notebook_id:
            conv_result = mcp_call(
                base,
                pub,
                key,
                "tools/call",
                {"name": "conversation_get", "arguments": {"conversationId": conversation_id}},
                request_id=3,
                timeout=60,
            )
            conversation = parse_inner(conv_result.get("content") or [])
            names = latest_new_files(conversation)
            if names:
                deliverables = download_new_files(
                    base,
                    pub,
                    project_id,
                    notebook_id,
                    conversation_id,
                    names,
                    resolve_save_dir(args, env),
                )

    emit(
        {
            "conversationId": conversation_id,
            "assistantName": inner.get("assistantName") or "",
            "responseMarkdown": response_md,
            "displayImages": display_images,
            "deliverables": deliverables,
            "artifactUrls": artifact_urls,
        }
    )
    return 0


def cmd_get_conversation(args: argparse.Namespace, env: dict[str, str]) -> int:
    base, pub, key = require_config(env)
    result = mcp_call(
        base,
        pub,
        key,
        "tools/call",
        {"name": "conversation_get", "arguments": {"conversationId": args.conversation_id}},
        request_id=3,
        timeout=60,
    )
    emit(parse_inner(result.get("content") or []))
    return 0


def cmd_recover(args: argparse.Namespace, env: dict[str, str]) -> int:
    base, pub, key = require_config(env)

    info = fetch_guide_info(base, pub)
    project_id = info.get("projectId")
    notebook_id = info.get("notebookId")
    if not project_id or not notebook_id:
        fail("guide_info_unavailable", "Could not resolve project/notebook for this guide.")

    result = mcp_call(
        base,
        pub,
        key,
        "tools/call",
        {"name": "conversation_get", "arguments": {"conversationId": args.conversation_id}},
        request_id=3,
        timeout=60,
    )
    conversation = parse_inner(result.get("content") or [])
    names = latest_new_files(conversation)
    deliverables = download_new_files(
        base,
        pub,
        project_id,
        notebook_id,
        args.conversation_id,
        names,
        resolve_save_dir(args, env),
    )
    emit({"conversationId": args.conversation_id, "deliverables": deliverables})
    return 0


def cmd_download(args: argparse.Namespace, env: dict[str, str]) -> int:
    base, pub, key = require_config(env)

    url = args.url
    if url.startswith("/api/"):
        root = base[:-4] if base.endswith("/api") else base
        url = root + url
    elif url.startswith("/published/"):
        url = base + url

    if args.output:
        out_path = Path(args.output).expanduser()
    else:
        parsed = urllib.parse.urlparse(url)
        fname = os.path.basename(parsed.path)
        if not fname or fname == "content":
            query = urllib.parse.parse_qs(parsed.query)
            fname = os.path.basename(query.get("path", [""])[0]) or f"artifact-{int(time.time())}"
        out_path = resolve_save_dir(args, env) / fname

    result = download_to(url, out_path)
    if not result:
        fail("download_failed", f"Download failed for {url}", url=url)
    emit(result)
    return 0


def cmd_doctor(args: argparse.Namespace, env: dict[str, str]) -> int:
    base = env.get("GUIDEANTS_API_BASE", "").rstrip("/")
    pub = env.get("GUIDEANTS_PUB_ID", "")
    key = env.get("GUIDEANTS_API_KEY", "")

    checks = [
        {"check": "env_api_base", "ok": bool(base), "value": base},
        {"check": "env_pub_id", "ok": bool(pub)},
        {"check": "env_api_key", "ok": bool(key) and key != API_KEY_PLACEHOLDER},
    ]

    reachable = False
    tool_count = 0
    if all(c["ok"] for c in checks):
        ok, tool_count = _try_tools_list(base, pub, key)
        reachable = ok
    checks.append({"check": "mcp_reachable", "ok": reachable, "toolCount": tool_count})

    overall = all(c["ok"] for c in checks)
    emit({"ok": overall, "checks": checks})
    return 0 if overall else 1


def _try_tools_list(base: str, pub: str, key: str) -> tuple[bool, int]:
    url = f"{base}/published/mcp?pubId={pub}"
    body = json.dumps(
        {"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}
    ).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "x-guideants-apikey": key,
    }
    try:
        status, raw = _request(url, method="POST", headers=headers, data=body, timeout=30)
    except urllib.error.URLError:
        return False, 0
    if status != 200:
        return False, 0
    try:
        rpc = json.loads(unwrap_sse(raw.decode("utf-8", errors="replace")))
    except json.JSONDecodeError:
        return False, 0
    if rpc.get("error"):
        return False, 0
    return True, len((rpc.get("result") or {}).get("tools", []))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="guideants_mcp.py",
        description="GuideAnts published-guide MCP client (standard library only).",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    list_parser = sub.add_parser("list-tools", help="List available assistant tools.")
    list_parser.set_defaults(func=cmd_list_tools)

    invoke_parser = sub.add_parser("invoke", help="Invoke an assistant tool.")
    invoke_parser.add_argument("tool", help="Tool name, e.g. the primary guide tool.")
    invoke_parser.add_argument("instructions", help="What you want the assistant to do.")
    invoke_parser.add_argument("--conversation-id", default="", help="Continue an existing thread.")
    invoke_parser.add_argument("--title", default="", help="Title for a new conversation.")
    invoke_parser.add_argument("--timeout", type=int, default=300, help="Seconds to wait (default 300).")
    invoke_parser.add_argument("--save-dir", default="", help="Where to save deliverables (default: cwd).")
    invoke_parser.add_argument(
        "--no-download", action="store_true", help="Skip downloading files the guide produced."
    )
    invoke_parser.set_defaults(func=cmd_invoke)

    get_parser = sub.add_parser("get-conversation", help="Fetch conversation history.")
    get_parser.add_argument("conversation_id")
    get_parser.set_defaults(func=cmd_get_conversation)

    recover_parser = sub.add_parser(
        "recover", help="Download files created in a conversation's latest turn."
    )
    recover_parser.add_argument("conversation_id")
    recover_parser.add_argument("--save-dir", default="", help="Where to save files (default: cwd).")
    recover_parser.set_defaults(func=cmd_recover)

    download_parser = sub.add_parser("download", help="Download a published artifact URL.")
    download_parser.add_argument("url")
    download_parser.add_argument("--output", default="", help="Explicit output file path.")
    download_parser.add_argument("--save-dir", default="", help="Output directory (default: cwd).")
    download_parser.set_defaults(func=cmd_download)

    doctor_parser = sub.add_parser("doctor", help="Check configuration and connectivity.")
    doctor_parser.set_defaults(func=cmd_doctor)

    return parser


def main(argv: list[str]) -> int:
    args = build_parser().parse_args(argv)
    env = load_env()
    return args.func(args, env)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
