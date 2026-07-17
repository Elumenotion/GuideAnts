import os
import tempfile
import unittest

import llama_router_ini as router
from guideants_hf.router_mmproj import (
    materialize_router_extras_for_runtime,
    materialize_router_ini_text,
    preset_disables_mmproj,
    resolve_router_runtime_config_path,
)


class RouterMmprojTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmpdir = tempfile.TemporaryDirectory()
        self._ini_dir = self._tmpdir.name

    def tearDown(self) -> None:
        self._tmpdir.cleanup()

    def _ini_path(self, name: str) -> str:
        return os.path.join(self._ini_dir, name)

    def test_preset_disables_mmproj_accepts_boolean_flag(self) -> None:
        self.assertTrue(preset_disables_mmproj({"no-mmproj": ""}))
        self.assertFalse(preset_disables_mmproj({"spec-type": "draft-mtp"}))

    def test_materialize_translates_no_mmproj_to_mmproj_auto_false(self) -> None:
        canonical = (
            "version = 1\n\n"
            "[alias]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = /models-local/llama/a/mmproj-F16.gguf\n"
            "no-mmproj = \n"
            "spec-type = draft-mtp\n"
        )
        runtime = materialize_router_ini_text(
            canonical,
            parse_router_ini=router.parse_router_ini,
            serialize_router_ini_for_runtime=router.serialize_router_ini_for_runtime,
        )

        self.assertIn("mmproj-auto = false", runtime)
        self.assertNotIn("no-mmproj", runtime)
        self.assertNotIn("mmproj =", runtime)

        canonical_entries = router.parse_router_ini(canonical)
        self.assertEqual(
            "/models-local/llama/a/mmproj-F16.gguf",
            canonical_entries["alias"].mmproj,
        )

    def test_materialize_restores_mmproj_when_override_removed(self) -> None:
        canonical = (
            "version = 1\n\n"
            "[alias]\n"
            "model = /models-local/llama/a/model.gguf\n"
            "mmproj = /models-local/llama/a/mmproj-F16.gguf\n"
            "spec-type = draft-mtp\n"
        )
        runtime = materialize_router_ini_text(
            canonical,
            parse_router_ini=router.parse_router_ini,
            serialize_router_ini_for_runtime=router.serialize_router_ini_for_runtime,
        )
        self.assertIn(
            "mmproj = /models-local/llama/a/mmproj-F16.gguf",
            runtime,
        )
        self.assertNotIn("mmproj-auto = false", runtime)

    def test_materialize_router_extras_for_runtime(self) -> None:
        extras = materialize_router_extras_for_runtime(
            {"no-mmproj": "", "spec-type": "draft-mtp"},
        )
        self.assertEqual(extras["mmproj-auto"], "false")
        self.assertEqual(extras["spec-type"], "draft-mtp")
        self.assertNotIn("no-mmproj", extras)

    def test_upsert_writes_runtime_ini_alongside_canonical(self) -> None:
        router.ROUTER_CONFIG_PATH = self._ini_path("canonical.ini")
        runtime_path = resolve_router_runtime_config_path(router.ROUTER_CONFIG_PATH)

        router.upsert_router_entry(
            "alias",
            "/models-local/llama/a/model.gguf",
            "/models-local/llama/a/mmproj-F16.gguf",
            preset={"no-mmproj": "", "spec-type": "draft-mtp"},
            preset_mode="merge",
            trigger_reload=False,
        )

        with open(router.ROUTER_CONFIG_PATH, "r", encoding="utf-8") as handle:
            canonical = handle.read()
        with open(runtime_path, "r", encoding="utf-8") as handle:
            runtime = handle.read()

        self.assertIn("mmproj = /models-local/llama/a/mmproj-F16.gguf", canonical)
        self.assertIn("no-mmproj =", canonical)
        self.assertIn("mmproj-auto = false", runtime)
        self.assertNotIn("no-mmproj", runtime)
        self.assertNotIn("mmproj =", runtime)


if __name__ == "__main__":
    unittest.main()
