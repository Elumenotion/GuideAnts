from __future__ import annotations

from pathlib import Path

from fastapi.testclient import TestClient

from guideants_video_adapter.app import create_app
from guideants_video_adapter.core import Installation, Job

JOB_ID = "0123456789abcdef0123456789abcdef"
INSTALL_ID = "fedcba9876543210fedcba9876543210"


class StubService:
    def __init__(self, tmp_path: Path) -> None:
        result = tmp_path / "result.mkv"
        result.write_bytes(b"mkv")
        self.result = result
        self.installations: dict[str, Installation] = {}
        self.job = Job(id=JOB_ID, state="completed", output_filename="result.mkv")

    def health(self) -> dict:
        return {"status": "ok"}

    def readiness(self) -> tuple[bool, dict]:
        return True, {"ready": True, "missing": [], "device": {"name": "Fake GPU"}}

    def capabilities(self) -> dict:
        return {"backend": "comfyui", "ready": True}

    def models(self) -> dict:
        return {"ready": True, "bundles": []}

    def install(self, bundle: str) -> Installation:
        installation = Installation(id=INSTALL_ID, bundle=bundle)
        self.installations[installation.id] = installation
        return installation

    def get_job(self, job_id: str) -> Job:
        assert job_id == JOB_ID
        return self.job

    def cancel_job(self, job_id: str) -> Job:
        assert job_id == JOB_ID
        return self.job

    def open_result(self, job_id: str) -> tuple[Path, str]:
        assert job_id == JOB_ID
        return self.result, "result.mkv"

    def submit_image_job(self, *args: object, **kwargs: object) -> Job:
        return self.job


def test_read_only_endpoints_and_result(tmp_path: Path) -> None:
    client = TestClient(create_app(StubService(tmp_path), admin_token="secret"))  # type: ignore[arg-type]
    assert client.get("/health").status_code == 200
    assert client.get("/ready").json()["ready"] is True
    assert client.get("/v1/capabilities").json()["backend"] == "comfyui"
    result = client.get(f"/v1/talking-head/jobs/{JOB_ID}/result")
    assert result.status_code == 200
    assert result.content == b"mkv"


def test_admin_endpoints_require_token(tmp_path: Path) -> None:
    client = TestClient(create_app(StubService(tmp_path), admin_token="secret"))  # type: ignore[arg-type]
    assert client.get("/v1/models").status_code == 401
    headers = {"X-Video-Admin-Token": "secret"}
    assert client.get("/v1/models", headers=headers).status_code == 200
    response = client.post(
        "/v1/admin/models/install",
        headers=headers,
        json={"bundle": "infinitetalk-i2v-v1"},
    )
    assert response.status_code == 202
    assert response.json()["installId"] == INSTALL_ID
    assert (
        client.get(f"/v1/admin/models/install/{INSTALL_ID}", headers=headers).json()[
            "installId"
        ]
        == INSTALL_ID
    )


def test_routes_reject_non_hex_identifiers(tmp_path: Path) -> None:
    client = TestClient(create_app(StubService(tmp_path), admin_token="secret"))  # type: ignore[arg-type]
    assert client.get("/v1/talking-head/jobs/not-a-uuid").status_code == 422
    assert (
        client.get(
            "/v1/admin/models/install/not-a-uuid",
            headers={"X-Video-Admin-Token": "secret"},
        ).status_code
        == 422
    )

