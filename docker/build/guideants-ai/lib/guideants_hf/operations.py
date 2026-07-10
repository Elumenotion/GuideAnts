from datetime import datetime, timezone
from typing import Any


IN_FLIGHT_OPERATION_STATUSES = frozenset(
    {
        "queued",
        "running",
        "cancelling",
        "resolvingFiles",
        "downloading",
        "registeringAlias",
    }
)


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def operation_status_is_terminal(status: str | None) -> bool:
    normalized = (status or "").strip().lower()
    return normalized in {"completed", "failed", "error", "cancelled", "canceled"}


def find_in_flight_operation(
    operations: dict[str, dict[str, Any]],
    *,
    model_id: str | None = None,
    bundle_id: str | None = None,
) -> dict[str, Any] | None:
    for operation in operations.values():
        status = operation.get("status")
        if operation_status_is_terminal(status):
            continue
        if model_id is not None and operation.get("modelId") == model_id:
            return operation
        if bundle_id is not None and operation.get("bundleId") == bundle_id:
            return operation
    return None
