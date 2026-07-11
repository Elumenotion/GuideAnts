"""Subordinate durable operation journal for llama-admin (D2)."""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
import threading
from dataclasses import dataclass, field
from typing import Any

from guideants_hf.operations import utc_now_iso


class OperationJournalError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


JOURNAL_LOCK = threading.Lock()


def canonical_json(payload: dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=True, sort_keys=True, separators=(",", ":"))


def hash_immutable_input(payload: dict[str, Any]) -> str:
    digest = hashlib.sha256(canonical_json(payload).encode("utf-8")).hexdigest()
    return f"sha256:{digest}"


@dataclass
class JournalStep:
    step: str
    path: str | None = None
    completed_at: str = field(default_factory=utc_now_iso)

    def to_record(self) -> dict[str, Any]:
        record: dict[str, Any] = {"step": self.step, "completedAt": self.completed_at}
        if self.path is not None:
            record["path"] = self.path
        return record


@dataclass
class OperationJournalRecord:
    operation_id: str
    immutable_input: dict[str, Any]
    immutable_input_hash: str
    alias: str
    status: str = "queued"
    progress: float | None = 0.0
    error_message: str | None = None
    log_line: str | None = None
    journal: list[JournalStep] = field(default_factory=list)
    completed_side_effects: list[str] = field(default_factory=list)
    created_at: str = field(default_factory=utc_now_iso)
    completed_at: str | None = None
    ini_sha256: str | None = None

    def to_dto(self) -> dict[str, Any]:
        return {
            "operationId": self.operation_id,
            "status": self.status,
            "routerModelId": self.alias,
            "progress": self.progress,
            "errorMessage": self.error_message,
            "logLine": self.log_line,
            "immutableInputHash": self.immutable_input_hash,
            "journal": [step.to_record() for step in self.journal],
        }

    def to_disk(self) -> dict[str, Any]:
        return {
            "operationId": self.operation_id,
            "immutableInput": self.immutable_input,
            "immutableInputHash": self.immutable_input_hash,
            "alias": self.alias,
            "status": self.status,
            "progress": self.progress,
            "errorMessage": self.error_message,
            "logLine": self.log_line,
            "journal": [step.to_record() for step in self.journal],
            "completedSideEffects": list(self.completed_side_effects),
            "createdAt": self.created_at,
            "completedAt": self.completed_at,
            "iniSha256": self.ini_sha256,
        }

    @classmethod
    def from_disk(cls, payload: dict[str, Any]) -> OperationJournalRecord:
        journal_steps: list[JournalStep] = []
        for item in payload.get("journal") or []:
            if not isinstance(item, dict):
                continue
            journal_steps.append(
                JournalStep(
                    step=str(item.get("step") or ""),
                    path=item.get("path") if isinstance(item.get("path"), str) else None,
                    completed_at=str(item.get("completedAt") or utc_now_iso()),
                )
            )
        immutable_input = payload.get("immutableInput")
        if not isinstance(immutable_input, dict):
            immutable_input = {}
        operation_id = str(payload.get("operationId") or "").strip()
        alias = str(payload.get("alias") or payload.get("routerModelId") or "").strip()
        immutable_hash = str(payload.get("immutableInputHash") or hash_immutable_input(immutable_input))
        return cls(
            operation_id=operation_id,
            immutable_input=immutable_input,
            immutable_input_hash=immutable_hash,
            alias=alias,
            status=str(payload.get("status") or "queued"),
            progress=payload.get("progress") if isinstance(payload.get("progress"), (int, float)) else None,
            error_message=payload.get("errorMessage") if isinstance(payload.get("errorMessage"), str) else None,
            log_line=payload.get("logLine") if isinstance(payload.get("logLine"), str) else None,
            journal=journal_steps,
            completed_side_effects=[
                str(x) for x in (payload.get("completedSideEffects") or []) if isinstance(x, str)
            ],
            created_at=str(payload.get("createdAt") or utc_now_iso()),
            completed_at=payload.get("completedAt") if isinstance(payload.get("completedAt"), str) else None,
            ini_sha256=payload.get("iniSha256") if isinstance(payload.get("iniSha256"), str) else None,
        )


