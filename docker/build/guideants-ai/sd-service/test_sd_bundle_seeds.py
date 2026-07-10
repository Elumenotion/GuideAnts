import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import sd_bundle_seeds


class SdBundleSeedTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.model_dir = Path(self.temp_dir.name)

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    def test_seed_writes_all_three_default_definitions_on_empty_volume(self) -> None:
        seeded = sd_bundle_seeds.seed_default_bundle_definitions(str(self.model_dir))

        self.assertEqual(
            seeded,
            [
                "FLUX.2-dev-GGUF-Q5_K_M",
                "flux2-klein-4b-q4ks",
                "flux2-klein-9b-q5",
            ],
        )
        for bundle_id in seeded:
            definition_path = self.model_dir / "bundles" / bundle_id / "bundle-definition.json"
            self.assertTrue(definition_path.is_file())
            payload = json.loads(definition_path.read_text(encoding="utf-8"))
            self.assertEqual(payload["bundleId"], bundle_id)
            self.assertIn("roles", payload)
            for role in ("diffusion", "vae", "textEncoder"):
                self.assertIn(role, payload["roles"])
                self.assertTrue(payload["roles"][role]["repo"])
                self.assertTrue(payload["roles"][role]["file"])

    def test_seed_is_idempotent_and_does_not_overwrite_existing_definitions(self) -> None:
        first = sd_bundle_seeds.seed_default_bundle_definitions(str(self.model_dir))
        self.assertEqual(len(first), 3)

        custom_path = self.model_dir / "bundles" / "flux2-klein-9b-q5" / "bundle-definition.json"
        custom_path.write_text(
            json.dumps(
                {
                    "bundleId": "flux2-klein-9b-q5",
                    "revision": "custom",
                    "roles": {
                        "diffusion": {"repo": "custom/diffusion", "file": "custom.gguf"},
                        "vae": {"repo": "custom/vae", "file": "custom.safetensors"},
                        "textEncoder": {"repo": "custom/text", "file": "custom-enc.gguf"},
                    },
                }
            ),
            encoding="utf-8",
        )

        second = sd_bundle_seeds.seed_default_bundle_definitions(str(self.model_dir))
        self.assertEqual(second, [])

        payload = json.loads(custom_path.read_text(encoding="utf-8"))
        self.assertEqual(payload["revision"], "custom")
        self.assertEqual(payload["roles"]["diffusion"]["repo"], "custom/diffusion")


if __name__ == "__main__":
    unittest.main()
