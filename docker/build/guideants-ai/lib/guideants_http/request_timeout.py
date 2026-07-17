from __future__ import annotations

import os
from typing import Any, Mapping

REQUEST_TIMEOUT_HEADER = "x-ga-request-timeout-seconds"
DEFAULT_MAX_REQUEST_TIMEOUT_SECONDS = 86_400


def parse_positive_int(value: Any, default: int) -> int:
    try:
        parsed = int(str(value).strip())
    except (TypeError, ValueError, AttributeError):
        return max(1, default)
    return parsed if parsed > 0 else max(1, default)


def absolute_max_request_timeout_seconds() -> int:
    configured = os.getenv("GA_MAX_REQUEST_TIMEOUT_SECONDS")
    if configured is None or not str(configured).strip():
        return DEFAULT_MAX_REQUEST_TIMEOUT_SECONDS
    return parse_positive_int(configured, DEFAULT_MAX_REQUEST_TIMEOUT_SECONDS)


def clamp_request_timeout_seconds(timeout_seconds: int, *, max_timeout_seconds: int | None = None) -> int:
    ceiling = (
        parse_positive_int(max_timeout_seconds, absolute_max_request_timeout_seconds())
        if max_timeout_seconds is not None
        else absolute_max_request_timeout_seconds()
    )
    return max(1, min(parse_positive_int(timeout_seconds, 1), ceiling))


def _header_value(headers: Mapping[str, Any], header_name: str) -> Any | None:
    raw = headers.get(header_name)
    if raw is not None:
        return raw
    lowered = header_name.lower()
    for key, value in headers.items():
        if str(key).lower() == lowered:
            return value
    return None


def resolve_request_timeout_seconds(
    headers: Mapping[str, Any] | None,
    env_default: int,
    *,
    max_timeout_seconds: int | None = None,
) -> int:
    """Use caller timeout header when present; otherwise the service env default."""
    fallback = clamp_request_timeout_seconds(parse_positive_int(env_default, 300), max_timeout_seconds=max_timeout_seconds)
    if not headers:
        return fallback

    raw = _header_value(headers, REQUEST_TIMEOUT_HEADER)
    if raw is None or not str(raw).strip():
        return fallback

    return clamp_request_timeout_seconds(parse_positive_int(raw, fallback), max_timeout_seconds=max_timeout_seconds)
