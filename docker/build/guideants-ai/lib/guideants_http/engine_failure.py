"""Classify engine RPC exceptions for one-shot recycle.

This helper is only called from an except-block. HTTP 200 never reaches it.

Recycle: TimeoutError, URLError/ConnectionError, HTTP 5xx, HTTP 408, empty-WAV
RuntimeError, or any other exception that escaped the RPC without an HTTP 4xx.

Do not recycle: HTTP 4xx other than 408. HTTP 200 with empty ``text`` is not an
exception — the caller returns success and must not call this helper.
"""

from __future__ import annotations

import re
import urllib.error

_HTTP_STATUS_IN_ERROR = re.compile(r"\bhttp (\d{3})\b", re.IGNORECASE)


def engine_rpc_http_status(exc: BaseException) -> int | None:
    match = _HTTP_STATUS_IN_ERROR.search(str(exc))
    if match is None:
        return None
    return int(match.group(1))


def engine_rpc_exception_is_engine_failure(exc: BaseException) -> bool:
    """True when inference raised because the engine failed to complete the call.

    Recycle on: TimeoutError, URLError/transport, HTTP 5xx, HTTP 408, and any other
    exception that escaped the RPC (including empty WAV payload RuntimeError).

    Do not recycle on HTTP 4xx other than 408 — those are request/config faults.
    HTTP 200 with empty text is success: this function is not called.
    """
    if isinstance(exc, (TimeoutError, ConnectionError, urllib.error.URLError)):
        return True
    code = engine_rpc_http_status(exc)
    if code is None:
        return True
    if 400 <= code < 500 and code != 408:
        return False
    return True


def wrapper_http_status_for_engine_exception(exc: BaseException) -> int:
    """Status the wrapper returns to GuideAntsApi.

    Pass through engine HTTP 4xx (except 408) so the API does not recycle on a
    request/config fault. Every other engine exception is HTTP 500.
    """
    code = engine_rpc_http_status(exc)
    if code is not None and 400 <= code < 500 and code != 408:
        return code
    return 500
