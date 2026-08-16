"""Loopback FastAPI surface for the GuideAnts InfiniteTalk adapter."""

from __future__ import annotations

import json
import os
import secrets
from pathlib import Path
from typing import Annotated, Any

from fastapi import FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from pydantic import BaseModel
from starlette.concurrency import run_in_threadpool

from .core import (
    AdapterError,
    AdapterService,
    ComfyTransport,
    DEFAULT_NEGATIVE_PROMPT,
    DEFAULT_POSITIVE_PROMPT,
    IMAGE_GENERATE_WORKFLOW_VERSION,
    IMAGE_WORKFLOW_VERSION,
    WORKFLOW_VERSION,
    validate_identifier,
)


def _path_env(name: str, default: str) -> Path:
    value = os.getenv(name, default)
    if not Path(value).is_absolute():
        raise RuntimeError(f"{name} must be an absolute path")
    return Path(value)


def build_service() -> AdapterService:
    return AdapterService(
        jobs_root=_path_env("VIDEO_JOBS_ROOT", "/var/lib/guideants-video/jobs"),
        models_root=_path_env("VIDEO_MODELS_ROOT", "/models"),
        workflow_path=_path_env(
            "VIDEO_WORKFLOW_PATH", "/opt/guideants-video/workflows/infinitetalk-i2v-v1.json"
        ),
        image_workflow_path=_path_env(
            "IMAGE_WORKFLOW_PATH",
            "/opt/guideants/comfyui-video/workflows/qwen-image-edit-v1.json",
        ),
        image_generate_workflow_path=_path_env(
            "IMAGE_GENERATE_WORKFLOW_PATH",
            "/opt/guideants/comfyui-video/workflows/qwen-image-v1.json",
        ),
        manifest_path=_path_env(
            "VIDEO_MODEL_MANIFEST", "/opt/guideants-video/catalog/manifest.json"
        ),
        comfy=ComfyTransport(os.getenv("COMFYUI_URL", "http://127.0.0.1:8188")),
    )


class InstallRequest(BaseModel):
    bundle: str


