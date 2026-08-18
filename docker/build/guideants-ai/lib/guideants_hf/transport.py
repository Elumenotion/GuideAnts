import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Callable


def _parse_positive_int(value: str | None, default: int) -> int:
    if value is None:
        return default
    try:
        parsed = int(value)
        if parsed > 0:
            return parsed
    except ValueError:
        pass
    return default


HF_TIMEOUT_SECONDS = _parse_positive_int(
    os.getenv("GA_HF_DOWNLOAD_TIMEOUT_SECONDS") or os.getenv("GA_LLAMA_ADMIN_HF_TIMEOUT_SECONDS"),
    1800,
)
HTTP_USER_AGENT = "GuideAnts-HF/1.0"

_CONTENT_RANGE_TOTAL_RE = re.compile(r"^bytes \d+-\d+/(\d+)$")


class IncompleteDownloadError(RuntimeError):
  """Stream ended before the declared or expected byte count; partial .tmp is preserved."""

  def __init__(self, message: str, *, received: int, expected: int) -> None:
    super().__init__(message)
    self.received = received
    self.expected = expected


class RangeNotSatisfiable(Exception):
    pass


def build_regex_from_include_pattern(pattern: str) -> re.Pattern[str]:
    parts = pattern.split("*")
    escaped = [re.escape(part) for part in parts]
    return re.compile("^" + ".*".join(escaped) + "$")


def _normalize_revision(revision: str | None) -> str:
    candidate = (revision or "main").strip()
    return candidate or "main"


def _parse_int_header(value: str | None) -> int | None:
    if value is None:
        return None
    candidate = value.strip()
    if not candidate:
        return None
    try:
        parsed = int(candidate)
    except ValueError:
        return None
    return parsed if parsed >= 0 else None


def _parse_content_range_total(value: str | None) -> int | None:
    if not value:
        return None
    match = _CONTENT_RANGE_TOTAL_RE.match(value.strip())
    if not match:
        return None
    return int(match.group(1))


def _assert_download_complete(
    *,
    temp_path: str,
    received_bytes: int,
    declared_length: int | None,
    expected_size: int | None,
    context: str,
) -> None:
    if declared_length is not None and received_bytes != declared_length:
        raise IncompleteDownloadError(
            f"Download truncated ({context}): received {received_bytes} bytes, "
            f"Content-Length declared {declared_length}.",
            received=received_bytes,
            expected=declared_length,
        )
    if expected_size is not None and received_bytes != expected_size:
        raise IncompleteDownloadError(
            f"Download truncated ({context}): received {received_bytes} bytes, expected {expected_size}.",
            received=received_bytes,
            expected=expected_size,
        )