class OperationJournalStore:
    def __init__(self, journal_root: str) -> None:
        self._journal_root = os.path.abspath(journal_root)
        self._records: dict[str, OperationJournalRecord] = {}
        os.makedirs(self._journal_root, exist_ok=True)
        self.reload_from_disk()

    @property
    def journal_root(self) -> str:
        return self._journal_root

    def _record_path(self, operation_id: str) -> str:
        safe_id = operation_id.strip()
        return os.path.join(self._journal_root, f"{safe_id}.json")

    def reload_from_disk(self) -> None:
        with JOURNAL_LOCK:
            if not os.path.isdir(self._journal_root):
                return
            for name in os.listdir(self._journal_root):
                if not name.endswith(".json"):
                    continue
                path = os.path.join(self._journal_root, name)
                try:
                    with open(path, "r", encoding="utf-8") as handle:
                        payload = json.load(handle)
                    if isinstance(payload, dict):
                        record = OperationJournalRecord.from_disk(payload)
                        if record.operation_id:
                            self._records[record.operation_id] = record
                except (OSError, json.JSONDecodeError):
                    continue

    def get(self, operation_id: str) -> OperationJournalRecord | None:
        with JOURNAL_LOCK:
            return self._records.get(operation_id)

    def list_records(self) -> list[OperationJournalRecord]:
        with JOURNAL_LOCK:
            return list(self._records.values())

    def save(self, record: OperationJournalRecord) -> None:
        with JOURNAL_LOCK:
            self._records[record.operation_id] = record
            self._write_atomic(record)

    def _write_atomic(self, record: OperationJournalRecord) -> None:
        os.makedirs(self._journal_root, exist_ok=True)
        destination = self._record_path(record.operation_id)
        directory = os.path.dirname(destination)
        payload = json.dumps(record.to_disk(), ensure_ascii=True, sort_keys=True, indent=2)
        temp_fd, temp_path = tempfile.mkstemp(dir=directory, prefix="op-", suffix=".json.tmp")
        try:
            with os.fdopen(temp_fd, "w", encoding="utf-8") as handle:
                handle.write(payload)
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temp_path, destination)
        finally:
            if os.path.exists(temp_path):
                try:
                    os.remove(temp_path)
                except OSError:
                    pass

    def create(
        self,
        *,
        operation_id: str,
        immutable_input: dict[str, Any],
        alias: str,
    ) -> OperationJournalRecord:
        if self.get(operation_id) is not None:
            raise OperationJournalError("OPERATION_EXISTS", f"Operation '{operation_id}' already exists.")
        record = OperationJournalRecord(
            operation_id=operation_id,
            immutable_input=immutable_input,
            immutable_input_hash=hash_immutable_input(immutable_input),
            alias=alias,
        )
        self.save(record)
        return record

    def append_step(self, operation_id: str, step: str, path: str | None = None) -> None:
        record = self.get(operation_id)
        if record is None:
            raise OperationJournalError("OPERATION_NOT_FOUND", f"Operation '{operation_id}' not found.")
        record.journal.append(JournalStep(step=step, path=path))
        self.save(record)

    def mark_side_effect(self, operation_id: str, side_effect: str) -> None:
        record = self.get(operation_id)
        if record is None:
            raise OperationJournalError("OPERATION_NOT_FOUND", f"Operation '{operation_id}' not found.")
        if side_effect not in record.completed_side_effects:
            record.completed_side_effects.append(side_effect)
        self.save(record)

    def update(self, operation_id: str, **fields: Any) -> None:
        record = self.get(operation_id)
        if record is None:
            raise OperationJournalError("OPERATION_NOT_FOUND", f"Operation '{operation_id}' not found.")
        for key, value in fields.items():
            if hasattr(record, key):
                setattr(record, key, value)
        self.save(record)
