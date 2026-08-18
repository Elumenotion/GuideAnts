"""Recover from llama.cpp 'option not recognized in preset' by mutating canonical INI."""

from __future__ import annotations

import re
from typing import Callable

from guideants_hf.router_mmproj import (
    MMPROJ_AUTO_DISABLED_VALUE,
    MMPROJ_AUTO_KEY,
    llama_option_token,
)

UNRECOGNIZED_OPTION_RE = re.compile(
    r"option '([^']+)' not recognized in preset '([^']+)'",
    re.IGNORECASE,
)


def parse_unrecognized_option_error(log_text: str) -> tuple[str, str] | None:
    """Return (option_token, alias) from llama.cpp's latest fatal preset parse error."""
    matches = list(UNRECOGNIZED_OPTION_RE.finditer(log_text))
    if not matches:
        return None
    match = matches[-1]
    return match.group(1).strip().lower(), match.group(2).strip()


def drop_unrecognized_option_from_entries(
    entries: dict,
    *,
    option_token: str,
    alias: str,
) -> bool:
    """Remove extras whose hyphen-stripped name equals option_token.

    mmproj-disable spellings are rewritten to mmproj-auto=false so vision-off
    intent survives. Returns True when the alias extras changed.
    """
    section = entries.get(alias)
    if section is None:
        return False
    token = llama_option_token(option_token)
    extras = dict(section.extras)
    changed = False
    mmproj_disable = token in {"nommproj", "nommprojauto"}
    for key in list(extras.keys()):
        if llama_option_token(key) != token:
            continue
        del extras[key]
        changed = True
    if mmproj_disable:
        extras[MMPROJ_AUTO_KEY] = MMPROJ_AUTO_DISABLED_VALUE
        changed = True
    if not changed:
        return False
    section.extras = extras
    return True


def sanitize_canonical_from_log(
    canonical_path: str,
    log_path: str,
    *,
    parse_router_ini: Callable[[str], dict],
    serialize_router_ini: Callable[[dict], str],
) -> bool:
    """Drop the unrecognized option named in log_path from canonical_path. True if rewritten."""
    try:
        with open(log_path, "r", encoding="utf-8", errors="replace") as handle:
            log_text = handle.read()
    except OSError:
        return False
    parsed = parse_unrecognized_option_error(log_text)
    if parsed is None:
        return False
    option_token, alias = parsed
    try:
        with open(canonical_path, "r", encoding="utf-8") as handle:
            canonical = handle.read()
    except OSError:
        return False
    entries = parse_router_ini(canonical)
    if not drop_unrecognized_option_from_entries(
        entries, option_token=option_token, alias=alias
    ):
        return False
    payload = serialize_router_ini(entries)
    with open(canonical_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(payload)
        handle.flush()
    return True