def list_hf_repository_files(
    repository: str,
    token: str | None,
    revision: str | None = "main",
) -> list[dict[str, Any]]:
    repo_normalized = repository.strip().strip("/")
    if not repo_normalized:
        raise ValueError("Repository is required.")

    rev = _normalize_revision(revision)
    url = f"https://huggingface.co/api/models/{repo_normalized}/tree/{urllib.parse.quote(rev, safe='')}?recursive=true"
    request = urllib.request.Request(
        url,
        method="GET",
        headers={
            "User-Agent": HTTP_USER_AGENT,
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=HF_TIMEOUT_SECONDS) as response:
            body = response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Failed to list repository files ({exc.code}): {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Failed to list repository files: {exc.reason}") from exc

    try:
        parsed = json.loads(body)
    except json.JSONDecodeError as exc:
        raise RuntimeError("Unexpected Hugging Face tree response (invalid JSON).") from exc

    if not isinstance(parsed, list):
        raise RuntimeError("Unexpected Hugging Face tree response (expected array).")

    out: list[dict[str, Any]] = []
    for item in parsed:
        if not isinstance(item, dict):
            continue
        file_type = item.get("type")
        path = item.get("path")
        size = item.get("size")
        if not isinstance(file_type, str) or not isinstance(path, str):
            continue
        out.append({"type": file_type, "path": path, "size": size if isinstance(size, int) else None})
    return out


def download_hf_file(
    repository: str,
    relative_path: str,
    destination_path: str,
    token: str | None,
    revision: str | None = "main",
    progress_callback: Callable[[int], None] | None = None,
    expected_size: int | None = None,
) -> None:
    repo_path = "/".join(part.strip() for part in repository.split("/") if part.strip())
    encoded_segments = [urllib.parse.quote(segment) for segment in relative_path.split("/") if segment]
    encoded_path = "/".join(encoded_segments)
    rev = _normalize_revision(revision)
    url = f"https://huggingface.co/{repo_path}/resolve/{urllib.parse.quote(rev, safe='')}/{encoded_path}"

    temp_path = destination_path + ".tmp"

    existing_bytes = 0
    if os.path.exists(temp_path):
        existing_bytes = os.path.getsize(temp_path)

    try:
        if existing_bytes > 0:
            try:
                _download_hf_range(
                    url,
                    token,
                    temp_path,
                    existing_bytes,
                    progress_callback,
                    expected_size=expected_size,
                )
            except RangeNotSatisfiable:
                os.remove(temp_path)
                _download_hf_full(
                    url,
                    token,
                    temp_path,
                    progress_callback,
                    expected_size=expected_size,
                )
        else:
            _download_hf_full(
                url,
                token,
                temp_path,
                progress_callback,
                expected_size=expected_size,
            )
    except IncompleteDownloadError:
        raise
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        if os.path.exists(temp_path):
            os.remove(temp_path)
        raise RuntimeError(f"Download failed ({exc.code}): {detail}") from exc

    final_bytes = os.path.getsize(temp_path)
    _assert_download_complete(
        temp_path=temp_path,
        received_bytes=final_bytes,
        declared_length=None,
        expected_size=expected_size,
        context="final verification",
    )

    if os.path.exists(destination_path):
        os.remove(destination_path)
    os.replace(temp_path, destination_path)


def _download_hf_full(
    url: str,
    token: str | None,
    temp_path: str,
    progress_callback: Callable[[int], None] | None,
    expected_size: int | None = None,
) -> None:
    request = urllib.request.Request(
        url,
        method="GET",
        headers={
            "User-Agent": HTTP_USER_AGENT,
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=HF_TIMEOUT_SECONDS) as response:
            declared_length = _parse_int_header(response.getheader("Content-Length"))
            with open(temp_path, "wb") as target:
                total = 0
                while True:
                    chunk = response.read(81920)
                    if not chunk:
                        break
                    target.write(chunk)
                    total += len(chunk)
                    if progress_callback:
                        progress_callback(total)
                target.flush()
                os.fsync(target.fileno())
            _assert_download_complete(
                temp_path=temp_path,
                received_bytes=total,
                declared_length=declared_length,
                expected_size=expected_size,
                context="full download",
            )
    except IncompleteDownloadError:
        raise
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        if os.path.exists(temp_path):
            os.remove(temp_path)
        raise RuntimeError(f"Download failed ({exc.code}): {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Download connection lost: {exc.reason}") from exc


def _download_hf_range(
    url: str,
    token: str | None,
    temp_path: str,
    existing_bytes: int,
    progress_callback: Callable[[int], None] | None,
    expected_size: int | None = None,
) -> None:
    headers: dict[str, str] = {
        "User-Agent": HTTP_USER_AGENT,
        "Range": f"bytes={existing_bytes}-",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    request = urllib.request.Request(url, method="GET", headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=HF_TIMEOUT_SECONDS) as response:
            declared_length = _parse_int_header(response.getheader("Content-Length"))
            content_range_total = _parse_content_range_total(response.getheader("Content-Range"))
            if response.status == 200:
                mode = "wb"
                offset = 0
            elif response.status == 206:
                mode = "ab"
                offset = existing_bytes
            else:
                mode = "wb"
                offset = 0

            with open(temp_path, mode) as target:
                total = offset
                while True:
                    chunk = response.read(81920)
                    if not chunk:
                        break
                    target.write(chunk)
                    total += len(chunk)
                    if progress_callback:
                        progress_callback(total)
                target.flush()
                os.fsync(target.fileno())

            expected_total = content_range_total or expected_size
            if declared_length is not None and response.status == 206:
                expected_received = offset + declared_length
                _assert_download_complete(
                    temp_path=temp_path,
                    received_bytes=total,
                    declared_length=expected_received,
                    expected_size=None,
                    context="range download",
                )
            _assert_download_complete(
                temp_path=temp_path,
                received_bytes=total,
                declared_length=None,
                expected_size=expected_total,
                context="range download",
            )
    except IncompleteDownloadError:
        raise
    except urllib.error.HTTPError as exc:
        if exc.code == 416:
            raise RangeNotSatisfiable() from exc
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Download failed ({exc.code}): {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Download connection lost: {exc.reason}") from exc