def create_app(service: AdapterService | None = None, admin_token: str | None = None) -> FastAPI:
    app = FastAPI(title="GuideAnts InfiniteTalk Adapter", version="1.0.0")
    app.state.service = service
    app.state.admin_token = (
        admin_token if admin_token is not None else os.getenv("VIDEO_ADMIN_TOKEN", "")
    )

    def require_admin(token: str | None) -> None:
        configured = app.state.admin_token
        if not configured:
            raise HTTPException(503, "VIDEO_ADMIN_TOKEN is not configured")
        if token is None or not secrets.compare_digest(token, configured):
            raise HTTPException(401, "invalid video admin token")

    def get_service() -> AdapterService:
        current = app.state.service
        if current is None:
            current = build_service()
            app.state.service = current
        return current

    @app.exception_handler(AdapterError)
    async def adapter_error_handler(_request: Any, exc: AdapterError) -> JSONResponse:
        return JSONResponse(status_code=exc.status_code, content={"detail": str(exc)})

    @app.get("/health")
    def health() -> dict[str, Any]:
        return get_service().health()

    @app.get("/ready")
    def ready() -> JSONResponse:
        is_ready, payload = get_service().readiness()
        return JSONResponse(status_code=200 if is_ready else 503, content=payload)

    @app.get("/v1/capabilities")
    def capabilities() -> dict[str, Any]:
        return get_service().capabilities()

    @app.get("/v1/models")
    def models(
        x_video_admin_token: Annotated[str | None, Header()] = None,
    ) -> dict[str, Any]:
        require_admin(x_video_admin_token)
        return get_service().models()

    @app.post("/v1/admin/models/install", status_code=202)
    def install_models(
        request: InstallRequest,
        x_video_admin_token: Annotated[str | None, Header()] = None,
    ) -> dict[str, Any]:
        require_admin(x_video_admin_token)
        return get_service().install(request.bundle).public()

    @app.get("/v1/admin/models/install/{installation_id}")
    def installation_status(
        installation_id: str,
        x_video_admin_token: Annotated[str | None, Header()] = None,
    ) -> dict[str, Any]:
        require_admin(x_video_admin_token)
        validate_identifier(installation_id, "install")
        installation = get_service().installations.get(installation_id)
        if installation is None:
            raise HTTPException(404, "installation not found")
        return installation.public()

    @app.post("/v1/talking-head/jobs", status_code=202)
    async def submit_job(
        source: Annotated[UploadFile, File()],
        audio: Annotated[UploadFile, File()],
        output_filename: Annotated[str, Form()],
        workflow_version: Annotated[str, Form()] = WORKFLOW_VERSION,
        parameters: Annotated[str, Form()] = "{}",
        positive_prompt: Annotated[str, Form()] = DEFAULT_POSITIVE_PROMPT,
        negative_prompt: Annotated[str, Form()] = DEFAULT_NEGATIVE_PROMPT,
    ) -> dict[str, Any]:
        try:
            parsed_parameters = json.loads(parameters)
        except json.JSONDecodeError as exc:
            raise HTTPException(422, "parameters must be a JSON object") from exc
        if not isinstance(parsed_parameters, dict):
            raise HTTPException(422, "parameters must be a JSON object")
        source_type = (source.content_type or "").split(";", 1)[0].lower()
        audio_type = (audio.content_type or "").split(";", 1)[0].lower()
        source_bytes = await source.read()
        audio_bytes = await audio.read()
        job = await run_in_threadpool(
            get_service().submit_job,
            source_bytes,
            source_type,
            audio_bytes,
            audio_type,
            output_filename,
            workflow_version,
            parsed_parameters,
            positive_prompt,
            negative_prompt,
        )
        return job.public()

    @app.get("/v1/talking-head/jobs/{job_id}")
    def job_status(job_id: str) -> dict[str, Any]:
        validate_identifier(job_id, "job")
        return get_service().get_job(job_id).public()

    @app.post("/v1/talking-head/jobs/{job_id}/cancel", status_code=202)
    def cancel_job(job_id: str) -> dict[str, Any]:
        validate_identifier(job_id, "job")
        return get_service().cancel_job(job_id).public()

    @app.get("/v1/talking-head/jobs/{job_id}/result")
    def job_result(job_id: str) -> FileResponse:
        validate_identifier(job_id, "job")
        path, filename = get_service().open_result(job_id)
        return FileResponse(path, media_type="video/mp4", filename=filename)

    @app.post("/v1/image/jobs", status_code=202)
    async def submit_image_job(
        source: Annotated[UploadFile, File()],
        prompt: Annotated[str, Form()],
        output_filename: Annotated[str, Form()],
        workflow_version: Annotated[str, Form()] = IMAGE_WORKFLOW_VERSION,
        negative_prompt: Annotated[str, Form()] = " ",
        parameters: Annotated[str, Form()] = "{}",
    ) -> dict[str, Any]:
        try:
            parsed_parameters = json.loads(parameters)
        except json.JSONDecodeError as exc:
            raise HTTPException(422, "parameters must be a JSON object") from exc
        if not isinstance(parsed_parameters, dict):
            raise HTTPException(422, "parameters must be a JSON object")
        source_type = (source.content_type or "").split(";", 1)[0].lower()
        source_bytes = await source.read()
        job = await run_in_threadpool(
            get_service().submit_image_job,
            source_bytes,
            source_type,
            prompt,
            output_filename,
            workflow_version,
            parsed_parameters,
            negative_prompt,
        )
        return job.public()

    @app.post("/v1/image/generate/jobs", status_code=202)
    async def submit_image_generate_job(
        prompt: Annotated[str, Form()],
        output_filename: Annotated[str, Form()],
        workflow_version: Annotated[str, Form()] = IMAGE_GENERATE_WORKFLOW_VERSION,
        negative_prompt: Annotated[str, Form()] = " ",
        parameters: Annotated[str, Form()] = "{}",
    ) -> dict[str, Any]:
        try:
            parsed_parameters = json.loads(parameters)
        except json.JSONDecodeError as exc:
            raise HTTPException(422, "parameters must be a JSON object") from exc
        if not isinstance(parsed_parameters, dict):
            raise HTTPException(422, "parameters must be a JSON object")
        job = await run_in_threadpool(
            get_service().submit_image_generate_job,
            prompt,
            output_filename,
            workflow_version,
            parsed_parameters,
            negative_prompt,
        )
        return job.public()

    @app.get("/v1/image/jobs/{job_id}")
    def image_job_status(job_id: str) -> dict[str, Any]:
        validate_identifier(job_id, "job")
        return get_service().get_job(job_id).public()

    @app.post("/v1/image/jobs/{job_id}/cancel", status_code=202)
    def cancel_image_job(job_id: str) -> dict[str, Any]:
        validate_identifier(job_id, "job")
        return get_service().cancel_job(job_id).public()

    @app.get("/v1/image/jobs/{job_id}/result")
    def image_job_result(job_id: str) -> FileResponse:
        validate_identifier(job_id, "job")
        path, filename = get_service().open_result(job_id)
        suffix = Path(filename).suffix.lower()
        media_type = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".webp": "image/webp"}.get(
            suffix, "application/octet-stream"
        )
        return FileResponse(path, media_type=media_type, filename=filename)

    return app


APP = create_app()

