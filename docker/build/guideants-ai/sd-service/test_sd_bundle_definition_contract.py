import json
import os
import sys
import tempfile
import types
import unittest


def _install_module_stub(name: str, attrs: dict | None = None) -> types.ModuleType:
    module = types.ModuleType(name)
    for key, value in (attrs or {}).items():
        setattr(module, key, value)
    sys.modules[name] = module
    return module


_guideants_hf = _install_module_stub("guideants_hf")
_catalog_download = _install_module_stub(
    "guideants_hf.catalog_download",
    {
        "lookup_hf_file_size": lambda *args, **kwargs: None,
    },
)
_transport = _install_module_stub(
    "guideants_hf.transport",
    {
        "download_hf_file": lambda *args, **kwargs: None,
    },
)
_operations = _install_module_stub(
    "guideants_hf.operations",
    {"find_in_flight_operation": lambda *args, **kwargs: None},
)


class _PathSafetyError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


def _ensure_inside_root(root_abs: str, candidate_abs: str) -> None:
    root_norm = os.path.normcase(os.path.abspath(root_abs))
    candidate_norm = os.path.normcase(os.path.abspath(candidate_abs))
    common = os.path.commonpath([root_norm, candidate_norm])
    if common != root_norm:
        raise _PathSafetyError("PATH_ESCAPE", "Target path escapes the model store root.")


_path_safety = _install_module_stub(
    "guideants_hf.path_safety",
    {
        "PathSafetyError": _PathSafetyError,
        "ensure_inside_root": _ensure_inside_root,
    },
)
_guideants_hf.catalog_download = _catalog_download
_guideants_hf.operations = _operations
_guideants_hf.path_safety = _path_safety

_install_module_stub("uvicorn", {"run": lambda *args, **kwargs: None})

