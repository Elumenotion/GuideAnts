import copy
import json
import sys
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator

SERVICE_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = SERVICE_ROOT.parents[3]
LIB_ROOT = SERVICE_ROOT.parent / "lib"
CONTRACTS_ROOT = REPO_ROOT / "docs" / "llama-router-preset-ui-execution" / "contracts"

for path in (str(LIB_ROOT), str(SERVICE_ROOT)):
    if path not in sys.path:
        sys.path.insert(0, path)

import llama_catalog
from llama_catalog import CatalogValidationError, reject_manifest_with_file_arrays, validate_manifest_instance


class LlamaSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        schema_path = Path(llama_catalog.schema_path())
        with schema_path.open("r", encoding="utf-8") as handle:
            cls.schema = json.load(handle)
        Draft202012Validator.check_schema(cls.schema)

        manifest_path = Path(llama_catalog.manifest_path())
        with manifest_path.open("r", encoding="utf-8") as handle:
            cls.manifest = json.load(handle)

    def test_shipped_manifest_validates(self) -> None:
        validate_manifest_instance(self.manifest)
        self.assertEqual(16, len(self.manifest["models"]))

    def test_rejects_file_arrays_on_definition(self) -> None:
        bad = copy.deepcopy(self.manifest["models"][0])
        bad["files"] = ["model.gguf"]
        with self.assertRaises(CatalogValidationError):
            reject_manifest_with_file_arrays(bad)

    def test_rejects_capability_booleans(self) -> None:
        bad = copy.deepcopy(self.manifest)
        bad["capabilities"] = {"vision": True}
        with self.assertRaises(CatalogValidationError):
            reject_manifest_with_file_arrays(bad)

    def test_rejects_duplicate_ids(self) -> None:
        bad = copy.deepcopy(self.manifest)
        bad["models"].append(copy.deepcopy(bad["models"][0]))
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)

    def test_rejects_duplicate_catalog_model_ids(self) -> None:
        bad = copy.deepcopy(self.manifest)
        clone = copy.deepcopy(bad["models"][0])
        clone["id"] = "duplicate-catalog-model-id"
        bad["models"].append(clone)
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)

    def test_rejects_missing_ctx_size(self) -> None:
        bad = copy.deepcopy(self.manifest)
        del bad["models"][0]["defaults"]["routerPreset"]["ctx-size"]
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)

    def test_rejects_vision_without_projector(self) -> None:
        bad = copy.deepcopy(self.manifest)
        text_only = next(m for m in bad["models"] if m["id"] == "gpt-oss-20b")
        text_only["defaults"]["routerPreset"]["image-min-tokens"] = "1024"
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)

    def test_rejects_vision_without_image_min_tokens(self) -> None:
        bad = copy.deepcopy(self.manifest)
        vision = next(m for m in bad["models"] if m["id"] == "qwen3.6-35b-a3b")
        del vision["defaults"]["routerPreset"]["image-min-tokens"]
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)

    def test_mtp_with_projector_requires_image_min_tokens(self) -> None:
        bad = copy.deepcopy(self.manifest)
        mtp = next(m for m in bad["models"] if m["id"] == "qwen3.6-35b-a3b-mtp")
        del mtp["defaults"]["routerPreset"]["image-min-tokens"]
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)

    def test_mtp_without_projector_rejects_image_min_tokens(self) -> None:
        bad = copy.deepcopy(self.manifest)
        text_only = next(m for m in bad["models"] if m["id"] == "gpt-oss-20b")
        text_only["defaults"]["routerPreset"]["spec-type"] = "draft-mtp"
        text_only["defaults"]["routerPreset"]["spec-draft-n-max"] = "2"
        text_only["defaults"]["routerPreset"]["image-min-tokens"] = "1024"
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)


    def test_rejects_companion_mmproj_destination_collision(self) -> None:
        bad = copy.deepcopy(self.manifest)
        muse = next(m for m in bad["models"] if m["id"] == "muse-glimmer-30b")
        muse["defaults"]["companionArtifacts"] = [{"path": muse["defaults"]["mmproj"]["path"]}]
        with self.assertRaises(CatalogValidationError):
            validate_manifest_instance(bad)


if __name__ == "__main__":
    unittest.main()
