import os
import sys
import tempfile
import unittest
from pathlib import Path

_LIB_ROOT = Path(__file__).resolve().parents[2] / "lib"
if str(_LIB_ROOT) not in sys.path:
    sys.path.insert(0, str(_LIB_ROOT))

from guideants_hf.catalog_completeness import (
    catalog_entry_for_directory_name,
    directory_model_entry_is_complete,
    gguf_model_entry_is_complete,
    has_partial_download_artifacts,
)


class CatalogCompletenessTests(unittest.TestCase):
    def test_has_partial_download_artifacts_detects_tmp_files(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            open(os.path.join(tmp, "model.safetensors.tmp"), "w", encoding="utf-8").close()
            self.assertTrue(has_partial_download_artifacts(tmp))

    def test_directory_model_entry_is_complete_requires_catalog_files(self) -> None:
        entry = {
            "id": "demo",
            "targetDirectory": "demo",
            "requiredFiles": ["config.json", "weights.bin"],
        }
        with tempfile.TemporaryDirectory() as tmp:
            os.makedirs(os.path.join(tmp, "demo"), exist_ok=True)
            model_path = os.path.join(tmp, "demo")
            self.assertFalse(directory_model_entry_is_complete(model_path, entry))

            open(os.path.join(model_path, "config.json"), "w", encoding="utf-8").close()
            self.assertFalse(directory_model_entry_is_complete(model_path, entry))

            open(os.path.join(model_path, "weights.bin"), "w", encoding="utf-8").close()
            self.assertTrue(directory_model_entry_is_complete(model_path, entry))

    def test_catalog_entry_for_directory_name_matches_target_directory(self) -> None:
        entries = {
            "omnivoice": {
                "id": "omnivoice",
                "targetDirectory": "OmniVoice",
                "requiredFiles": ["config.json"],
            }
        }
        matched = catalog_entry_for_directory_name("OmniVoice", entries)
        self.assertIsNotNone(matched)
        assert matched is not None
        self.assertEqual("omnivoice", matched["id"])

    def test_gguf_model_entry_is_complete_rejects_empty_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "model.gguf")
            open(path, "w", encoding="utf-8").close()
            self.assertFalse(gguf_model_entry_is_complete(path))

            with open(path, "wb") as handle:
                handle.write(b"gguf")
            self.assertTrue(gguf_model_entry_is_complete(path))


if __name__ == "__main__":
    unittest.main()
