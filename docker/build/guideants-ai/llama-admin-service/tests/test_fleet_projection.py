import json
import os
import tempfile
import unittest

from fleet_projection import (
    atomic_write_projection,
    build_projection_document,
    get_fleet_preset_response,
    preset_to_fleet_env,
    put_fleet_preset,
    validate_preset,
)


class FleetProjectionTests(unittest.TestCase):
    def test_validate_preset_rejects_alias_key(self) -> None:
        with self.assertRaises(ValueError):
            validate_preset({"ctx-size": "8192"})

    def test_preset_to_fleet_env_maps_no_mmap(self) -> None:
        env = preset_to_fleet_env({"noMmap": True})
        self.assertEqual(env["GA_LLAMA_NO_MMAP"], "1")

    def test_preset_to_fleet_env_maps_fixture_keys(self) -> None:
        preset = {
            "jinja": True,
            "parallel": 5,
            "threads": 16,
            "kvUnified": True,
            "contBatching": True,
            "flashAttn": "on",
            "modelsMax": 1,
            "noAutoload": True,
        }
        env = preset_to_fleet_env(preset)
        self.assertEqual(env["GA_LLAMA_JINJA"], "1")
        self.assertEqual(env["GA_LLAMA_PARALLEL"], "5")
        self.assertEqual(env["GA_LLAMA_THREADS"], "16")
        self.assertEqual(env["GA_LLAMA_FLASH_ATTN"], "on")
        self.assertEqual(env["GA_LLAMA_NO_AUTOLOAD"], "1")

    def test_atomic_write_projection_is_revisioned(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_LLAMA_FLEET_PROJECTION_DIR"] = tmp
            document = build_projection_document(
                revision=3,
                desired_revision=3,
                applied_revision=2,
                apply_status="pending_restart",
                apply_error=None,
                preset={"parallel": 5, "threads": 16, "jinja": True},
            )
            path = atomic_write_projection(document)
            self.assertTrue(os.path.exists(path))
            loaded = json.load(open(path, "r", encoding="utf-8"))
            self.assertEqual(loaded["revision"], 3)
            self.assertEqual(loaded["desiredRevision"], 3)
            self.assertEqual(loaded["appliedRevision"], 2)
            self.assertIn("fleetEnv", loaded)

    def test_put_fleet_preset_increments_revision(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            os.environ["GA_LLAMA_FLEET_PROJECTION_DIR"] = tmp
            response = put_fleet_preset(
                0,
                {
                    "jinja": True,
                    "parallel": 5,
                    "threads": 16,
                    "kvUnified": True,
                    "contBatching": True,
                    "flashAttn": "on",
                    "modelsMax": 1,
                    "noAutoload": True,
                },
            )
            self.assertEqual(response["desiredRevision"], 1)
            self.assertEqual(response["applyStatus"], "pending_restart")
            current = get_fleet_preset_response()
            self.assertEqual(current["desiredRevision"], 1)


if __name__ == "__main__":
    unittest.main()
