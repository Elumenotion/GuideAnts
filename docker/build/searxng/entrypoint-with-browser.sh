#!/usr/bin/env bash
set -euo pipefail

ensure_json_format() {
  python - <<'PY'
import os
import re
from pathlib import Path

formats_block = re.compile(r"(^  formats:\n)(?P<body>(?:^    - .*\n)+)", re.MULTILINE)


def enable_json_format(path: Path) -> None:
    if not path.exists():
        return

    text = path.read_text(encoding="utf-8")
    match = formats_block.search(text)
    if not match:
        return

    body = match.group("body")
    if re.search(r"^    - json\s*$", body, re.MULTILINE):
        return

    updated = text[:match.start("body")] + body + "    - json\n" + text[match.end("body"):]
    path.write_text(updated, encoding="utf-8")
    print(f"Enabled JSON search format in {path}")


paths = [
    Path("/usr/local/searxng/searx/settings.yml"),
    Path(os.getenv("__SEARXNG_SETTINGS_PATH", "/etc/searxng/settings.yml")),
]

seen = set()
for path in paths:
    normalized = str(path)
    if normalized in seen:
        continue
    seen.add(normalized)
    enable_json_format(path)
PY
}

ensure_json_format

nginx -g 'daemon off;' &
nginx_pid=$!

python /opt/browser-render/browser_api.py &
browser_pid=$!

/usr/local/searxng/entrypoint.sh &
searx_pid=$!

cleanup() {
  kill -TERM "$nginx_pid" "$browser_pid" "$searx_pid" 2>/dev/null || true
}

trap cleanup INT TERM

set +e
wait -n "$nginx_pid" "$browser_pid" "$searx_pid"
exit_code=$?
set -e

cleanup
wait "$nginx_pid" || true
wait "$browser_pid" || true
wait "$searx_pid" || true

exit "$exit_code"
