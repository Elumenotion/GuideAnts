import json
import sys
import unittest
from pathlib import Path
from unittest import mock

SERVICE_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = SERVICE_ROOT.parents[3]
LIB_ROOT = SERVICE_ROOT.parent / "lib"
CONTRACTS_ROOT = REPO_ROOT / "docs" / "llama-router-preset-ui-execution" / "contracts"
for path in (str(LIB_ROOT), str(SERVICE_ROOT)):
    if path not in sys.path:
        sys.path.insert(0, path)

import llama_catalog
from guideants_hf.quant_grouping import QuantGroupingError, resolve_projector
from llama_catalog import CatalogDefinitionError, build_catalog_response, get_definition


class LlamaCatalogServiceTests(unittest.TestCase):
    def test_build_catalog_response_matches_contract_shape(self) -> None:
        response = build_catalog_response()
        self.assertEqual(1, response["schemaVersion"])
        self.assertEqual("llama", response["task"])
        self.assertEqual("2026-07-10", response["catalogVersion"])
        self.assertEqual(15, len(response["models"]))
        self.assertNotIn("selectedQuant", response)
        self.assertNotIn("defaultQuant", response)

    def test_get_definition_unknown_id(self) -> None:
        with self.assertRaises(CatalogDefinitionError) as ctx:
            get_definition("missing-id")
        self.assertEqual("CATALOG_DEFINITION_NOT_FOUND", ctx.exception.code)

    def test_get_definition_version_mismatch(self) -> None:
        with self.assertRaises(CatalogDefinitionError) as ctx:
            get_definition("qwen3.6-35b-a3b-mtp", catalog_version="1999-01-01")
        self.assertEqual("CATALOG_VERSION_MISMATCH", ctx.exception.code)

    @mock.patch("llama_catalog.resolve_repository_commit", return_value="abc123")
    @mock.patch("llama_catalog.list_repository_artifacts_at_revision")
    def test_resolve_definition_quants_enriches_guidance(
        self,
        list_files: mock.Mock,
        _resolve_commit: mock.Mock,
    ) -> None:
        list_files.return_value = [
            {"type": "file", "path": "Qwen3.6-35B-A3B-MTP-UD-Q4_K_XL.gguf", "size": 100},
            {"type": "file", "path": "mmproj-F16.gguf", "size": 900_000_000},
        ]
        payload = llama_catalog.resolve_definition_quants("qwen3.6-35b-a3b-mtp", None)
        self.assertEqual("abc123", payload["resolvedRevision"])
        self.assertIsNotNone(payload["projector"])
        self.assertEqual("mmproj-F16.gguf", payload["projector"]["path"])
        self.assertEqual("ud_q4_k_xl", payload["quants"][0]["id"])
        self.assertIn("guidance", payload["quants"][0])

    def test_projector_from_separate_repository(self) -> None:
        model_files = [{"type": "file", "path": "Model-Q4_K_M.gguf", "size": 10}]
        external_files = [{"type": "file", "path": "nested/mmproj-F16.gguf", "size": 99, "lfsOid": "sha256:1"}]

        def resolver(repo: str, rev: str, token: str | None) -> list[dict]:
            self.assertEqual("other/repo", repo)
            return external_files

        projector = resolve_projector(
            {"path": "mmproj-F16.gguf", "repository": "other/repo", "revision": "main"},
            model_repository="model/repo",
            model_revision="abc",
            model_files=model_files,
            token=None,
            resolve_external_files=resolver,
        )
        self.assertEqual("nested/mmproj-F16.gguf", projector["path"])
        self.assertEqual("sha256:1", projector["lfsOid"])

    def test_missing_projector_raises(self) -> None:
        with self.assertRaises(QuantGroupingError) as ctx:
            resolve_projector(
                {"path": "mmproj-F16.gguf"},
                model_repository="model/repo",
                model_revision="abc",
                model_files=[],
                token=None,
                resolve_external_files=lambda *_args: [],
            )
        self.assertEqual("PROJECTOR_NOT_FOUND", ctx.exception.code)

    def test_phase0_fixture_subset_parses(self) -> None:
        fixture_path = CONTRACTS_ROOT / "admin-catalog-get-response.fixture.json"
        with fixture_path.open("r", encoding="utf-8") as handle:
            fixture = json.load(handle)
        self.assertEqual("2026-07-10", fixture["catalogVersion"])
        self.assertEqual("qwen3.6-35b-a3b-mtp", fixture["models"][0]["id"])


if __name__ == "__main__":
    unittest.main()