class _FastApiStub:
    def __init__(self, *args, **kwargs) -> None:
        self.routes = {}

    def get(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator

    def post(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator

    def put(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator

    def delete(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator

    def on_event(self, *args, **kwargs):
        def decorator(fn):
            return fn

        return decorator


_fastapi = _install_module_stub("fastapi")
_fastapi_responses = _install_module_stub("fastapi.responses", {"JSONResponse": dict})
_fastapi.FastAPI = _FastApiStub
_fastapi.File = lambda *args, **kwargs: None
_fastapi.Form = lambda *args, **kwargs: None
_fastapi.HTTPException = RuntimeError
_fastapi.Request = object
_fastapi.UploadFile = object

class _BaseModelStub:
    def __init__(self, **kwargs) -> None:
        for key, value in kwargs.items():
            setattr(self, key, value)


_pydantic = _install_module_stub("pydantic")
_pydantic.BaseModel = _BaseModelStub
_pydantic.field_validator = lambda *args, **kwargs: (lambda fn: fn)

import sd_service


def _write_verified_role_file(
    role_path: str,
    filename: str,
    content: bytes,
    *,
    repo: str = "org/repo",
) -> str:
    os.makedirs(role_path, exist_ok=True)
    expected_file = os.path.join(role_path, filename)
    with open(expected_file, "wb") as handle:
        handle.write(content)
    sd_service.write_role_file_metadata(
        expected_file,
        expected_size=len(content),
        repo=repo,
        filename=filename,
    )
    return expected_file


class SdBundleDefinitionContractTests(unittest.TestCase):
    def test_list_bundles_does_not_mark_wrong_or_cached_files_ready(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            bundle_id = "FLUX.2-dev"
            paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
            cache_dir = os.path.join(paths["diffusion"], ".cache", "huggingface", "download")
            os.makedirs(cache_dir, exist_ok=True)
            with open(os.path.join(cache_dir, "flux2-dev-Q4_0.gguf"), "wb") as handle:
                handle.write(b"x" * 1024)
            with open(os.path.join(paths["diffusion"], "flux2-dev-Q4_0.gguf"), "wb") as handle:
                handle.write(b"x" * 1024)

            sd_service.write_bundle_definition_payload(
                model_dir,
                bundle_id,
                {
                    "bundleId": bundle_id,
                    "revision": "main",
                    "roles": {
                        "diffusion": {
                            "repo": "unsloth/FLUX.2-dev-GGUF",
                            "file": "flux2-dev-Q5_K_M.gguf",
                        },
                        "vae": {"repo": "org/vae", "file": "vae.safetensors"},
                        "textEncoder": {"repo": "org/te", "file": "te.gguf"},
                    },
                    "sampling": {"steps": 28, "cfgScale": 1.0, "samplingMethod": "euler"},
                },
            )

            bundles = sd_service.list_bundles(model_dir)
            self.assertEqual(len(bundles), 1)
            bundle = bundles[0]
            self.assertFalse(bundle["roles"]["diffusion"]["ready"])
            self.assertFalse(bundle["complete"])

    def test_list_bundles_marks_expected_file_ready_without_size_sidecar(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            bundle_id = "flux2-klein-4b"
            paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
            os.makedirs(paths["diffusion"], exist_ok=True)
            with open(os.path.join(paths["diffusion"], "flux-2-klein-4b-Q8_0.gguf"), "wb") as handle:
                handle.write(b"x" * 128)

            sd_service.write_bundle_definition_payload(
                model_dir,
                bundle_id,
                {
                    "bundleId": bundle_id,
                    "revision": "main",
                    "roles": {
                        "diffusion": {
                            "repo": "unsloth/FLUX.2-klein-4B-GGUF",
                            "file": "flux-2-klein-4b-Q8_0.gguf",
                        },
                        "vae": {"repo": "org/vae", "file": "vae.safetensors"},
                        "textEncoder": {"repo": "org/te", "file": "te.gguf"},
                    },
                    "sampling": {"steps": 4, "cfgScale": 1.0, "samplingMethod": "euler"},
                },
            )

            bundles = sd_service.list_bundles(model_dir)
            bundle = bundles[0]
            self.assertTrue(bundle["roles"]["diffusion"]["ready"])

    def test_list_bundles_marks_verified_expected_file_ready(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            bundle_id = "FLUX.2-dev"
            paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
            _write_verified_role_file(
                paths["diffusion"],
                "flux2-dev-Q5_K_M.gguf",
                b"x" * 128,
                repo="unsloth/FLUX.2-dev-GGUF",
            )

            sd_service.write_bundle_definition_payload(
                model_dir,
                bundle_id,
                {
                    "bundleId": bundle_id,
                    "revision": "main",
                    "roles": {
                        "diffusion": {
                            "repo": "unsloth/FLUX.2-dev-GGUF",
                            "file": "flux2-dev-Q5_K_M.gguf",
                        },
                        "vae": {"repo": "org/vae", "file": "vae.safetensors"},
                        "textEncoder": {"repo": "org/te", "file": "te.gguf"},
                    },
                    "sampling": {"steps": 28, "cfgScale": 1.0, "samplingMethod": "euler"},
                },
            )

            bundles = sd_service.list_bundles(model_dir)
            bundle = bundles[0]
            self.assertTrue(bundle["roles"]["diffusion"]["ready"])
            self.assertFalse(bundle["complete"])

    def test_migrate_bundle_folder_renames_legacy_directory_to_canonical_id(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            legacy_id = "flux2-klein-4b-q4ks"
            canonical_id = "flux2-klein-4b"
            legacy_path = os.path.join(model_dir, "bundles", legacy_id, "diffusion")
            os.makedirs(legacy_path, exist_ok=True)
            with open(os.path.join(legacy_path, "flux-2-klein-4b-Q8_0.gguf"), "wb") as handle:
                handle.write(b"x" * 64)

            result = sd_service.migrate_bundle_folder(model_dir, legacy_id, canonical_id)

            self.assertEqual(result["action"], "renamed")
            self.assertTrue(
                os.path.isfile(
                    os.path.join(
                        model_dir,
                        "bundles",
                        canonical_id,
                        "diffusion",
                        "flux-2-klein-4b-Q8_0.gguf",
                    )
                )
            )
            self.assertFalse(os.path.isdir(os.path.join(model_dir, "bundles", legacy_id)))

    def test_migrate_bundle_folder_updates_active_bundle_marker(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            legacy_id = "FLUX.2-dev-GGUF-Q5_K_M"
            canonical_id = "FLUX.2-dev"
            os.makedirs(os.path.join(model_dir, "bundles", legacy_id), exist_ok=True)
            sd_service.write_active_bundle_marker(model_dir, legacy_id)

            sd_service.migrate_bundle_folder(model_dir, legacy_id, canonical_id)

            self.assertEqual(sd_service.read_active_bundle(model_dir), canonical_id)

    def test_read_bundle_definition_returns_none_when_file_missing(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            self.assertIsNone(sd_service.read_bundle_definition(model_dir, "missing-bundle"))

    def test_upsert_and_read_bundle_definition_round_trip(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            request = sd_service.UpsertBundleDefinitionRequest(
                revision="main",
                roles={
                    "diffusion": {"repo": "org/diff", "file": "model.gguf"},
                    "vae": {"repo": "org/vae", "file": "vae.safetensors"},
                    "textEncoder": {"repo": "org/te", "file": "te.gguf"},
                },
                sampling={"steps": 4, "cfgScale": 1.0, "samplingMethod": "euler"},
            )
            written = sd_service.upsert_bundle_definition(model_dir, "test-bundle", request)
            self.assertEqual(written["sampling"]["steps"], 4)

            loaded = sd_service.read_bundle_definition(model_dir, "test-bundle")
            self.assertIsNotNone(loaded)
            assert loaded is not None
            self.assertEqual(loaded["roles"]["diffusion"]["file"], "model.gguf")
            self.assertEqual(loaded["sampling"]["steps"], 4)

            path = sd_service.bundle_definition_path(model_dir, "test-bundle")
            self.assertTrue(os.path.isfile(path))
            with open(path, "r", encoding="utf-8") as handle:
                payload = json.load(handle)
            self.assertEqual(payload["bundleId"], "test-bundle")

    def test_require_bundle_sampling_fails_when_sampling_missing(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            sd_service.write_bundle_definition_payload(
                model_dir,
                "incomplete-bundle",
                {
                    "bundleId": "incomplete-bundle",
                    "revision": "main",
                    "roles": {
                        "diffusion": {"repo": "org/diff", "file": "model.gguf"},
                        "vae": {"repo": "org/vae", "file": "vae.safetensors"},
                        "textEncoder": {"repo": "org/te", "file": "te.gguf"},
                    },
                },
            )
            with self.assertRaises(RuntimeError):
                sd_service.require_bundle_sampling(model_dir, "incomplete-bundle")

    def test_resolve_initial_bundle_role_states_skips_download_when_corrected_file_exists(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            bundle_id = "flux2-klein-4b"
            paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
            _write_verified_role_file(
                paths["diffusion"],
                "flux-2-klein-4b-Q8_0.gguf",
                b"gguf",
                repo="unsloth/FLUX.2-klein-4B-GGUF",
            )

            previous_definition = {
                "revision": "main",
                "roles": {
                    "diffusion": {"repo": "unsloth/FLUX.2-klein-4B-GGUF", "file": "wrong-name.gguf"},
                    "vae": {"repo": "org/vae", "file": "vae.safetensors"},
                    "textEncoder": {"repo": "org/te", "file": "te.gguf"},
                },
            }
            request = sd_service.DownloadBundleRequest(
                bundle_id=bundle_id,
                diffusion_repo="unsloth/FLUX.2-klein-4B-GGUF",
                diffusion_file="flux-2-klein-4b-Q8_0.gguf",
                vae_repo="org/vae",
                vae_file="vae.safetensors",
                text_encoder_repo="org/te",
                text_encoder_file="te.gguf",
                sampling_steps=4,
                sampling_cfg_scale=1.0,
                sampling_method="euler",
            )

            states = sd_service.resolve_initial_bundle_role_states(previous_definition, request, paths)

            self.assertEqual(states["diffusion"], "ready")

    def test_resolve_initial_bundle_role_states_force_redownload_queues_existing_file(self) -> None:
        with tempfile.TemporaryDirectory() as model_dir:
            bundle_id = "flux2-klein-4b"
            paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
            _write_verified_role_file(
                paths["diffusion"],
                "flux-2-klein-4b-Q8_0.gguf",
                b"gguf",
                repo="unsloth/FLUX.2-klein-4B-GGUF",
            )

            request = sd_service.DownloadBundleRequest(
                bundle_id=bundle_id,
                diffusion_repo="unsloth/FLUX.2-klein-4B-GGUF",
                diffusion_file="flux-2-klein-4b-Q8_0.gguf",
                vae_repo="org/vae",
                vae_file="vae.safetensors",
                text_encoder_repo="org/te",
                text_encoder_file="te.gguf",
                sampling_steps=4,
                sampling_cfg_scale=1.0,
                sampling_method="euler",
                force_redownload=True,
            )

            states = sd_service.resolve_initial_bundle_role_states(None, request, paths)

            self.assertEqual(states["diffusion"], "queued")

    def test_start_bundle_download_force_redownload_deletes_existing_file_before_hf(self) -> None:
        downloads: list[tuple[str, str, str]] = []

        def _record_staged_download(**kwargs) -> None:
            target_path = kwargs["target_path"]
            filename = kwargs["filename"]
            expected_file = sd_service.resolve_role_file_path(target_path, filename)
            os.makedirs(os.path.dirname(expected_file), exist_ok=True)
            with open(expected_file, "wb") as handle:
                handle.write(b"fresh")
            downloads.append((kwargs["repo"], filename, kwargs["role"]))

        original_download = sd_service.download_bundle_role_via_staging
        original_lookup = sd_service.lookup_hf_file_size
        sd_service.download_bundle_role_via_staging = _record_staged_download
        sd_service.lookup_hf_file_size = lambda repo, filename, token, revision=None: 5
        try:
            with tempfile.TemporaryDirectory() as model_dir:
                bundle_id = "flux2-klein-4b"
                paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
                diffusion_path = os.path.join(paths["diffusion"], "flux-2-klein-4b-Q8_0.gguf")
                os.makedirs(paths["diffusion"], exist_ok=True)
                with open(diffusion_path, "wb") as handle:
                    handle.write(b"stale")

                request = sd_service.DownloadBundleRequest(
                    bundle_id=bundle_id,
                    diffusion_repo="unsloth/FLUX.2-klein-4B-GGUF",
                    diffusion_file="flux-2-klein-4b-Q8_0.gguf",
                    vae_repo="org/vae",
                    vae_file="vae.safetensors",
                    text_encoder_repo="org/te",
                    text_encoder_file="te.gguf",
                    sampling_steps=4,
                    sampling_cfg_scale=1.0,
                    sampling_method="euler",
                    force_redownload=True,
                )

                operation = sd_service.start_bundle_download(request, model_dir)
                operation_id = operation["operationId"]

                import time

                deadline = time.monotonic() + 5.0
                while time.monotonic() < deadline:
                    with sd_service.BUNDLE_OPS_LOCK:
                        current = sd_service.BUNDLE_OPERATIONS.get(operation_id)
                    if current and current.get("status") in {"completed", "failed", "error"}:
                        break
                    time.sleep(0.05)

                with sd_service.BUNDLE_OPS_LOCK:
                    final = sd_service.BUNDLE_OPERATIONS[operation_id]

                self.assertEqual(final["status"], "completed")
                self.assertEqual(
                    downloads,
                    [
                        ("unsloth/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q8_0.gguf", "diffusion"),
                        ("org/vae", "vae.safetensors", "vae"),
                        ("org/te", "te.gguf", "textEncoder"),
                    ],
                )
                with open(diffusion_path, "rb") as handle:
                    self.assertEqual(handle.read(), b"fresh")
        finally:
            sd_service.download_bundle_role_via_staging = original_download
            sd_service.lookup_hf_file_size = original_lookup

    def test_start_bundle_download_skips_hf_when_expected_file_already_on_disk(self) -> None:
        downloads: list[tuple[str, str, str]] = []

        def _record_staged_download(**kwargs) -> None:
            downloads.append((kwargs["repo"], kwargs["filename"], kwargs["role"]))

        original_download = sd_service.download_bundle_role_via_staging
        original_lookup = sd_service.lookup_hf_file_size
        sd_service.download_bundle_role_via_staging = _record_staged_download
        sd_service.lookup_hf_file_size = lambda repo, filename, token, revision=None: {
            "flux-2-klein-4b-Q8_0.gguf": 2,
            "vae.safetensors": 2,
            "te.gguf": 2,
        }.get(filename)
        try:
            with tempfile.TemporaryDirectory() as model_dir:
                bundle_id = "flux2-klein-4b"
                paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
                _write_verified_role_file(paths["diffusion"], "flux-2-klein-4b-Q8_0.gguf", b"ok", repo="unsloth/FLUX.2-klein-4B-GGUF")
                _write_verified_role_file(paths["vae"], "vae.safetensors", b"ok", repo="org/vae")
                _write_verified_role_file(paths["textEncoder"], "te.gguf", b"ok", repo="org/te")

                previous_definition = {
                    "revision": "main",
                    "roles": {
                        "diffusion": {"repo": "unsloth/FLUX.2-klein-4B-GGUF", "file": "wrong-name.gguf"},
                        "vae": {"repo": "org/vae", "file": "vae.safetensors"},
                        "textEncoder": {"repo": "org/te", "file": "te.gguf"},
                    },
                }
                sd_service.write_bundle_definition_payload(
                    model_dir,
                    bundle_id,
                    {
                        "bundleId": bundle_id,
                        "revision": "main",
                        "roles": previous_definition["roles"],
                        "sampling": {"steps": 4, "cfgScale": 1.0, "samplingMethod": "euler"},
                    },
                )

                request = sd_service.DownloadBundleRequest(
                    bundle_id=bundle_id,
                    diffusion_repo="unsloth/FLUX.2-klein-4B-GGUF",
                    diffusion_file="flux-2-klein-4b-Q8_0.gguf",
                    vae_repo="org/vae",
                    vae_file="vae.safetensors",
                    text_encoder_repo="org/te",
                    text_encoder_file="te.gguf",
                    sampling_steps=4,
                    sampling_cfg_scale=1.0,
                    sampling_method="euler",
                )

                operation = sd_service.start_bundle_download(request, model_dir)
                operation_id = operation["operationId"]

                import time

                deadline = time.monotonic() + 5.0
                while time.monotonic() < deadline:
                    with sd_service.BUNDLE_OPS_LOCK:
                        current = sd_service.BUNDLE_OPERATIONS.get(operation_id)
                    if current and current.get("status") in {"completed", "failed", "error"}:
                        break
                    time.sleep(0.05)

                with sd_service.BUNDLE_OPS_LOCK:
                    final = sd_service.BUNDLE_OPERATIONS[operation_id]

                self.assertEqual(final["status"], "completed")
                self.assertEqual(final["roles"]["diffusion"], "ready")
                self.assertEqual(downloads, [])
        finally:
            sd_service.download_bundle_role_via_staging = original_download
            sd_service.lookup_hf_file_size = original_lookup

    def test_download_bundle_role_via_staging_keeps_partial_files_out_of_role_dir(self) -> None:
        destinations: list[str] = []

        def _record_hf_download(
            repository: str,
            relative_path: str,
            destination_path: str,
            token: str | None,
            *,
            revision: str | None = "main",
            progress_callback=None,
        ) -> None:
            destinations.append(destination_path)
            os.makedirs(os.path.dirname(destination_path), exist_ok=True)
            with open(destination_path, "wb") as handle:
                handle.write(b"complete")

        original_download = sd_service.download_hf_file
        original_lookup = sd_service.lookup_hf_file_size
        sd_service.download_hf_file = _record_hf_download
        sd_service.lookup_hf_file_size = lambda repo, filename, token, revision=None: 8
        try:
            with tempfile.TemporaryDirectory() as model_dir:
                bundle_id = "flux2-klein-4b"
                paths = sd_service.expected_bundle_paths(model_dir, bundle_id)
                operation_id = "op-staging-test"

                sd_service.download_bundle_role_via_staging(
                    model_dir=model_dir,
                    operation_id=operation_id,
                    role="diffusion",
                    repo="unsloth/FLUX.2-klein-4B-GGUF",
                    filename="flux-2-klein-4b-Q8_0.gguf",
                    target_path=paths["diffusion"],
                    hf_token=None,
                    revision="main",
                )

                expected_file = os.path.join(paths["diffusion"], "flux-2-klein-4b-Q8_0.gguf")
                self.assertTrue(os.path.isfile(expected_file))
                self.assertEqual(len(destinations), 1)
                self.assertIn(f"{os.sep}.staging{os.sep}", destinations[0])
                self.assertNotIn(paths["diffusion"], destinations[0])
                self.assertFalse(os.path.isfile(expected_file + ".tmp"))
                sd_service.cleanup_bundle_operation_staging(model_dir, operation_id)
                self.assertFalse(os.path.isdir(os.path.join(model_dir, ".staging", operation_id)))
        finally:
            sd_service.download_hf_file = original_download
            sd_service.lookup_hf_file_size = original_lookup


if __name__ == "__main__":
    unittest.main()
