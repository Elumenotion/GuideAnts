import json
import os
import tempfile
import threading
import unittest
from unittest import mock

from support import CONTRACTS_ROOT

import llama_router_ini as router


class RouterPresetTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory()
        router.ROUTER_CONFIG_PATH = os.path.join(self._tmpdir.name, "router-models.ini")

    def tearDown(self) -> None:
        self._tmpdir.cleanup()

    def test_parse_and_serialize_round_trip_with_preset(self) -> None:
        text = (
            "version = 1\n\n"
            "[alias-a]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = \n"
            "ctx-size = 131072\n"
            "spec-type = draft-mtp\n"
        )
        parsed = router.parse_router_ini(text)
        self.assertIn("alias-a", parsed)
        self.assertEqual(parsed["alias-a"].extras["ctx-size"], "131072")
        serialized = router.serialize_router_ini(parsed)
        reparsed = router.parse_router_ini(serialized)
        self.assertEqual(reparsed["alias-a"].extras, parsed["alias-a"].extras)

    def test_replace_mode_replaces_prior_extras(self) -> None:
        with open(router.ROUTER_CONFIG_PATH, "w", encoding="utf-8") as handle:
            handle.write(
                "version = 1\n\n[alias]\nmodel = /m/a.gguf\nmmproj = \nctx-size = 4096\ncache-ram = 1024\n"
            )
        router.upsert_router_entry(
            "alias",
            "/m/a.gguf",
            "",
            preset={"ctx-size": "131072", "spec-type": "draft-mtp"},
            preset_mode="replace",
            trigger_reload=False,
        )
        entries = router.read_router_entries()
        self.assertEqual(entries["alias"].extras, {"ctx-size": "131072", "spec-type": "draft-mtp"})

    def test_merge_mode_preserves_unmentioned_extras(self) -> None:
        with open(router.ROUTER_CONFIG_PATH, "w", encoding="utf-8") as handle:
            handle.write(
                "version = 1\n\n[alias]\nmodel = /m/a.gguf\nmmproj = \nctx-size = 4096\ncache-ram = 1024\n"
            )
        router.upsert_router_entry(
            "alias",
            "/m/a.gguf",
            "",
            preset={"ctx-size": "8192"},
            preset_mode="merge",
            trigger_reload=False,
        )
        entries = router.read_router_entries()
        self.assertEqual(entries["alias"].extras["ctx-size"], "8192")
        self.assertEqual(entries["alias"].extras["cache-ram"], "1024")

    def test_rejects_infrastructure_keys_in_preset(self) -> None:
        from guideants_hf.preset_validation import PresetValidationError, normalize_preset_map

        with self.assertRaises(PresetValidationError):
            normalize_preset_map({"model": "/bad"})

    def test_rejects_fleet_keys_in_preset(self) -> None:
        from guideants_hf.preset_validation import PresetValidationError, normalize_preset_map

        with self.assertRaises(PresetValidationError):
            normalize_preset_map({"parallel": "4"})

    def test_fixture_preset_shape(self) -> None:
        fixture = json.loads((CONTRACTS_ROOT / "admin-router-entries-post-request.fixture.json").read_text(encoding="utf-8"))
        preset = router.normalize_preset_map(fixture["preset"])
        router.upsert_router_entry(
            fixture["alias"],
            fixture["modelPath"],
            fixture["mmprojPath"],
            preset=preset,
            preset_mode=fixture["presetMode"],
            trigger_reload=False,
        )
        entries = router.read_router_entries()
        self.assertEqual(entries[fixture["alias"]].extras, preset)

    def test_reload_failure_surfaces_runtime_apply(self) -> None:
        with open(router.ROUTER_CONFIG_PATH, "w", encoding="utf-8") as handle:
            handle.write("version = 1\n\n[alias]\nmodel = /m/a.gguf\nmmproj = \n")
        _, runtime_apply = router.upsert_router_entry(
            "alias",
            "/m/a.gguf",
            "",
            preset={"ctx-size": "4096"},
            preset_mode="replace",
            reload_callback=lambda: router.RuntimeApplyResult(False, "deadbeef", "reload failed"),
        )
        self.assertIsNotNone(runtime_apply)
        assert runtime_apply is not None
        self.assertFalse(runtime_apply.applied)
        self.assertEqual(runtime_apply.ini_sha256, "deadbeef")

    def test_concurrent_ini_writes_are_serialized(self) -> None:
        barrier = threading.Barrier(2)

        def writer(preset_value: str) -> None:
            barrier.wait()
            router.upsert_router_entry(
                "alias",
                "/m/a.gguf",
                "",
                preset={"ctx-size": preset_value},
                preset_mode="replace",
                trigger_reload=False,
            )

        t1 = threading.Thread(target=writer, args=("1111",))
        t2 = threading.Thread(target=writer, args=("2222",))
        t1.start()
        t2.start()
        t1.join()
        t2.join()
        entries = router.read_router_entries()
        self.assertIn(entries["alias"].extras["ctx-size"], {"1111", "2222"})


if __name__ == "__main__":
    unittest.main()
