"""Live manifest-drift suite for all 15 curated repositories.

Run explicitly (not part of default deterministic unit discovery):

    python -m unittest discover -s docker/build/guideants-ai/llama-admin-service/tests -p "test_live_manifest_drift.py" -v

Requires HuggingFace:Token via GA_LLAMA_LIVE_HF_TOKEN or HUGGINGFACE_TOKEN/HF_TOKEN.
When no token is configured the module fails fast with a BLOCKED error for Phase 8B deferral.
"""

import os
import sys
import unittest
from pathlib import Path

SERVICE_ROOT = Path(__file__).resolve().parents[1]
LIB_ROOT = SERVICE_ROOT.parent / "lib"
for path in (str(LIB_ROOT), str(SERVICE_ROOT)):
    if path not in sys.path:
        sys.path.insert(0, path)

import llama_catalog
from llama_catalog import cached_manifest, resolve_definition_quants
from guideants_hf.repository import HuggingFaceAccessError


def _resolve_live_token() -> str:
    for key in ("GA_LLAMA_LIVE_HF_TOKEN", "HUGGINGFACE_TOKEN", "HF_TOKEN"):
        value = (os.getenv(key) or "").strip()
        if value:
            return value
    raise RuntimeError(
        "BLOCKED: no Hugging Face token in GA_LLAMA_LIVE_HF_TOKEN, HUGGINGFACE_TOKEN, or HF_TOKEN"
    )


_manifest_ids: list[str] = []


class LiveManifestDriftTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.token = _resolve_live_token()
        llama_catalog.cached_manifest.cache_clear()
        cls.manifest = cached_manifest()
        if len(cls.manifest.get("models", [])) != 15:
            raise AssertionError(f"Expected 15 manifest definitions, found {len(cls.manifest.get('models', []))}")
        global _manifest_ids
        _manifest_ids = [str(model["id"]) for model in cls.manifest["models"] if isinstance(model.get("id"), str)]

    def test_manifest_has_fifteen_named_entries(self) -> None:
        ids = [model["id"] for model in self.manifest["models"]]
        self.assertEqual(15, len(set(ids)))

    def _resolve_entry(self, catalog_id: str) -> None:
        try:
            payload = resolve_definition_quants(catalog_id, self.token)
        except HuggingFaceAccessError as exc:
            self.fail(f"{catalog_id}: Hugging Face access failed ({exc.code}): {exc}")
        self.assertTrue(payload["resolvedRevision"])
        self.assertGreater(len(payload["quants"]), 0, f"{catalog_id}: no quants discovered")
        recommended = next(
            (m for m in self.manifest["models"] if m["id"] == catalog_id),
            None,
        )
        self.assertIsNotNone(recommended)
        labels = {quant["label"] for quant in payload["quants"]}
        for badge in recommended.get("quantMetadata", {}).get("recommendedLabels", []):
            self.assertIn(
                badge,
                labels,
                f"{catalog_id}: recommended label '{badge}' missing at commit {payload['resolvedRevision']}",
            )


def _make_test(catalog_id: str):
    def test(self: LiveManifestDriftTests) -> None:
        self._resolve_entry(catalog_id)

    test.__name__ = f"test_live_{catalog_id.replace('.', '_').replace('-', '_')}"
    return test


_manifest = None


def _make_test(catalog_id: str):
    def test(self: LiveManifestDriftTests) -> None:
        self._resolve_entry(catalog_id)

    test.__name__ = f"test_live_{catalog_id.replace('.', '_').replace('-', '_')}"
    return test


def _register_live_tests() -> None:
    llama_catalog.cached_manifest.cache_clear()
    manifest = cached_manifest()
    for model in manifest.get("models", []):
        if isinstance(model, dict) and isinstance(model.get("id"), str):
            catalog_id = model["id"]
            setattr(LiveManifestDriftTests, _make_test(catalog_id).__name__, _make_test(catalog_id))


_register_live_tests()


if __name__ == "__main__":
    unittest.main()
