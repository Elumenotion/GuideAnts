import sys
import unittest
from pathlib import Path

_SERVICE_ROOT = Path(__file__).resolve().parents[1]
if str(_SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVICE_ROOT))

from warmup_engine_client import _aux_load_body, post_aux_load


class WarmupEngineClientTests(unittest.TestCase):
    def test_aux_load_body_uses_model_path_by_default(self) -> None:
        self.assertEqual(_aux_load_body("OmniVoice"), {"model_path": "OmniVoice"})

    def test_aux_load_body_legacy_model_id_field(self) -> None:
        self.assertEqual(
            _aux_load_body("omnivoice", load_field="model_id"),
            {"model_id": "omnivoice"},
        )

    def test_gguf_always_uses_model_path(self) -> None:
        self.assertEqual(_aux_load_body("weights.gguf"), {"model_path": "weights.gguf"})

    def test_image_generation_without_bundle_ref_returns_false(self) -> None:
        from unittest.mock import patch

        with patch("warmup_engine_client._post_json") as post_json:
            self.assertFalse(post_aux_load("ImageGeneration", None))
            self.assertFalse(post_aux_load("ImageGeneration", "  "))
            post_json.assert_not_called()


if __name__ == "__main__":
    unittest.main()
